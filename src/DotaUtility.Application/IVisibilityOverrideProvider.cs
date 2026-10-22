namespace DotaUtility.Application;

public interface IVisibilityOverrideProvider
{
    bool HasEverReceived(int? playerId);

    bool TryGetFresh(
        int? playerId,
        TimeSpan maximumAge,
        out VisibilityOverrideSignal signal);
}
