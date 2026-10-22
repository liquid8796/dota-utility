namespace DotaUtility.Memory;

public interface IProcessMemoryAccessor : IDisposable
{
    bool IsAttached { get; }

    int? ProcessId { get; }

    bool TryAttach(string processName);

    bool TryAttach(int processId);

    void Detach();

    bool TryGetModule(string moduleName, out ProcessModuleSnapshot module);

    bool TryReadBytes(ulong address, int count, out byte[] bytes);

    bool TryRead<T>(ulong address, out T value)
        where T : unmanaged;
}
