namespace DotaUtility.Application;

public sealed class CompositeVisibilityOverrideProvider : IVisibilityOverrideProvider
{
    private readonly IReadOnlyList<IVisibilityOverrideProvider> _providers;

    public CompositeVisibilityOverrideProvider(params IVisibilityOverrideProvider[] providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        if (providers.Length == 0)
        {
            throw new ArgumentException("At least one provider is required.", nameof(providers));
        }

        if (providers.Any(provider => provider is null))
        {
            throw new ArgumentException("Providers cannot contain null entries.", nameof(providers));
        }

        _providers = providers;
    }

    public bool HasEverReceived(int? playerId) =>
        _providers.Any(provider => provider.HasEverReceived(playerId));

    public bool TryGetFresh(
        int? playerId,
        TimeSpan maximumAge,
        out VisibilityOverrideSignal signal)
    {
        foreach (var provider in _providers)
        {
            if (provider.TryGetFresh(playerId, maximumAge, out signal))
            {
                return true;
            }
        }

        signal = default;
        return false;
    }
}
