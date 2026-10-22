using DotaUtility.Memory;
using DotaUtility.ModifierAudit;

namespace DotaUtility.Dota2.Tests;

public sealed class ModifierSourceAttributionTests
{
    private const ulong SystemAddress = 0x20000;
    private const ulong Chunk = 0x30000;
    private const ulong CasterEntity = 0x40000;
    private const ulong ModuleBase = 0x10000000;
    private const ulong Vtable = ModuleBase + 0x1000;
    private const ulong FirstMethod = ModuleBase + 0x2000;
    private const int CasterIndex = 25;
    private const uint CasterHandle = 0x18019;

    [Fact]
    public void TryRead_ResolvesUniqueCasterWithoutSubstitutingModifierTeamHint()
    {
        var memory = CreateScenario(rawTeam: 2, kind: 3, ownerHandle: 0x18005);
        var module = new ProcessModuleSnapshot("client.dll", "fixture", ModuleBase, 0x10000);
        var reader = new ModifierCasterReader(memory);
        var modifiers = new[]
        {
            new OwnerModifier("modifier_test", ".?AVCDOTA_Buff@@", 1, 0, 0x18021, CasterHandle, uint.MaxValue, false, ClientCachedTeamHint: 3),
        };

        Assert.True(reader.TryRead(SystemAddress, modifiers, module, () => true, out var observations, out var detail), detail);
        var obs = Assert.Single(observations!);
        Assert.Equal(CasterHandle, obs.Handle);
        Assert.Equal("resolved", obs.Availability);
        Assert.Equal((byte)2, obs.RawEntityTeam);
        Assert.Equal((byte)3, obs.NativeEntityKind);
        Assert.Equal(0x18005u, obs.OwnerHandle);
        Assert.Null(obs.EffectiveTeam);
        Assert.False(obs.IsEnemySightEvidence);
    }

    [Fact]
    public void TryRead_ReportsUnavailableCasterWithoutFailingOrSubstitutingParentTeam()
    {
        var memory = CreateScenario(rawTeam: 2, kind: 0, ownerHandle: uint.MaxValue);
        var module = new ProcessModuleSnapshot("client.dll", "fixture", ModuleBase, 0x10000);
        var reader = new ModifierCasterReader(memory);
        var modifiers = new[]
        {
            new OwnerModifier("modifier_test", ".?AVCDOTA_Buff@@", 1, 0, 0x18021, 0x28099, uint.MaxValue, false, ClientCachedTeamHint: 3),
            new OwnerModifier("modifier_self", ".?AVCDOTA_Buff@@", 2, 1, 0x18021, uint.MaxValue, uint.MaxValue, false, ClientCachedTeamHint: 3),
        };

        Assert.True(reader.TryRead(SystemAddress, modifiers, module, () => true, out var observations, out _));
        Assert.Equal(2, observations!.Count);
        Assert.Equal("unavailable", observations[0].Availability);
        Assert.Null(observations[0].RawEntityTeam);
        Assert.Equal("invalid_handle", observations[1].Availability);
    }

    [Fact]
    public void Comparer_DetectsModifierListOrUnitStateChange()
    {
        var mod = new OwnerModifier("modifier_a", ".?AVCDOTA_Buff@@", 1, 0, 0x18021, CasterHandle, uint.MaxValue, false);
        var cache = new ModifierTeamCache(0, 0, 0, 0);
        var a = new OwnerModifierSnapshot(1, 0, [mod], cache, 0f, "0000000000000000");
        var b = new OwnerModifierSnapshot(1, 0, [mod], cache, 0f, "0000000000000000");
        var c = new OwnerModifierSnapshot(1, 0, [mod], cache, 0f, "0000000000000001");

        Assert.True(OwnerModifierSnapshotComparer.Equivalent(a, b));
        Assert.False(OwnerModifierSnapshotComparer.Equivalent(a, c));
        Assert.False(OwnerModifierSnapshotComparer.Equivalent(a, null));
    }

    private static FakeProcessMemoryAccessor CreateScenario(byte rawTeam, byte kind, uint ownerHandle)
    {
        var m = new FakeProcessMemoryAccessor();
        m.TryAttach(1337);
        var offsets = DotaOffsets.Current;
        m.WriteBytes(SystemAddress + 0x10, new byte[512]);
        m.Write(SystemAddress + (ulong)offsets.EntityListFirstChunkOffset, Chunk);
        var entry = Chunk + (ulong)(CasterIndex * offsets.EntityListEntryStride);
        m.WriteBytes(entry, new byte[offsets.EntityListEntryStride]);
        m.Write(entry, CasterEntity);
        m.Write(entry + (ulong)offsets.EntityIdentityHandle, CasterHandle);

        m.WriteBytes(CasterEntity, new byte[0x600]);
        m.Write(CasterEntity, Vtable);
        m.Write(CasterEntity + 0x10, entry);
        m.Write(CasterEntity + 0x3E7, rawTeam);
        m.Write(CasterEntity + 0x5D0, kind);
        m.Write(CasterEntity + 0x514, ownerHandle);

        m.Write(Vtable, FirstMethod);
        m.Write(FirstMethod, (byte)0xC3);
        return m;
    }
}
