namespace DotaUtility.Application;

public enum VisibilityOverrideSource
{
    LocalEngine = 0,
    RemoteHostRelay = 1,
}

public readonly record struct VisibilityOverrideSignal(
    bool IsVisible,
    DateTimeOffset ObservedAt,
    int? PlayerId = null,
    VisibilityOverrideSource Source = VisibilityOverrideSource.LocalEngine,
    bool IsInEnemySentry = false);
