using System.Runtime.InteropServices;
using DotaUtility.Memory;

namespace DotaUtility.Memory.Tests;

public sealed class Win32ProcessMemoryAccessorTests
{
    [Fact]
    public void TryRead_ReadsUnmanagedValueFromAttachedProcess()
    {
        const long expected = 0x1020_3040_5060_7080;
        var address = Marshal.AllocHGlobal(sizeof(long));
        try
        {
            Marshal.WriteInt64(address, expected);
            using var accessor = new Win32ProcessMemoryAccessor();

            Assert.True(accessor.TryAttach(Environment.ProcessId));
            Assert.True(accessor.IsAttached);
            Assert.Equal(Environment.ProcessId, accessor.ProcessId);
            Assert.True(accessor.TryRead<long>(unchecked((ulong)address.ToInt64()), out var actual));
            Assert.Equal(expected, actual);
        }
        finally
        {
            Marshal.FreeHGlobal(address);
        }
    }

    [Fact]
    public void Detach_ClearsProcessState()
    {
        using var accessor = new Win32ProcessMemoryAccessor();
        Assert.True(accessor.TryAttach(Environment.ProcessId));

        accessor.Detach();

        Assert.False(accessor.IsAttached);
        Assert.Null(accessor.ProcessId);
    }

    [Fact]
    public void AttachmentGeneration_IncrementsMonotonicallyAcrossAttachAndDetach()
    {
        using var accessor = new Win32ProcessMemoryAccessor();
        var gen0 = accessor.AttachmentGeneration;

        Assert.True(accessor.TryAttach(Environment.ProcessId));
        var gen1 = accessor.AttachmentGeneration;
        Assert.True(gen1 > gen0);

        accessor.Detach();
        var gen2 = accessor.AttachmentGeneration;
        Assert.True(gen2 > gen1);
    }

    [Theory]
    [InlineData(0UL, 1024)]
    [InlineData(0x10000UL, 0)]
    [InlineData(0x8000_0000_0000_0000UL, 1024)]
    public void ModulePatternScanner_RejectsImpossibleModuleMetadata(ulong baseAddress, int size)
    {
        using var accessor = new Win32ProcessMemoryAccessor();
        var module = new ProcessModuleSnapshot("client.dll", "client.dll", baseAddress, size);
        var pattern = BytePattern.Parse("48 89 0D ?? ?? ?? ??");

        Assert.False(ModulePatternScanner.TryResolveRipRelativeAddress(
            accessor,
            module,
            pattern,
            displacementOffset: 3,
            instructionLength: 7,
            out _));
    }
}
