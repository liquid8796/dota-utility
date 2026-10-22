using System.Net;
using System.Net.Sockets;
using System.Text;
using DotaUtility.App.Services;
using DotaUtility.Application;

namespace DotaUtility.App.Tests;

public sealed class VisibilityRelayTests
{
    [Fact]
    public async Task HostRelay_ExportsFreshSignalsForAllPlayers()
    {
        await using var bridge = new LocalLobbyVisibilityBridge(port: 0);
        bridge.Start();
        await using var host = new VisibilityRelayHost(
            bridge,
            relayPort: 0,
            enableDiscovery: false);
        host.Start();

        await LocalLobbyVisibilityBridgeTests.SendSignalAsync(
            bridge.Port,
            playerId: 1,
            visible: true);
        await LocalLobbyVisibilityBridgeTests.SendSignalAsync(
            bridge.Port,
            playerId: 7,
            visible: false);

        var body = await GetRelayBodyAsync(host.RelayPort);

        Assert.Contains("1=1", body, StringComparison.Ordinal);
        Assert.Contains("7=0", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RelayClient_UsesManualHostAndSelectsRequestedPlayer()
    {
        await using var bridge = new LocalLobbyVisibilityBridge(port: 0);
        bridge.Start();
        await using var host = new VisibilityRelayHost(
            bridge,
            relayPort: 0,
            enableDiscovery: false);
        host.Start();

        await LocalLobbyVisibilityBridgeTests.SendSignalAsync(
            bridge.Port,
            playerId: 3,
            visible: true);
        await LocalLobbyVisibilityBridgeTests.SendSignalAsync(
            bridge.Port,
            playerId: 8,
            visible: false);

        await using var client = new VisibilityRelayClient(
            manualHost: $"127.0.0.1:{host.RelayPort}",
            enableDiscovery: false);
        client.Start();

        var received = await WaitUntilAsync(
            () => client.TryGetFresh(
                8,
                TimeSpan.FromSeconds(1),
                out _),
            TimeSpan.FromSeconds(3));

        Assert.True(received);
        Assert.True(client.TryGetFresh(
            3,
            TimeSpan.FromSeconds(1),
            out var playerThree));
        Assert.True(client.TryGetFresh(
            8,
            TimeSpan.FromSeconds(1),
            out var playerEight));

        Assert.True(playerThree.IsVisible);
        Assert.False(playerEight.IsVisible);
        Assert.Equal(VisibilityOverrideSource.RemoteHostRelay, playerThree.Source);
        Assert.Equal(VisibilityOverrideSource.RemoteHostRelay, playerEight.Source);
    }

    private static async Task<string> GetRelayBodyAsync(int port)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await using var stream = client.GetStream();
        var request = Encoding.ASCII.GetBytes(
            "GET /dota-utility/relay/all HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(request);
        await stream.FlushAsync();

        using var reader = new StreamReader(stream, Encoding.ASCII);
        var response = await reader.ReadToEndAsync();
        var separator = response.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        return separator >= 0 ? response[(separator + 4)..] : response;
    }

    private static async Task<bool> WaitUntilAsync(
        Func<bool> predicate,
        TimeSpan timeout)
    {
        var stopAt = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < stopAt)
        {
            if (predicate())
            {
                return true;
            }

            await Task.Delay(50);
        }

        return predicate();
    }
}
