namespace DotaUtility.Domain;

public readonly record struct EnemyHeroMinimapMarker(
    int EntityIndex,
    string HeroName,
    float WorldX,
    float WorldY,
    bool IsLive = true);

public sealed record VisibilitySnapshot(
    VisibilityState State,
    DateTimeOffset ObservedAt,
    bool IsAttached,
    int? ProcessId,
    int? LocalTeam,
    int? EnemyTeam,
    int? RawVisibilityMask,
    string Detail,
    ulong? HeroAddress = null,
    int? LocalPlayerId = null,
    bool IsInEnemySentry = false,
    IReadOnlyList<EnemyHeroMinimapMarker>? EnemyHeroes = null)
{
    public static VisibilitySnapshot Unknown(
        string detail,
        bool isAttached = false,
        int? processId = null,
        DateTimeOffset? observedAt = null) =>
        new(
            VisibilityState.Unknown,
            observedAt ?? DateTimeOffset.UtcNow,
            isAttached,
            processId,
            null,
            null,
            null,
            detail);
}
