using System.Text;
using DotaUtility.Memory;

namespace DotaUtility.ModifierAudit;

public sealed record OwnerModifier(
    string Name, string NativeType, int NativeInstanceToken, int NativeListIndex,
    uint ParentHandle, uint CasterHandle, uint AbilityHandle, bool PendingRemoval,
    int ClientCachedTeamHint = -1);

public sealed record ModifierTeamCache(ushort TrueSightMask, ushort TrueSightValidMask,
    ushort FowPositionMask, ushort FowPositionValidMask)
{
    // The client getter checks validity before consuming its memoized property.
    // Even a valid true-sight property is NOT a proof of enemy terrain/entity sight.
    public bool? TrueSightPropertyForTeam(int team) => team is >= 0 and < 15
        && (TrueSightValidMask & (1 << team)) != 0
        ? (TrueSightMask & (1 << team)) != 0 : null;
}

public sealed record OwnerModifierSnapshot(int ArrayCount, int NullEntries,
    IReadOnlyList<OwnerModifier> Modifiers, ModifierTeamCache TeamCache,
    float InvisibilityLevel, string UnitStateBits)
{
    public string CurrentHeroVisibility => "Unknown";
    public bool CanDriveCurrentOverlay => false;
}

/// <summary>
/// Bounded observation of the actual local hero's modifier list. The CLI must
/// verify the build and supply a full native-local-context consistency predicate.
/// This reader is not a visibility probe and never calls a modifier virtual method.
/// </summary>
public sealed class OwnerModifierReader(IProcessMemoryAccessor memory)
{
    public const int MaximumModifiers = 256;
    private const ulong MaxPointer = 0x0000_7FFF_FFFF_FFFF;
    private const int MaximumAllocation = 4096;
    private const int ReadBudget = 1 << 20;
    private readonly IProcessMemoryAccessor _memory = memory ?? throw new ArgumentNullException(nameof(memory));

