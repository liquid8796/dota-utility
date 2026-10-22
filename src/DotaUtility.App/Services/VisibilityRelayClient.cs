using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using DotaUtility.Application;

namespace DotaUtility.App.Services;

public sealed class VisibilityRelayClient : IVisibilityOverrideProvider, IAsyncDisposable
{
    public const string ManualHostEnvironmentVariable = "DOTAUTILITY_RELAY_HOST";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(1);

    private readonly object _gate = new();
    private readonly Dictionary<int, VisibilityOverrideSignal> _signals = new();
    private readonly HashSet<int> _everReceivedPlayers = new();
    private readonly int _defaultRelayPort;
    private readonly int _discoveryPort;
    private readonly bool _enableDiscovery;
    private readonly HttpClient _httpClient;

    private readonly RelayEndpoint? _manualEndpoint;
    private RelayEndpoint? _discoveredEndpoint;
    private UdpClient? _discoverySocket;
    private CancellationTokenSource? _cancellation;
    private Task? _discoveryWorker;
    private Task? _pollWorker;
    private bool _disposed;

    public VisibilityRelayClient(
        string? manualHost = null,
        int defaultRelayPort = VisibilityRelayHost.DefaultRelayPort,
        int discoveryPort = VisibilityRelayHost.DefaultDiscoveryPort,
        bool enableDiscovery = true)
    {
        if (defaultRelayPort is < 1 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(defaultRelayPort));
        }

        if (discoveryPort is < 1 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(discoveryPort));
        }

        _defaultRelayPort = defaultRelayPort;
        _discoveryPort = discoveryPort;
        _enableDiscovery = enableDiscovery;
        _manualEndpoint = TryParseEndpoint(
            manualHost ?? Environment.GetEnvironmentVariable(ManualHostEnvironmentVariable),
            defaultRelayPort);

        _httpClient = new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
        })
        {
            Timeout = RequestTimeout,
        };
    }

    public string? CurrentEndpoint
    {
        get
        {
            lock (_gate)
            {
                return (_manualEndpoint ?? _discoveredEndpoint)?.ToString();
            }
        }
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_cancellation is not null)
        {
            return;
        }

        _cancellation = new CancellationTokenSource();

        if (_enableDiscovery && _manualEndpoint is null)
        {
            _discoverySocket = new UdpClient(AddressFamily.InterNetwork);
            _discoverySocket.Client.SetSocketOption(
                SocketOptionLevel.Socket,
                SocketOptionName.ReuseAddress,
                true);
            _discoverySocket.Client.Bind(
                new IPEndPoint(IPAddress.Any, _discoveryPort));

            _discoveryWorker = Task.Run(
                () => DiscoveryLoopAsync(_cancellation.Token),
                CancellationToken.None);
        }

        _pollWorker = Task.Run(
            () => PollLoopAsync(_cancellation.Token),
            CancellationToken.None);
    }

    public bool HasEverReceived(int? playerId)
    {
        if (playerId is not int value)
        {
            return false;
        }

        lock (_gate)
        {
            return _everReceivedPlayers.Contains(value);
        }
    }

    public bool TryGetFresh(
        int? playerId,
        TimeSpan maximumAge,
        out VisibilityOverrideSignal signal)
    {
        if (maximumAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAge), maximumAge, "Freshness window must be positive.");
        }

        if (playerId is not int value)
        {
            signal = default;
            return false;
        }

        lock (_gate)
        {
            if (!_signals.TryGetValue(value, out var current)
                || DateTimeOffset.UtcNow - current.ObservedAt > maximumAge)
            {
                signal = default;
                return false;
            }

            signal = current;
            return true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var cancellation = _cancellation;
        _cancellation = null;

        cancellation?.Cancel();
        _discoverySocket?.Dispose();

        foreach (var worker in new[] { _discoveryWorker, _pollWorker })
        {
            if (worker is null)
            {
                continue;
            }

            try
            {
                await worker.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (SocketException)
            {
            }
        }

        _httpClient.Dispose();
        cancellation?.Dispose();
    }

    private async Task DiscoveryLoopAsync(CancellationToken cancellationToken)
    {
        var socket = _discoverySocket
            ?? throw new InvalidOperationException("Discovery socket was not initialized.");

        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await socket.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var text = Encoding.ASCII.GetString(result.Buffer);
            if (!TryParseDiscovery(text, result.RemoteEndPoint.Address, out var endpoint))
            {
                continue;
            }

            lock (_gate)
            {
                _discoveredEndpoint = endpoint;
            }
        }
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            RelayEndpoint? endpoint;
            lock (_gate)
            {
                endpoint = _manualEndpoint ?? _discoveredEndpoint;
            }

            if (endpoint is not null)
            {
                await PollEndpointAsync(endpoint.Value, cancellationToken).ConfigureAwait(false);
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PollEndpointAsync(
        RelayEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        string body;
        try
        {
            var uri = new Uri(
                $"http://{endpoint.Host}:{endpoint.Port}/dota-utility/relay/all",
                UriKind.Absolute);
            body = await _httpClient.GetStringAsync(uri, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var parsed = new List<VisibilityOverrideSignal>();
        foreach (var line in body.Split(
                     new[] { '\r', '\n' },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0
                || !int.TryParse(line[..separator], out var playerId)
                || playerId is < 0 or > 63)
            {
                continue;
            }

            var visibleText = line[(separator + 1)..];
            if (visibleText is not ("0" or "1"))
            {
                continue;
            }

            parsed.Add(new VisibilityOverrideSignal(
                visibleText == "1",
                now,
                playerId,
                VisibilityOverrideSource.RemoteHostRelay));
        }

        if (parsed.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            foreach (var signal in parsed)
            {
                if (signal.PlayerId is not int playerId)
                {
                    continue;
                }

                _signals[playerId] = signal;
                _everReceivedPlayers.Add(playerId);
            }
        }
    }

    private bool TryParseDiscovery(
        string packet,
        IPAddress remoteAddress,
        out RelayEndpoint endpoint)
    {
        endpoint = default;

        const string prefix = "DOTAUTILITY_RELAY_V1|";
        if (!packet.StartsWith(prefix, StringComparison.Ordinal)
            || !int.TryParse(packet[prefix.Length..], out var port)
            || port is < 1 or > ushort.MaxValue)
        {
            return false;
        }

        endpoint = new RelayEndpoint(remoteAddress.ToString(), port);
        return true;
    }

    private static RelayEndpoint? TryParseEndpoint(
        string? value,
        int defaultPort)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return new RelayEndpoint(
                uri.Host,
                uri.IsDefaultPort ? defaultPort : uri.Port);
        }

        if (trimmed.Count(character => character == ':') == 1)
        {
            var separator = trimmed.LastIndexOf(':');
            var host = trimmed[..separator];
            if (int.TryParse(trimmed[(separator + 1)..], out var port)
                && port is >= 1 and <= ushort.MaxValue
                && host.Length > 0)
            {
                return new RelayEndpoint(host, port);
            }
        }

        return new RelayEndpoint(trimmed, defaultPort);
    }

    private readonly record struct RelayEndpoint(string Host, int Port)
    {
        public override string ToString() => $"{Host}:{Port}";
    }
}
