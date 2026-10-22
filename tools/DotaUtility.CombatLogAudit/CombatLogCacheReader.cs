using DotaUtility.Memory;

namespace DotaUtility.CombatLogAudit;

/// <summary>Historical event data only; these flags are not local-hero sight.</summary>
public sealed record CachedCombatEvent(
    uint? TargetNameIndex,
    float? Timestamp,
    bool? VisibleRadiant,
    bool? VisibleDire)
{
    public string CurrentHeroVisibility => "Unknown";
    public bool CanDriveCurrentOverlay => false;
}

public sealed record CombatCacheSnapshot(
    int RootIndex,
    int Capacity,
    int VisitedNodes,
    IReadOnlyList<CachedCombatEvent> Events);

/// <summary>
/// Read/Write, bounded cache traversal for the individually hash-checked CLI build.
/// This is not an IVisibilityProbe and cannot publish a game visibility snapshot.
/// A caller must also validate local player/match identity around this operation.
/// </summary>
public sealed class CombatLogCacheReader(IProcessMemoryAccessor memory)
{
    public const int RecordSize = 0x120;
    public const int MaximumNodes = 128;
    public const int MaximumEvents = 512;
    private const int MaximumAllocation = 4096;
    private const int MaximumEventsPerNode = 128;
    private const int MaximumReadBytes = 1 << 20;
    private const ulong MaximumPointer = 0x0000_7FFF_FFFF_FFFF;
    private readonly IProcessMemoryAccessor _memory = memory ?? throw new ArgumentNullException(nameof(memory));

    public bool TryRead(
        ulong hud,
        ulong expectedRecordVtable,
        out CombatCacheSnapshot? snapshot,
        out string detail)
    {
        snapshot = null;
        detail = string.Empty;
        var blocks = new List<(ulong Address, byte[] Bytes)>();
        var totalBytes = 0;

        byte[] ReadBlock(ulong address, int count)
        {
            if (address == 0 || address > MaximumPointer || count <= 0
                || count > 65536 || (ulong)count - 1 > MaximumPointer - address
                || totalBytes > MaximumReadBytes - count)
            {
                throw new CacheReadException("Pointer range or read budget is invalid.");
            }
            if (!_memory.TryReadBytes(address, count, out var bytes) || bytes.Length != count)
            {
                throw new CacheReadException("A required cache block was unreadable.");
            }
            totalBytes += count;
            blocks.Add((address, bytes));
            return bytes;
        }

        try
        {
            if (hud == 0 || expectedRecordVtable == 0 || expectedRecordVtable > MaximumPointer)
            {
                throw new CacheReadException("HUD or expected record type is unavailable.");
            }
            var header = ReadBlock(checked(hud + 0x40), 0x30);
            var capacity = BitConverter.ToInt32(header, 0);
            var allocationFlags = BitConverter.ToUInt32(header, 4);
            var nodes = BitConverter.ToUInt64(header, 8);
            var root = BitConverter.ToInt32(header, 0x10);
            if (capacity < 0 || capacity > MaximumAllocation
                || (capacity > 0 && ((allocationFlags & 0x7FFFFFFF) == 0 || nodes == 0)))
            {
                throw new CacheReadException("Invalid cache tree allocation.");
            }

            var pending = new Stack<int>();
            var visited = new HashSet<int>();
            var events = new List<CachedCombatEvent>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var index = pending.Pop();
                if (index == -1)
                {
                    continue;
                }
                if (index < 0 || index >= capacity || !visited.Add(index) || visited.Count > MaximumNodes)
                {
                    throw new CacheReadException("Cache tree is cyclic, out of range or over budget.");
                }
                var node = ReadBlock(checked(nodes + (ulong)index * 0x30), 0x30);
                pending.Push(BitConverter.ToInt32(node, 0));
                pending.Push(BitConverter.ToInt32(node, 4));
                var count = BitConverter.ToInt32(node, 0x18);
                var allocation = BitConverter.ToInt32(node, 0x28);
                var records = BitConverter.ToUInt64(node, 0x20);
                if (count < 0 || allocation < 0 || count > allocation || allocation > MaximumAllocation
                    || count > MaximumEventsPerNode || (count > 0 && records == 0)
                    || events.Count > MaximumEvents - count)
                {
                    throw new CacheReadException("Invalid or over-budget cache record array.");
                }
                if (count == 0)
                {
                    continue;
                }
                var data = ReadBlock(records, checked(count * RecordSize));
                for (var i = 0; i < count; i++)
                {
                    var start = i * RecordSize;
                    if (BitConverter.ToUInt64(data, start) != expectedRecordVtable)
                    {
                        throw new CacheReadException("Cached entry vtable does not match the audited record type.");
                    }
                    var presence = BitConverter.ToUInt32(data, start + 0x10);
                    bool? Flag(int bit, int offset) => (presence & (1u << bit)) == 0
                        ? null : data[start + offset] != 0;
                    float? timestamp = (presence & (1u << 11)) == 0
                        ? null : BitConverter.ToSingle(data, start + 0x50);
                    if (timestamp.HasValue && !float.IsFinite(timestamp.Value))
                    {
                        throw new CacheReadException("Non-finite cache event timestamp.");
                    }
                    events.Add(new CachedCombatEvent(
                        (presence & 1) == 0 ? null : BitConverter.ToUInt32(data, start + 0x30),
                        timestamp,
                        Flag(12, 0x54),
                        Flag(13, 0x55)));
                }
            }

            // Best-effort consistency check, not an atomic game-frame snapshot.
            foreach (var block in blocks)
            {
                if (!_memory.TryReadBytes(block.Address, block.Bytes.Length, out var again)
                    || !again.AsSpan().SequenceEqual(block.Bytes))
                {
                    throw new CacheReadException("Cache changed or became unreadable during sampling.");
                }
            }
            snapshot = new CombatCacheSnapshot(root, capacity, visited.Count, events.ToArray());
            return true;
        }
        catch (CacheReadException exception)
        {
            detail = exception.Message;
            return false;
        }
        catch (OverflowException)
        {
            detail = "Cache pointer arithmetic overflowed.";
            return false;
        }
    }

    private sealed class CacheReadException(string message) : Exception(message);
}
