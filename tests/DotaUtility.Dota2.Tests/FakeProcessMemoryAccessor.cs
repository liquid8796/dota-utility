using System.Runtime.InteropServices;
using DotaUtility.Memory;

namespace DotaUtility.Dota2.Tests;

internal sealed class FakeProcessMemoryAccessor : IProcessMemoryAccessor, IProcessAttachmentGeneration
{
    private readonly Dictionary<ulong, byte> _memory = [];
    private readonly Dictionary<string, ProcessModuleSnapshot> _modules = new(StringComparer.OrdinalIgnoreCase);

    public bool IsAttached { get; private set; }

    public int? ProcessId { get; private set; }

    public long AttachmentGeneration { get; set; }

    public bool AttachSucceeds { get; set; } = true;

    public Action<ulong, int>? BeforeRead { get; set; }

    public bool TryAttach(string processName)
    {
        if (!AttachSucceeds)
        {
            return false;
        }

        IsAttached = true;
        ProcessId = 1337;
        AttachmentGeneration++;
        return true;
    }

    public bool TryAttach(int processId)
    {
        if (!AttachSucceeds)
        {
            return false;
        }

        IsAttached = true;
        ProcessId = processId;
        AttachmentGeneration++;
        return true;
    }

    public void Detach()
    {
        IsAttached = false;
        ProcessId = null;
        AttachmentGeneration++;
    }

    public void Dispose() => Detach();

    public bool TryGetModule(string moduleName, out ProcessModuleSnapshot module) => _modules.TryGetValue(moduleName, out module!);

    public bool TryReadBytes(ulong address, int count, out byte[] bytes)
    {
        BeforeRead?.Invoke(address, count);
        bytes = new byte[count];
        for (var index = 0; index < count; index++)
        {
            if (!_memory.TryGetValue(address + (ulong)index, out bytes[index]))
            {
                bytes = [];
                return false;
            }
        }

        return true;
    }

    public bool TryRead<T>(ulong address, out T value)
        where T : unmanaged
    {
        var size = Marshal.SizeOf<T>();
        if (!TryReadBytes(address, size, out var bytes))
        {
            value = default;
            return false;
        }

        value = MemoryMarshal.Read<T>(bytes);
        return true;
    }

    public void Write<T>(ulong address, T value)
        where T : unmanaged
    {
        var bytes = new byte[Marshal.SizeOf<T>()];
        MemoryMarshal.Write(bytes.AsSpan(), in value);
        for (var index = 0; index < bytes.Length; index++)
        {
            _memory[address + (ulong)index] = bytes[index];
        }
    }

    public void WriteBytes(ulong address, ReadOnlySpan<byte> bytes)
    {
        for (var index = 0; index < bytes.Length; index++)
        {
            _memory[address + (ulong)index] = bytes[index];
        }
    }

    public void AddModule(ProcessModuleSnapshot module) => _modules[module.Name] = module;
}
