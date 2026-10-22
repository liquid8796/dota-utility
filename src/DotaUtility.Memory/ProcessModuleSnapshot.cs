namespace DotaUtility.Memory;

public sealed record ProcessModuleSnapshot(
    string Name,
    string FilePath,
    ulong BaseAddress,
    int Size);
