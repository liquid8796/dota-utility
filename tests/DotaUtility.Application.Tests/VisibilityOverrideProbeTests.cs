using DotaUtility.Application;
using DotaUtility.Domain;

namespace DotaUtility.Application.Tests;

public sealed class VisibilityOverrideProbeTests
{
    [Fact]
    public async Task FreshLocalEngineSignal_OverridesMemoryStateAndClearsRawMask()
    {
        var observedAt = DateTimeOffset.UtcNow;
        var memorySnapshot = CreateSnapshot(
            VisibilityState.Hidden,
            rawMask: 0,
            heroAddress: 0x1234,
            localPlayerId: 4);
        var probe = new VisibilityOverrideProbe(
            new FixedProbe(memorySnapshot),
            new FixedOverrideProvider(
                hasEverReceived: true,
                new VisibilityOverrideSignal(
                    true,
                    observedAt,
                    PlayerId: 4,
                    VisibilityOverrideSource.LocalEngine)),
            TimeSpan.FromSeconds(2));

        var actual = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(VisibilityState.Visible, actual.State);
        Assert.Equal(observedAt, actual.ObservedAt);
        Assert.Null(actual.RawVisibilityMask);
        Assert.Equal(memorySnapshot.HeroAddress, actual.HeroAddress);
        Assert.Contains("engine FoW bridge", actual.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FreshRemoteRelaySignal_UsesRemoteRelayDetail()
    {
        var memorySnapshot = CreateSnapshot(
            VisibilityState.Hidden,
            rawMask: 0,
            heroAddress: 0x1234,
            localPlayerId: 6);
        var probe = new VisibilityOverrideProbe(
            new FixedProbe(memorySnapshot),
            new FixedOverrideProvider(
                hasEverReceived: true,
                new VisibilityOverrideSignal(
                    true,
                    DateTimeOffset.UtcNow,
                    PlayerId: 6,
                    VisibilityOverrideSource.RemoteHostRelay)),
            TimeSpan.FromSeconds(2));

        var actual = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(VisibilityState.Visible, actual.State);
        Assert.Contains("remote host FoW relay", actual.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StaleBridgeAfterFirstHeartbeat_ReturnsUnknownInsteadOfMemoryFallback()
    {
        var memorySnapshot = CreateSnapshot(
            VisibilityState.Hidden,
            rawMask: 0,
            heroAddress: 0x1234,
            localPlayerId: 2);
        var probe = new VisibilityOverrideProbe(
            new FixedProbe(memorySnapshot),
            new FixedOverrideProvider(hasEverReceived: true, signal: null),
            TimeSpan.FromSeconds(2),
            allowMemoryFallback: false);

        var actual = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(VisibilityState.Unknown, actual.State);
        Assert.Null(actual.RawVisibilityMask);
        Assert.Contains("stale", actual.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NoBridgeHeartbeatYet_UsesMemoryProbeWhenFallbackEnabled()
    {
        var memorySnapshot = CreateSnapshot(
            VisibilityState.Hidden,
            rawMask: 0x0A,
            heroAddress: 0x1234,
            localPlayerId: 1);
        var probe = new VisibilityOverrideProbe(
            new FixedProbe(memorySnapshot),
            new FixedOverrideProvider(hasEverReceived: false, signal: null),
            TimeSpan.FromSeconds(2),
            allowMemoryFallback: true);

        var actual = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(memorySnapshot, actual);
    }

    [Fact]
    public async Task NoBridgeHeartbeatYet_ReturnsUnknownWhenAuthoritativeBridgeRequired()
    {
        var memorySnapshot = CreateSnapshot(
            VisibilityState.Hidden,
            rawMask: 0,
            heroAddress: 0x1234,
            localPlayerId: 5);
        var probe = new VisibilityOverrideProbe(
            new FixedProbe(memorySnapshot),
            new FixedOverrideProvider(hasEverReceived: false, signal: null),
            TimeSpan.FromSeconds(2),
            allowMemoryFallback: false);

        var actual = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(VisibilityState.Unknown, actual.State);
        Assert.Null(actual.RawVisibilityMask);
        Assert.Contains("host relay", actual.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FreshBridgeWithoutResolvedHero_StaysUnknown()
    {
        var memorySnapshot = new VisibilitySnapshot(
            VisibilityState.Unknown,
            DateTimeOffset.UtcNow,
            IsAttached: true,
            ProcessId: 4242,
            LocalTeam: null,
            EnemyTeam: null,
            RawVisibilityMask: null,
            Detail: "Local hero pointer unavailable.",
            HeroAddress: null,
            LocalPlayerId: 0);
        var probe = new VisibilityOverrideProbe(
            new FixedProbe(memorySnapshot),
            new FixedOverrideProvider(
                hasEverReceived: true,
                new VisibilityOverrideSignal(
                    true,
                    DateTimeOffset.UtcNow,
                    PlayerId: 0)),
            TimeSpan.FromSeconds(2));

        var actual = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(VisibilityState.Unknown, actual.State);
        Assert.Null(actual.RawVisibilityMask);
        Assert.Contains("waiting for the local hero pointer", actual.Detail, StringComparison.OrdinalIgnoreCase);
    }

    private static VisibilitySnapshot CreateSnapshot(
        VisibilityState state,
        int rawMask,
        ulong heroAddress,
        int localPlayerId) =>
        new(
            state,
            DateTimeOffset.UtcNow,
            IsAttached: true,
            ProcessId: 4242,
            LocalTeam: 2,
            EnemyTeam: 3,
            RawVisibilityMask: rawMask,
            Detail: "Memory probe.",
            HeroAddress: heroAddress,
            LocalPlayerId: localPlayerId);

    private sealed class FixedProbe(VisibilitySnapshot snapshot) : IVisibilityProbe
    {
        public Task<VisibilitySnapshot> ProbeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(snapshot);
    }

    private sealed class FixedOverrideProvider(
        bool hasEverReceived,
        VisibilityOverrideSignal? signal) : IVisibilityOverrideProvider
    {
        public bool HasEverReceived(int? playerId)
        {
            if (!hasEverReceived)
            {
                return false;
            }

            return signal is not VisibilityOverrideSignal current
                || current.PlayerId == playerId;
        }

        public bool TryGetFresh(
            int? playerId,
            TimeSpan maximumAge,
            out VisibilityOverrideSignal value)
        {
            if (signal is VisibilityOverrideSignal current
                && current.PlayerId == playerId)
            {
                value = current;
                return true;
            }

            value = default;
            return false;
        }
    }
}
