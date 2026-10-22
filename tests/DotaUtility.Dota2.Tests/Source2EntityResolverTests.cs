namespace DotaUtility.Dota2.Tests;

public sealed class Source2EntityResolverTests
{
    [Fact]
    public void ResolveByHandle_UsesLowEntityIndexBitsAndChunkedEntityList()
    {
        const ulong entitySystem = 0x1000;
        const ulong chunk = 0x4000;
        const ulong entity = 0x9000;
        const uint handle = 0x0001_8221;
        const int expectedEntityIndex = 0x221;
        var memory = new FakeProcessMemoryAccessor();
        memory.TryAttach(1337);
        var offsets = DotaOffsets.Current;
        var chunkIndex = expectedEntityIndex >> offsets.EntityListChunkShift;
        var withinChunk = expectedEntityIndex & offsets.EntityListChunkMask;
        memory.Write(
            entitySystem + (ulong)offsets.EntityListFirstChunkOffset + (ulong)(chunkIndex * offsets.EntityListChunkPointerStride),
            chunk);
        memory.Write(
            chunk + (ulong)(withinChunk * offsets.EntityListEntryStride),
            entity);
        memory.Write(chunk + (ulong)(withinChunk * offsets.EntityListEntryStride) + (ulong)offsets.EntityIdentityHandle, handle);
        var resolver = new Source2EntityResolver(memory, offsets);

        var actual = resolver.ResolveByHandle(entitySystem, handle);

        Assert.Equal(entity, actual);
    }

    [Fact]
    public void ResolveByIndex_ReturnsZeroWhenChunkCannotBeRead()
    {
        var memory = new FakeProcessMemoryAccessor();
        memory.TryAttach(1337);
        var resolver = new Source2EntityResolver(memory, DotaOffsets.Current);

        var actual = resolver.ResolveByIndex(0x1000, 7);

        Assert.Equal(0UL, actual);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(0xFFFF_FFFFu)]
    [InlineData(0xFFFF_FFFEu)]
    public void ResolveByHandle_ReturnsZeroForInvalidHandles(uint handle)
    {
        var memory = new FakeProcessMemoryAccessor();
        memory.TryAttach(1337);
        var resolver = new Source2EntityResolver(memory, DotaOffsets.Current);

        var actual = resolver.ResolveByHandle(0x1000, handle);

        Assert.Equal(0UL, actual);
    }
}
