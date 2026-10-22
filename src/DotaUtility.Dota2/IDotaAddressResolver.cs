namespace DotaUtility.Dota2;

public sealed record DotaResolvedAddresses(
    ulong EntitySystemGlobalAddress,
    ulong NetworkGameClientGlobalAddress,
    ulong TeamDataHandleArrayAddress,
    ulong LocalControllerArrayAddress);

public interface IDotaAddressResolver
{
    bool TryResolve(out DotaResolvedAddresses addresses, out string detail);
}
