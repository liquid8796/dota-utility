using DotaUtility.Memory;

namespace DotaUtility.Dota2;

public sealed class Source2EntityResolver
{
    private readonly IProcessMemoryAccessor _memory;
    private readonly DotaOffsets _offsets;

    public Source2EntityResolver(IProcessMemoryAccessor memory, DotaOffsets offsets)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _offsets = offsets ?? throw new ArgumentNullException(nameof(offsets));
    }

    public ulong ResolveByHandle(ulong entitySystem, uint handle)
    {
        if (entitySystem == 0 || handle is 0 or uint.MaxValue or 0xFFFF_FFFEu)
        {
            return 0;
        }

        var entityIndex = (int)(handle & (uint)_offsets.EntityHandleIndexMask);
        var identity = ResolveIdentityByIndex(entitySystem, entityIndex);
        if (identity == 0
            || !_memory.TryRead<uint>(identity + (ulong)_offsets.EntityIdentityHandle, out var storedHandle)
            || storedHandle != handle
            || !_memory.TryRead<ulong>(identity, out var entity)
            || entity == 0)
        {
            return 0;
        }

        // Native Source 2 validates the serial as well as the low entity index.
        // Reject slots recycled during the external pointer read.
        return _memory.TryRead<uint>(identity + (ulong)_offsets.EntityIdentityHandle, out var confirmedHandle)
            && confirmedHandle == handle
            && _memory.TryRead<ulong>(identity, out var confirmedEntity)
            && confirmedEntity == entity
                ? entity
                : 0;
    }

    /// <summary>Enumerates the current occupant; does not validate an old handle.</summary>
    public ulong ResolveByIndex(ulong entitySystem, int entityIndex)
    {
        var identity = ResolveIdentityByIndex(entitySystem, entityIndex);
        return identity != 0 && _memory.TryRead<ulong>(identity, out var entity) ? entity : 0;
    }

    private ulong ResolveIdentityByIndex(ulong entitySystem, int entityIndex)
    {
        if (entitySystem == 0 || entityIndex < 0 || entityIndex > _offsets.EntityHandleIndexMask)
        {
            return 0;
        }

        var chunkIndex = entityIndex >> _offsets.EntityListChunkShift;
        var withinChunk = entityIndex & _offsets.EntityListChunkMask;
        var chunkPointerAddress = entitySystem
            + (ulong)_offsets.EntityListFirstChunkOffset
            + (ulong)(chunkIndex * _offsets.EntityListChunkPointerStride);

        if (!_memory.TryRead<ulong>(chunkPointerAddress, out var chunk) || chunk == 0)
        {
            return 0;
        }

        return chunk + (ulong)(withinChunk * _offsets.EntityListEntryStride);
    }
}
