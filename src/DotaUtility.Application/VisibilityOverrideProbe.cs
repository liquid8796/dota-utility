using DotaUtility.Domain;

namespace DotaUtility.Application;

public sealed class VisibilityOverrideProbe : IVisibilityProbe
{
    private readonly IVisibilityProbe _inner;
    private readonly IVisibilityOverrideProvider _overrideProvider;
    private readonly TimeSpan _maximumAge;
    private readonly bool _allowMemoryFallback;

    public VisibilityOverrideProbe(
        IVisibilityProbe inner,
        IVisibilityOverrideProvider overrideProvider,
        TimeSpan maximumAge,
        bool allowMemoryFallback = true)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _overrideProvider = overrideProvider ?? throw new ArgumentNullException(nameof(overrideProvider));
        if (maximumAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAge), maximumAge, "Freshness window must be positive.");
        }

        _maximumAge = maximumAge;
        _allowMemoryFallback = allowMemoryFallback;
    }

    public async Task<VisibilitySnapshot> ProbeAsync(CancellationToken cancellationToken)
    {
        var snapshot = await _inner.ProbeAsync(cancellationToken).ConfigureAwait(false);
        var localPlayerId = snapshot.LocalPlayerId;

        if (_overrideProvider.TryGetFresh(localPlayerId, _maximumAge, out var signal))
        {
            if (!snapshot.IsAttached || snapshot.HeroAddress is null)
            {
                return snapshot with
                {
                    State = VisibilityState.Unknown,
                    RawVisibilityMask = null,
                    Detail = signal.Source == VisibilityOverrideSource.RemoteHostRelay
                        ? "Remote host FoW relay is active; waiting for the local hero pointer."
                        : "Dota engine visibility bridge is active; waiting for the local hero pointer.",
                };
            }

            var state = signal.IsVisible ? VisibilityState.Visible : VisibilityState.Hidden;
            var stateText = signal.IsVisible ? "detected" : "clear";
            var playerText = signal.PlayerId is int playerId ? $" for player {playerId}" : string.Empty;
            var detailPrefix = signal.Source == VisibilityOverrideSource.RemoteHostRelay
                ? "Dota remote host FoW relay"
                : "Dota engine FoW bridge";

            return snapshot with
            {
                State = state,
                ObservedAt = signal.ObservedAt,
                RawVisibilityMask = null,
                Detail = $"{detailPrefix}: enemy vision {stateText}{playerText}.",
                IsInEnemySentry = signal.IsInEnemySentry || snapshot.IsInEnemySentry,
            };
        }

        if (!_allowMemoryFallback
            && _overrideProvider.HasEverReceived(localPlayerId)
            && snapshot.IsAttached)
        {
            return snapshot with
            {
                State = VisibilityState.Unknown,
                RawVisibilityMask = null,
                Detail = "Authoritative Dota visibility heartbeat is stale; waiting for a fresh local-engine or host-relay state.",
            };
        }

        if (!_allowMemoryFallback && snapshot.IsAttached)
        {
            var playerText = localPlayerId is int playerId ? $" for player {playerId}" : string.Empty;
            return snapshot with
            {
                State = VisibilityState.Unknown,
                RawVisibilityMask = null,
                Detail = $"Waiting for authoritative Dota visibility bridge or host relay{playerText}.",
            };
        }

        return snapshot;
    }
}
