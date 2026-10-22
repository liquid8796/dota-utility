using System.Buffers.Binary;
using DotaUtility.Domain;
using DotaUtility.Memory;

namespace DotaUtility.Dota2.Tests;

public sealed class DotaBuildCompatibilityGateTests
{
    private const ulong ModuleBase = 0x10000;
    private const int PeOffset = 0x80;
    private const uint Timestamp = 0x6AD1_1234;
    private const int ImageSize = 0x4000;

    [Fact]
    public void TryValidate_RejectsUnknownClientBuildDigest()
    {
        using var tempDir = new TempClientDirectory(Timestamp, (uint)ImageSize);
        var memory = CreateMemoryWithMappedModule(tempDir.ClientPath, Timestamp, (uint)ImageSize, ImageSize);
        var gate = new DotaBuildCompatibilityGate(memory);

        var valid = gate.TryValidate(out var detail);

        Assert.False(valid);
        Assert.Contains("Unsupported client.dll build", detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryValidate_RejectsMappedAndBackingFileMismatch()
    {
        using var tempDir = new TempClientDirectory(Timestamp, (uint)ImageSize);
        var memory = CreateMemoryWithMappedModule(tempDir.ClientPath, Timestamp + 1, (uint)ImageSize, ImageSize);
        var gate = new DotaBuildCompatibilityGate(memory);

        var valid = gate.TryValidate(out var detail);

        Assert.False(valid);
        Assert.Contains("does not match its backing file", detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryValidate_RejectsMalformedOversizedPeMetadataWithoutThrowing()
    {
        using var tempDir = new TempClientDirectory(Timestamp, 0x8000_0000u);
        var memory = CreateMemoryWithMappedModule(tempDir.ClientPath, Timestamp, 0x8000_0000u, ImageSize);
        var gate = new DotaBuildCompatibilityGate(memory);

        var valid = gate.TryValidate(out var detail);

        Assert.False(valid);
        Assert.Contains("PE identity", detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectedBuild_NeverInvokesAddressResolverAndPreventsProjectorReads()
    {
        var memory = new FakeProcessMemoryAccessor();
        memory.TryAttach(1337);
        var gate = new RejectingGate();
        var resolverCalled = false;
        var probe = new DotaUtilityProbe(
            memory,
            new CallbackResolver(() => resolverCalled = true),
            DotaOffsets.Current,
            gate);

        var snapshot = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(VisibilityState.Unknown, snapshot.State);
        Assert.False(resolverCalled);
        Assert.Contains("Unsupported client.dll", snapshot.Detail, StringComparison.OrdinalIgnoreCase);

        var reads = 0;
        memory.BeforeRead = (_, _) => reads++;
        var projector = new DotaHeroScreenProjector(memory, DotaOffsets.Current, gate);
        Assert.False(projector.TryProject(0x5000, 1920, 1080, out _));
        Assert.Equal(0, reads);
    }

    private static FakeProcessMemoryAccessor CreateMemoryWithMappedModule(
        string filePath,
        uint mappedTimestamp,
        uint mappedSizeOfImage,
        int moduleSize)
    {
        var memory = new FakeProcessMemoryAccessor();
        memory.TryAttach(1337);
        var headers = BuildPeHeaders(mappedTimestamp, mappedSizeOfImage);
        memory.WriteBytes(ModuleBase, headers);
        memory.AddModule(new ProcessModuleSnapshot("client.dll", filePath, ModuleBase, moduleSize));
        return memory;
    }

    private static byte[] BuildPeHeaders(uint timestamp, uint sizeOfImage)
    {
        var buffer = new byte[PeOffset + 0x100];
        buffer[0] = (byte)'M';
        buffer[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0x3C, 4), PeOffset);
        buffer[PeOffset] = (byte)'P';
        buffer[PeOffset + 1] = (byte)'E';
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(PeOffset + 4, 2), 0x8664);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(PeOffset + 8, 4), timestamp);
        var optionalHeader = PeOffset + 24;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(optionalHeader, 2), 0x020B);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(optionalHeader + 56, 4), sizeOfImage);
        return buffer;
    }

    private sealed class TempClientDirectory : IDisposable
    {
        private readonly string _directory;

        public TempClientDirectory(uint timestamp, uint sizeOfImage)
        {
            _directory = Path.Combine(Path.GetTempPath(), $"dota-utility-gate-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_directory);
            ClientPath = Path.Combine(_directory, "client.dll");
            File.WriteAllBytes(ClientPath, BuildPeHeaders(timestamp, sizeOfImage));
        }

        public string ClientPath { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_directory))
                {
                    Directory.Delete(_directory, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class RejectingGate : IDotaBuildCompatibilityGate
    {
        public bool TryValidate(out string detail)
        {
            detail = "Unsupported client.dll build test-digest.";
            return false;
        }
    }

    private sealed class CallbackResolver(Action onResolve) : IDotaAddressResolver
    {
        public bool TryResolve(out DotaResolvedAddresses addresses, out string detail)
        {
            onResolve();
            addresses = new DotaResolvedAddresses(0x1000, 0x1100, 0x1200, 0x1300);
            detail = "Resolved.";
            return true;
        }
    }
}
