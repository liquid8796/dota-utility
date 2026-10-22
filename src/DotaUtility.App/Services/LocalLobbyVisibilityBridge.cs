using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DotaUtility.Application;

namespace DotaUtility.App.Services;

public sealed class LocalLobbyVisibilityBridge : IVisibilityOverrideProvider, IAsyncDisposable
{
    public const int DefaultPort = 39077;

    private readonly TcpListener _listener;
    private readonly Func<bool>? _isEnemyMinimapEnabled;
    private readonly object _gate = new();
    private readonly Dictionary<int, VisibilityOverrideSignal> _signals = new();
    private readonly HashSet<int> _everReceivedPlayers = new();

    private CancellationTokenSource? _cancellation;
    private Task? _worker;
    private bool _disposed;

    public LocalLobbyVisibilityBridge(
        int port = DefaultPort,
        Func<bool>? isEnemyMinimapEnabled = null)
    {
        if (port is < 0 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        _listener = new TcpListener(IPAddress.Loopback, port);
        _isEnemyMinimapEnabled = isEnemyMinimapEnabled;
    }

    public event Action<VisibilityOverrideSignal>? SignalReceived;

    public int Port { get; private set; }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            if (_worker is { IsCompleted: false })
            {
                return;
            }

            _listener.Start(backlog: 8);
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _cancellation = new CancellationTokenSource();
            _worker = Task.Run(() => AcceptLoopAsync(_cancellation.Token), CancellationToken.None);
        }
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

    public IReadOnlyList<VisibilityOverrideSignal> GetFreshSignals(TimeSpan maximumAge)
    {
        if (maximumAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAge), maximumAge, "Freshness window must be positive.");
        }

        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            return _signals.Values
                .Where(signal => now - signal.ObservedAt <= maximumAge)
                .OrderBy(signal => signal.PlayerId)
                .ToArray();
        }
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? cancellation;
        Task? worker;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            cancellation = _cancellation;
            worker = _worker;
            _cancellation = null;
            _worker = null;
        }

        cancellation?.Cancel();
        _listener.Stop();

        try
        {
            if (worker is not null)
            {
                await worker.ConfigureAwait(false);
            }
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
        finally
        {
            cancellation?.Dispose();
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            await HandleClientAsync(client, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
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

            var isParsed = TryParseSignal(requestLine, out var signal);
            if (signal is VisibilityOverrideSignal value && value.PlayerId is int playerId)
            {
                lock (_gate)
                {
                    _signals[playerId] = value;
                    _everReceivedPlayers.Add(playerId);
                }

                SignalReceived?.Invoke(value);
            }

            var minimapEnabled = false;
            try
            {
                minimapEnabled = _isEnemyMinimapEnabled?.Invoke() == true;
            }
            catch
            {
                minimapEnabled = false;
            }

            string response;
            if (!isParsed)
            {
                response = "HTTP/1.1 400 Bad Request\r\nConnection: close\r\nContent-Length: 0\r\nCache-Control: no-store\r\n\r\n";
            }
            else if (minimapEnabled)
            {
                const string body = "minimap=1";
                response = $"HTTP/1.1 200 OK\r\nConnection: close\r\nContent-Type: text/plain\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\n\r\n{body}";
            }
            else
            {
                response = "HTTP/1.1 204 No Content\r\nConnection: close\r\nContent-Length: 0\r\nCache-Control: no-store\r\n\r\n";
            }
            var bytes = Encoding.ASCII.GetBytes(response);
            try
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
            }
        }
    }

    private static bool TryParseSignal(string? requestLine, out VisibilityOverrideSignal? signal)
    {
        signal = null;
        if (string.IsNullOrWhiteSpace(requestLine))
        {
            return false;
        }

        var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !string.Equals(parts[0], "GET", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!Uri.TryCreate($"http://127.0.0.1{parts[1]}", UriKind.Absolute, out var uri)
            || !string.Equals(uri.AbsolutePath, "/dota-utility/vision", StringComparison.Ordinal))
        {
            return false;
        }

        var query = ParseQuery(uri.Query);
        if (!query.TryGetValue("visible", out var visibleText)
            || !TryParseBoolean(visibleText, out var visible)
            || !query.TryGetValue("player", out var playerText)
            || !int.TryParse(playerText, out var playerId)
            || playerId is < 0 or > 63)
        {
            return false;
        }

        var inSentry = query.TryGetValue("sentry", out var sentryText)
            && TryParseBoolean(sentryText, out var parsedSentry)
            && parsedSentry;

        signal = new VisibilityOverrideSignal(
            visible,
            DateTimeOffset.UtcNow,
            playerId,
            VisibilityOverrideSource.LocalEngine,
            IsInEnemySentry: inSentry);
        return true;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var trimmed = query.TrimStart('?');
        if (trimmed.Length == 0)
        {
            return result;
        }

        foreach (var pair in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator < 0)
            {
                result[Uri.UnescapeDataString(pair)] = string.Empty;
                continue;
            }

            var key = Uri.UnescapeDataString(pair[..separator]);
            var value = Uri.UnescapeDataString(pair[(separator + 1)..]);
            result[key] = value;
        }

        return result;
    }

    private static bool TryParseBoolean(string value, out bool result)
    {
        if (value is "1")
        {
            result = true;
            return true;
        }

        if (value is "0")
        {
            result = false;
            return true;
        }

        return bool.TryParse(value, out result);
    }
}
