using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DotaUtility.Memory;

public sealed partial class Win32ProcessMemoryAccessor : IProcessMemoryAccessor, IProcessAttachmentGeneration
{
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint Synchronize = 0x0010_0000;
    private const uint WaitTimeout = 0x0000_0102;

    private readonly object _gate = new();
    private SafeProcessHandle? _processHandle;
    private int? _processId;
    private long _attachmentGeneration;

    public long AttachmentGeneration
    {
        get
        {
            lock (_gate)
            {
                return _attachmentGeneration;
            }
        }
    }

    public bool IsAttached
    {
        get
        {
            lock (_gate)
            {
                if (IsProcessHandleActiveLocked())
                {
                    return true;
                }

                ClearProcessStateLocked();
                return false;
            }
        }
    }

    public int? ProcessId
    {
        get
        {
            lock (_gate)
            {
                if (!IsProcessHandleActiveLocked())
                {
                    ClearProcessStateLocked();
                    return null;
                }

                return _processId;
            }
        }
    }

    public bool TryAttach(string processName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processName);
        var normalizedName = Path.GetFileNameWithoutExtension(processName);
        var candidates = Process.GetProcessesByName(normalizedName);
        try
        {
            foreach (var process in candidates.OrderBy(static process => process.Id))
            {
                if (TryAttach(process.Id))
                {
                    return true;
                }
            }
        }
        finally
        {
            foreach (var process in candidates)
            {
                process.Dispose();
            }
        }

        return false;
    }

    public bool TryAttach(int processId)
    {
        if (processId <= 0)
        {
            return false;
        }

        var handle = NativeMethods.OpenProcess(
            ProcessVmRead | ProcessQueryInformation | ProcessQueryLimitedInformation | Synchronize,
            false,
            unchecked((uint)processId));

        if (handle.IsInvalid)
        {
            handle.Dispose();
            return false;
        }

        lock (_gate)
        {
            var previous = _processHandle;
            _processHandle = handle;
            _processId = processId;
            _attachmentGeneration++;
            previous?.Dispose();
        }

        return true;
    }

    public void Detach()
    {
        lock (_gate)
        {
            ClearProcessStateLocked();
        }
    }

    public bool TryGetModule(string moduleName, out ProcessModuleSnapshot module)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);
        module = default!;

        int processId;
        long attachmentGeneration;
        lock (_gate)
        {
            if (!IsProcessHandleActiveLocked() || _processId is not int currentProcessId)
            {
                ClearProcessStateLocked();
                return false;
            }

            processId = currentProcessId;
            attachmentGeneration = _attachmentGeneration;
        }

        ProcessModuleSnapshot? candidate = null;
        try
        {
            using var process = Process.GetProcessById(processId);
            foreach (ProcessModule processModule in process.Modules)
            {
                if (!string.Equals(processModule.ModuleName, moduleName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                candidate = new ProcessModuleSnapshot(
                    processModule.ModuleName,
                    processModule.FileName,
                    unchecked((ulong)processModule.BaseAddress.ToInt64()),
                    processModule.ModuleMemorySize);
                break;
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
        catch (NotSupportedException)
        {
        }

        lock (_gate)
        {
            if (_attachmentGeneration != attachmentGeneration || _processId != processId)
            {
                // Another attach/detach happened during enumeration.
                // Never clear the newer handle or publish the old snapshot.
                return false;
            }

            if (!IsProcessHandleActiveLocked())
            {
                ClearProcessStateLocked();
                return false;
            }

            if (candidate is null)
            {
                return false;
            }

            module = candidate;
            return true;
        }
    }

    public bool TryReadBytes(ulong address, int count, out byte[] bytes)
    {
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        bytes = new byte[count];
        if (count == 0)
        {
            return true;
        }

        lock (_gate)
        {
            if (_processHandle is not { IsClosed: false, IsInvalid: false } handle)
            {
                bytes = [];
                return false;
            }

            var success = NativeMethods.ReadProcessMemory(
                handle,
                new IntPtr(unchecked((long)address)),
                bytes,
                unchecked((nuint)count),
                out var bytesRead);

            if (!success || bytesRead != unchecked((nuint)count))
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

    public void Dispose()
    {
        Detach();
        GC.SuppressFinalize(this);
    }

    private bool IsProcessHandleActiveLocked()
    {
        if (_processHandle is not { IsClosed: false, IsInvalid: false } handle)
        {
            return false;
        }

        var waitResult = NativeMethods.WaitForSingleObject(handle, 0);
        return waitResult == WaitTimeout;
    }

    private void ClearProcessStateLocked()
    {
        if (_processHandle is not null || _processId is not null)
        {
            _attachmentGeneration++;
        }

        _processHandle?.Dispose();
        _processHandle = null;
        _processId = null;
    }

    private static partial class NativeMethods
    {
        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static partial SafeProcessHandle OpenProcess(
            uint desiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
            uint processId);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool ReadProcessMemory(
            SafeProcessHandle process,
            IntPtr baseAddress,
            [Out] byte[] buffer,
            nuint size,
            out nuint numberOfBytesRead);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static partial uint WaitForSingleObject(
            SafeProcessHandle handle,
            uint milliseconds);
    }
}
