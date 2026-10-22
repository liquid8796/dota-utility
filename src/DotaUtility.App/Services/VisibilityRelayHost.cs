using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DotaUtility.App.Services;

public sealed class VisibilityRelayHost : IAsyncDisposable
{
    public const int DefaultRelayPort = 39078;
    public const int DefaultDiscoveryPort = 39079;

    private static readonly TimeSpan RelayFreshness = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DiscoveryInterval = TimeSpan.FromSeconds(1);

    private readonly LocalLobbyVisibilityBridge _localBridge;
    private readonly TcpListener _relayListener;
    private readonly int _discoveryPort;
    private readonly bool _enableDiscovery;

    private CancellationTokenSource? _cancellation;
    private Task? _acceptWorker;
    private Task? _broadcastWorker;
    private bool _disposed;

    public VisibilityRelayHost(
        LocalLobbyVisibilityBridge localBridge,
        int relayPort = DefaultRelayPort,
        int discoveryPort = DefaultDiscoveryPort,
        bool enableDiscovery = true)
    {
        _localBridge = localBridge ?? throw new ArgumentNullException(nameof(localBridge));
        if (relayPort is < 0 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(relayPort));
        }

        if (discoveryPort is < 1 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(discoveryPort));
        }

        _relayListener = new TcpListener(IPAddress.Any, relayPort);
        _discoveryPort = discoveryPort;
        _enableDiscovery = enableDiscovery;
    }

    public int RelayPort { get; private set; }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_cancellation is not null)
        {
            return;
        }

        _relayListener.Start(backlog: 8);
        RelayPort = ((IPEndPoint)_relayListener.LocalEndpoint).Port;

        _cancellation = new CancellationTokenSource();
        _acceptWorker = Task.Run(
            () => AcceptLoopAsync(_cancellation.Token),
            CancellationToken.None);

        if (_enableDiscovery)
        {
            _broadcastWorker = Task.Run(
                () => BroadcastLoopAsync(_cancellation.Token),
                CancellationToken.None);
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
        _relayListener.Stop();

        var workers = new[] { _acceptWorker, _broadcastWorker }
            .Where(worker => worker is not null)
            .Cast<Task>()
            .ToArray();

        foreach (var worker in workers)
        {
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

        cancellation?.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _relayListener
                    .AcceptTcpClientAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            await HandleClientAsync(client, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleClientAsync(
        TcpClient client,
        CancellationToken cancellationToken)
    {
        using (client)
        await using (var stream = client.GetStream())
        using (var reader = new StreamReader(
            stream,
            Encoding.ASCII,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: true))
        {
            string? requestLine;
            try
            {
                requestLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                return;
            }

            var requestPath = ParseRequestPath(requestLine);
            if (!string.Equals(
                    requestPath,
                    "/dota-utility/relay/all",
                    StringComparison.Ordinal))
            {
                await WriteResponseAsync(
                    stream,
                    "404 Not Found",
                    string.Empty,
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var body = BuildRelayBody(_localBridge.GetFreshSignals(RelayFreshness));
            await WriteResponseAsync(
                stream,
                "200 OK",
                body,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task BroadcastLoopAsync(CancellationToken cancellationToken)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork)
        {
            EnableBroadcast = true,
        };

        var endpoint = new IPEndPoint(IPAddress.Broadcast, _discoveryPort);

        while (!cancellationToken.IsCancellationRequested)
        {
            if (_localBridge.GetFreshSignals(RelayFreshness).Count > 0)
            {
                var packet = Encoding.ASCII.GetBytes(
                    $"DOTAUTILITY_RELAY_V1|{RelayPort}");
                try
                {
                    await udp.SendAsync(packet, endpoint, cancellationToken).ConfigureAwait(false);
                }
                catch (SocketException)
                {
                    // Firewall/network policy may block LAN discovery.
                }
            }

            await Task.Delay(DiscoveryInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string? ParseRequestPath(string? requestLine)
    {
        if (string.IsNullOrWhiteSpace(requestLine))
        {
            return null;
        }

        var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2
            || !string.Equals(parts[0], "GET", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Uri.TryCreate(
            $"http://127.0.0.1{parts[1]}",
            UriKind.Absolute,
            out var uri)
            ? uri.AbsolutePath
            : null;
    }

    private static string BuildRelayBody(
        IReadOnlyList<Application.VisibilityOverrideSignal> signals)
    {
        var builder = new StringBuilder();
        foreach (var signal in signals)
        {
            if (signal.PlayerId is not int playerId)
            {
                continue;
            }

            builder
                .Append(playerId)
                .Append('=')
                .Append(signal.IsVisible ? '1' : '0')
                .Append('\n');
        }

        return builder.ToString();
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream,
        string status,
        string body,
        CancellationToken cancellationToken)
    {
        var bodyBytes = Encoding.ASCII.GetBytes(body);
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\n" +
            "Connection: close\r\n" +
            "Content-Type: text/plain; charset=us-ascii\r\n" +
            $"Content-Length: {bodyBytes.Length}\r\n" +
            "Cache-Control: no-store\r\n\r\n");

        try
        {
            await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            if (bodyBytes.Length > 0)
            {
                await stream.WriteAsync(bodyBytes, cancellationToken).ConfigureAwait(false);
            }

            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
        }
    }
}
