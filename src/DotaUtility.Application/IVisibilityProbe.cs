using DotaUtility.Domain;

namespace DotaUtility.Application;

public interface IVisibilityProbe
{
    Task<VisibilitySnapshot> ProbeAsync(CancellationToken cancellationToken);
}
