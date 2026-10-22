using DotaUtility.Dota2;
using DotaUtility.Memory;

namespace DotaUtility.ModifierAudit;

/// <summary>Raw caster metadata, not the native effective-team virtual result.</summary>
public sealed record ModifierCasterObservation(
    uint Handle, string Availability, byte? RawEntityTeam,
    byte? NativeEntityKind, uint? OwnerHandle)
{
    public int? EffectiveTeam => null;
    public bool IsEnemySightEvidence => false;
}

public static class OwnerModifierSnapshotComparer
{
    public static bool Equivalent(OwnerModifierSnapshot? a, OwnerModifierSnapshot? b) =>
        a is not null && b is not null && a.ArrayCount == b.ArrayCount
        && a.NullEntries == b.NullEntries && a.Modifiers.SequenceEqual(b.Modifiers)
        && a.TeamCache == b.TeamCache && a.InvisibilityLevel == b.InvisibilityLevel
        && a.UnitStateBits == b.UnitStateBits;
}

/// <summary>
/// Reads only unique caster references from an already validated owner list.
/// Missing casters remain unavailable. In particular, +0x94 on a modifier is
/// never substituted: native initialization may have copied the parent's team.
/// The NPC effective-team getter may follow an owner or a game-mode override;
/// this reader intentionally does not execute or approximate that getter.
/// </summary>
public sealed class ModifierCasterReader(IProcessMemoryAccessor memory)
{
    private const ulong MaxPointer = 0x0000_7FFF_FFFF_FFFF;
    private readonly IProcessMemoryAccessor _memory = memory ?? throw new ArgumentNullException(nameof(memory));

    public bool TryRead(ulong entitySystem, IReadOnlyList<OwnerModifier> modifiers,
        ProcessModuleSnapshot module, Func<bool> contextStillCurrent,
        out IReadOnlyList<ModifierCasterObservation>? observations, out string detail)
    {
        ArgumentNullException.ThrowIfNull(modifiers);
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(contextStillCurrent);
        observations = null;
        detail = string.Empty;
        var blocks = new List<(ulong Address, byte[] Bytes)>();
        var identities = new List<(uint Handle, ulong Entity)>();
        var entities = new Source2EntityResolver(_memory, DotaOffsets.Current);
        var rows = new List<ModifierCasterObservation>();

        static ulong At(ulong pointer, ulong offset = 0, int size = 1)
        {
            if (pointer < 0x10000 || pointer > MaxPointer || offset > MaxPointer - pointer
                || size <= 0 || (ulong)size - 1 > MaxPointer - pointer - offset)
                throw new ReadFailure("Invalid caster pointer range.");
            return pointer + offset;
        }

        byte[] Read(ulong address, int size)
        {
            At(address, 0, size);
            if (size > 32 || blocks.Count >= 2048
                || !_memory.TryReadBytes(address, size, out var bytes) || bytes.Length != size)
                throw new ReadFailure("Caster metadata unreadable or budget exceeded.");
            blocks.Add((address, bytes));
            return bytes;
        }

        bool InModule(ulong pointer, int size) => module.Size > 0 && pointer >= module.BaseAddress
            && pointer - module.BaseAddress <= (ulong)module.Size
            && (ulong)size <= (ulong)module.Size - (pointer - module.BaseAddress);

        try
        {
            if (modifiers.Count > OwnerModifierReader.MaximumModifiers || !contextStillCurrent())
                throw new ReadFailure("No stable owner context or too many modifier sources.");
            At(entitySystem, 0x10, 512);
            At(module.BaseAddress, 0, module.Size);
            foreach (var handle in modifiers.Select(m => m.CasterHandle).Distinct())
            {
                if (handle is 0 or uint.MaxValue or 0xFFFFFFFE)
                {
                    rows.Add(new(handle, "invalid_handle", null, null, null));
                    continue;
                }
                var entity = entities.ResolveByHandle(entitySystem, handle);
                identities.Add((handle, entity));
                if (entity == 0)
                {
                    rows.Add(new(handle, "unavailable", null, null, null));
                    continue;
                }
                var pointer = BitConverter.ToUInt64(Read(At(entity, 0x10, 8), 8));
                var identity = Read(At(pointer, 0, 20), 20);
                if (BitConverter.ToUInt64(identity, 0) != entity
                    || BitConverter.ToUInt32(identity, 16) != handle)
                    throw new ReadFailure("Caster identity back-pointer or generation changed.");
                var vtable = BitConverter.ToUInt64(Read(At(entity, 0, 8), 8));
                if (!InModule(vtable, 8)) throw new ReadFailure("Caster vtable outside audited client.");
                var firstMethod = BitConverter.ToUInt64(Read(vtable, 8));
                if (!InModule(firstMethod, 1)) throw new ReadFailure("Caster method outside audited client.");
                var team = Read(At(entity, 0x3E7), 1)[0];
                var kind = Read(At(entity, 0x5D0), 1)[0];
                if (team >= 15) throw new ReadFailure("Invalid raw caster team.");
                uint? owner = kind == 3
                    ? BitConverter.ToUInt32(Read(At(entity, 0x514, 4), 4)) : null;
                rows.Add(new(handle, "resolved", team, kind, owner));
            }
            foreach (var (address, bytes) in blocks)
                if (!_memory.TryReadBytes(address, bytes.Length, out var after)
                    || !after.AsSpan().SequenceEqual(bytes))
                    throw new ReadFailure("Caster metadata changed during acquisition.");
            foreach (var (handle, entity) in identities)
                if (entities.ResolveByHandle(entitySystem, handle) != entity)
                    throw new ReadFailure("Caster appeared, disappeared or changed generation during acquisition.");
            if (!contextStillCurrent()) throw new ReadFailure("Owner context changed during source acquisition.");
            observations = rows.AsReadOnly();
            detail = "Source handles and raw teams observed; effective teams and enemy sight are not inferred.";
            return true;
        }
        catch (ReadFailure error)
        {
            detail = error.Message;
            return false;
        }
    }

    private sealed class ReadFailure(string message) : Exception(message);
}