    public bool TryRead(ulong hero, uint heroHandle, ProcessModuleSnapshot module,
        Func<bool> contextStillCurrent, out OwnerModifierSnapshot? snapshot, out string detail)
    {
        ArgumentNullException.ThrowIfNull(contextStillCurrent);
        ArgumentNullException.ThrowIfNull(module);
        snapshot = null;
        detail = string.Empty;
        var guards = new List<(ulong Address, byte[] Bytes)>();
        var consumed = 0;

        ulong At(ulong address, ulong offset = 0, int size = 1)
        {
            if (address < 0x10000 || address > MaxPointer || offset > MaxPointer - address
                || size <= 0 || (ulong)size - 1 > MaxPointer - address - offset)
                throw new ReadFailure("Invalid or overflowing pointer range.");
            return address + offset;
        }

        byte[] Read(ulong address, int size)
        {
            At(address, 0, size);
            if (size > 65536 || consumed > ReadBudget - size)
                throw new ReadFailure("Modifier read budget exceeded.");
            if (!_memory.TryReadBytes(address, size, out var bytes) || bytes.Length != size)
                throw new ReadFailure("A required modifier block is unreadable.");
            consumed += size;
            guards.Add((address, bytes));
            return bytes;
        }

        ulong InModule(ulong address, int size)
        {
            At(address, 0, size);
            if (module.Size <= 0 || address < module.BaseAddress
                || address - module.BaseAddress > (ulong)module.Size
                || (ulong)size > (ulong)module.Size - (address - module.BaseAddress))
                throw new ReadFailure("RTTI reference is outside the audited client module.");
            return address;
        }

        string CString(ulong address, int maximumLength = 192)
        {
            var text = new List<byte>();
            for (var offset = 0; offset < maximumLength; offset += 16)
            {
                var chunk = Read(At(address, (ulong)offset, 16), 16);
                foreach (var value in chunk)
                {
                    if (value == 0)
                    {
                        if (text.Count == 0) throw new ReadFailure("Empty modifier name/type.");
                        return Encoding.ASCII.GetString(text.ToArray());
                    }
                    if (value is < 32 or > 126) throw new ReadFailure("Non-ASCII modifier name/type.");
                    text.Add(value);
                }
            }
            throw new ReadFailure("Unterminated or over-budget modifier name/type.");
        }

        string TypeName(ulong vtable)
        {
            if (vtable < 8) throw new ReadFailure("Invalid modifier vtable.");
            var locator = BitConverter.ToUInt64(Read(InModule(vtable - 8, 16), 16), 0);
            var col = Read(InModule(locator, 24), 24);
            if (BitConverter.ToUInt32(col, 0) != 1 || BitConverter.ToUInt32(col, 4) != 0
                || BitConverter.ToUInt32(col, 20) != locator - module.BaseAddress)
                throw new ReadFailure("Unexpected primary MSVC RTTI locator.");
            var type = At(module.BaseAddress, BitConverter.ToUInt32(col, 12) + 16UL, 192);
            InModule(type, 192);
            var name = CString(type);
            if ((!name.StartsWith(".?AVCDOTA_Modifier_", StringComparison.Ordinal)
                && name != ".?AVCDOTA_Buff@@") || !name.EndsWith("@@", StringComparison.Ordinal))
                throw new ReadFailure("Unexpected object type in modifier list.");
            return name;
        }

        try
        {
            if (heroHandle is 0xFFFFFFFF or 0xFFFFFFFE || !contextStillCurrent())
                throw new ReadFailure("No current, validated native local-hero identity.");
            At(module.BaseAddress, 0, module.Size);
            var manager = At(hero, 0xD38, 0x388);
            var header = Read(At(manager, 8, 28), 28);
            var owner = BitConverter.ToUInt64(header, 0);
            var count = BitConverter.ToInt32(header, 8);
            var array = BitConverter.ToUInt64(header, 16);
            var capacity = BitConverter.ToInt32(header, 24);
            if (owner != hero || count is < 0 or > MaximumModifiers
                || capacity < count || capacity > MaximumAllocation)
                throw new ReadFailure("Modifier-manager owner/count/capacity mismatch.");
            var slots = count == 0 ? [] : Read(At(array, 0, count * 8), count * 8);
            var addresses = new HashSet<ulong>();
            var tokens = new HashSet<int>();
            var rows = new List<OwnerModifier>();
            var nullEntries = 0;
            for (var index = 0; index < count; index++)
            {
                var address = BitConverter.ToUInt64(slots, index * 8);
                if (address == 0) { nullEntries++; continue; }
                if (!addresses.Add(address)) throw new ReadFailure("Duplicate modifier address.");
                var bytes = Read(At(address, 0, 0xA0), 0xA0);
                var parent = BitConverter.ToUInt32(bytes, 0x74);
                var token = BitConverter.ToInt32(bytes, 0x48);
                if (BitConverter.ToUInt64(bytes, 0x30) != manager || parent != heroHandle)
                    throw new ReadFailure("Modifier belongs to a different manager or hero generation.");
                if (token < 0 || !tokens.Add(token))
                    throw new ReadFailure("Invalid or duplicate native modifier instance token.");
                var nativeType = TypeName(BitConverter.ToUInt64(bytes, 0));
                var name = CString(BitConverter.ToUInt64(bytes, 0x28));
                if (!name.StartsWith("modifier_", StringComparison.Ordinal)
                    || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
                    throw new ReadFailure("Unexpected modifier identifier.");
                rows.Add(new(name, nativeType, token, BitConverter.ToInt32(bytes, 0x50), parent,
                    BitConverter.ToUInt32(bytes, 0x68), BitConverter.ToUInt32(bytes, 0x70),
                    (bytes[0x9E] & 4) != 0, BitConverter.ToInt32(bytes, 0x94)));
            }
            var cache = Read(At(manager, 0x37A, 8), 8);
            var invisibility = BitConverter.ToSingle(Read(At(hero, 0xC64, 4), 4));
            if (!float.IsFinite(invisibility) || invisibility is < 0 or > 1)
                throw new ReadFailure("Invalid invisibility-effect value.");
            var unitState = BitConverter.ToUInt64(Read(At(hero, 0x11C0, 8), 8));
            foreach (var guard in guards)
            {
                if (!_memory.TryReadBytes(guard.Address, guard.Bytes.Length, out var again)
                    || !again.AsSpan().SequenceEqual(guard.Bytes))
                    throw new ReadFailure("Modifier context changed during acquisition; sample discarded.");
            }
            if (!contextStillCurrent()) throw new ReadFailure("Local hero or match changed during acquisition.");
            snapshot = new(count, nullEntries, rows.AsReadOnly(),
                new(BitConverter.ToUInt16(cache, 0), BitConverter.ToUInt16(cache, 2),
                    BitConverter.ToUInt16(cache, 4), BitConverter.ToUInt16(cache, 6)),
                invisibility, unitState.ToString("X16"));
            detail = "Observed local modifiers only; neither their names nor property caches establish enemy sight.";
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
