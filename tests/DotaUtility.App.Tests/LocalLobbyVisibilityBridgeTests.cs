using System.Net;
using System.Net.Sockets;
using System.Text;
using DotaUtility.App.Services;
using DotaUtility.Application;

namespace DotaUtility.App.Tests;

public sealed class LocalLobbyVisibilityBridgeTests
{
    [Fact]
    public async Task PlayerSpecificRequests_UpdateIndependentSignals()
    {
        await using var bridge = new LocalLobbyVisibilityBridge(port: 0);
        bridge.Start();

        await SendSignalAsync(bridge.Port, playerId: 0, visible: true);
        await SendSignalAsync(bridge.Port, playerId: 5, visible: false);

        Assert.True(bridge.HasEverReceived(0));
        Assert.True(bridge.HasEverReceived(5));
        Assert.True(bridge.TryGetFresh(0, TimeSpan.FromSeconds(1), out var playerZero));
        Assert.True(bridge.TryGetFresh(5, TimeSpan.FromSeconds(1), out var playerFive));

        Assert.True(playerZero.IsVisible);
        Assert.False(playerFive.IsVisible);
        Assert.Equal(VisibilityOverrideSource.LocalEngine, playerZero.Source);
        Assert.Equal(VisibilityOverrideSource.LocalEngine, playerFive.Source);
    }

    [Fact]
    public async Task InvalidRequest_DoesNotPublishSignal()
    {
        await using var bridge = new LocalLobbyVisibilityBridge(port: 0);
        bridge.Start();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, bridge.Port);
        await using var stream = client.GetStream();
        var request = Encoding.ASCII.GetBytes(
            "GET /wrong-path?visible=1&player=0 HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(request);
        await stream.FlushAsync();

        using var reader = new StreamReader(stream, Encoding.ASCII);
        var statusLine = await reader.ReadLineAsync();

        Assert.Equal("HTTP/1.1 400 Bad Request", statusLine);
        Assert.False(bridge.HasEverReceived(0));
        Assert.False(bridge.TryGetFresh(0, TimeSpan.FromSeconds(1), out _));
    }

    internal static async Task SendSignalAsync(
        int port,
        int playerId,
        bool visible)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await using var stream = client.GetStream();
        var request = Encoding.ASCII.GetBytes(
            $"GET /dota-utility/vision?visible={(visible ? 1 : 0)}&player={playerId} HTTP/1.1\r\n" +
            "Host: 127.0.0.1\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(request);
        await stream.FlushAsync();

        using var reader = new StreamReader(stream, Encoding.ASCII);
        var statusLine = await reader.ReadLineAsync();
        Assert.Equal("HTTP/1.1 204 No Content", statusLine);
    }
}
