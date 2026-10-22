using System.Text;
using DotaUtility.Memory;
using DotaUtility.ModifierAudit;
using Xunit;

namespace DotaUtility.Dota2.Tests;

public sealed class OwnerModifierReaderTests
{
    private sealed class Fixture
    {
        internal const ulong Hero = 1048576uL;
        internal const ulong Manager = 1051960uL;
        internal const ulong Array = 2097152uL;
        internal const ulong Buff = 3145728uL;
        internal const ulong NameAddress = 4194304uL;
        internal const ulong ModuleBase = 6442450944uL;
        internal const ulong Vtable = 6442455040uL;
        internal const ulong Col = 6442463232uL;
        internal const ulong TypeAddress = 6442467344uL;
        internal const uint Handle = 98337u;

        private readonly ProcessModuleSnapshot _module = new("client.dll", "fixture", 6442450944uL, 65536);
        private readonly OwnerModifierReader _reader;

        internal FakeProcessMemoryAccessor M { get; } = new();

        internal Fixture()
        {
            _reader = new OwnerModifierReader(M);
            M.WriteBytes(1051968uL, new byte[28]);
            M.Write(1051968uL, 1048576uL);
            M.Write(1051976uL, 1);
            M.Write(1051984uL, 2097152uL);
            M.Write(1051992uL, 2);
            M.Write(2097152uL, 3145728uL);
            M.WriteBytes(3145728uL, new byte[160]);
            M.Write(3145728uL, 6442455040uL);
            M.Write(3145768uL, 4194304uL);
            M.Write(3145776uL, 1051960uL);
            M.Write(3145800uL, 17);
            M.Write(3145844uL, 98337u);
            M.Write(3145832uL, 98337u);
            M.Write(3145840uL, uint.MaxValue);
            M.WriteBytes(6442455032uL, new byte[16]);
            M.Write(6442455032uL, 6442463232uL);
            M.Write(6442455040uL, 6442479616uL);
            M.WriteBytes(6442463232uL, new byte[24]);
            M.Write(6442463232uL, 1u);
            M.Write(6442463244uL, 16384u);
            M.Write(6442463252uL, 12288u);
            Type(".?AVCDOTA_Modifier_Test@@");
            Name("modifier_test");
            M.WriteBytes(1052850uL, new byte[8]);
            M.Write(1051748uL, 1f);
            M.Write(1053120uL, 128uL);
        }

        internal void Name(string name)
        {
            M.WriteBytes(4194304uL, new byte[192]);
            M.WriteBytes(4194304uL, Encoding.ASCII.GetBytes(name));
        }

        internal void Type(string type)
        {
            M.WriteBytes(6442467344uL, new byte[192]);
            M.WriteBytes(6442467344uL, Encoding.ASCII.GetBytes(type));
        }

        internal OwnerModifierSnapshot Read()
        {
            Assert.True(_reader.TryRead(1048576uL, 98337u, _module, () => true, out var snapshot, out var detail), detail);
            return Assert.IsType<OwnerModifierSnapshot>(snapshot);
        }

        internal void Rejects(Func<bool>? stable = null)
        {
            Assert.False(_reader.TryRead(1048576uL, 98337u, _module, stable ?? (() => true), out var snapshot, out var detail));
            Assert.Null(snapshot);
            Assert.NotEmpty(detail);
        }
    }

    [Fact]
    public void ReadsOwnerBoundModifierWithoutClaimingEnemySight()
    {
        var fixture = new Fixture();
        var ownerModifierSnapshot = fixture.Read();
        Assert.Single(ownerModifierSnapshot.Modifiers);
        Assert.Equal("modifier_test", ownerModifierSnapshot.Modifiers[0].Name);
        Assert.Equal(98337u, ownerModifierSnapshot.Modifiers[0].ParentHandle);
        Assert.Equal("Unknown", ownerModifierSnapshot.CurrentHeroVisibility);
        Assert.False(ownerModifierSnapshot.CanDriveCurrentOverlay);
    }

    [Theory]
    [InlineData("modifier_item_gem_of_true_sight")]
    [InlineData("modifier_truesight")]
    [InlineData("modifier_slark_shadow_dance_passive_regen")]
    [InlineData("modifier_invisible")]
    [InlineData("modifier_item_radiance_debuff")]
    public void ModifierNamesAreNotVisibilityAssertions(string name)
    {
        var fixture = new Fixture();
        fixture.Name(name);
        var ownerModifierSnapshot = fixture.Read();
        Assert.Equal(98337u, ownerModifierSnapshot.Modifiers[0].CasterHandle);
        Assert.Equal("Unknown", ownerModifierSnapshot.CurrentHeroVisibility);
        Assert.False(ownerModifierSnapshot.CanDriveCurrentOverlay);
    }

    [Fact]
    public void EmptyListRemainsUnknown()
    {
        var fixture = new Fixture();
        fixture.M.Write(1051976uL, 0);
        fixture.M.Write(1051984uL, 0uL);
        fixture.M.Write(1051992uL, 0);
        var ownerModifierSnapshot = fixture.Read();
        Assert.Empty(ownerModifierSnapshot.Modifiers);
        Assert.Equal("Unknown", ownerModifierSnapshot.CurrentHeroVisibility);
    }

    [Fact]
    public void NullTombstoneIsRecordedNotDereferenced()
    {
        var fixture = new Fixture();
        fixture.M.Write(2097152uL, 0uL);
        var ownerModifierSnapshot = fixture.Read();
        Assert.Equal(1, ownerModifierSnapshot.NullEntries);
        Assert.Empty(ownerModifierSnapshot.Modifiers);
    }

    [Fact]
    public void PendingRemovalIsPreservedNotTreatedAsAnActiveEffect()
    {
        var fixture = new Fixture();
        fixture.M.Write(3145886uL, (byte)4);
        var ownerModifierSnapshot = fixture.Read();
        Assert.True(ownerModifierSnapshot.Modifiers[0].PendingRemoval);
        Assert.False(ownerModifierSnapshot.CanDriveCurrentOverlay);
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(257, 300)]
    [InlineData(1, 0)]
    [InlineData(1, 4097)]
    [InlineData(0, -1)]
    public void InvalidArrayBoundsAreRejected(int count, int capacity)
    {
        var fixture = new Fixture();
        fixture.M.Write(1051976uL, count);
        fixture.M.Write(1051992uL, capacity);
        fixture.Rejects();
    }

    [Fact]
    public void WrongManagerOwnerIsRejected()
    {
        var fixture = new Fixture();
        fixture.M.Write(1051968uL, 1114112uL);
        fixture.Rejects();
    }

    [Fact]
    public void WrongModifierManagerIsRejected()
    {
        var fixture = new Fixture();
        fixture.M.Write(3145776uL, 1051968uL);
        fixture.Rejects();
    }

    [Fact]
    public void SameIndexDifferentHeroGenerationIsRejected()
    {
        var fixture = new Fixture();
        fixture.M.Write(3145844uL, 131105u);
        fixture.Rejects();
    }

    [Fact]
    public void MissingCasterAndAbilityAreNotFalseHidden()
    {
        var fixture = new Fixture();
        fixture.M.Write(3145832uL, uint.MaxValue);
        fixture.M.Write(3145840uL, uint.MaxValue);
        Assert.Equal("Unknown", fixture.Read().CurrentHeroVisibility);
    }

    [Fact]
    public void DuplicatePointerIsRejected()
    {
        var fixture = new Fixture();
        fixture.M.Write(1051976uL, 2);
        fixture.M.Write(2097160uL, 3145728uL);
        fixture.Rejects();
    }

    [Fact]
    public void DuplicateNativeInstanceTokenIsRejected()
    {
        var fixture = new Fixture();
        fixture.M.TryReadBytes(3145728uL, 160, out var bytes);
        fixture.M.WriteBytes(3149824uL, bytes);
        fixture.M.Write(1051976uL, 2);
        fixture.M.Write(2097160uL, 3149824uL);
        fixture.Rejects();
    }

    [Fact]
    public void RepeatedNamesWithDifferentInstanceTokensRemainDistinct()
    {
        var fixture = new Fixture();
        fixture.M.TryReadBytes(3145728uL, 160, out var bytes);
        fixture.M.WriteBytes(3149824uL, bytes);
        fixture.M.Write(3149896uL, 99);
        fixture.M.Write(1051976uL, 2);
        fixture.M.Write(2097160uL, 3149824uL);
        Assert.Equal(2, fixture.Read().Modifiers.Count);
    }

    [Fact]
    public void UnreadableEntryIsRejected()
    {
        var fixture = new Fixture();
        fixture.M.Write(2097152uL, 12582912uL);
        fixture.Rejects();
    }

    [Fact]
    public void OverflowingPointerIsRejectedBeforeRead()
    {
        var fixture = new Fixture();
        fixture.M.Write(2097152uL, 140737488355312uL);
        fixture.Rejects();
    }

    [Fact]
    public void NonModifierRttiIsRejected()
    {
        var fixture = new Fixture();
        fixture.Type(".?AVC_DOTA_BaseNPC@@");
        fixture.Rejects();
    }

    [Fact]
    public void SecondaryRttiLocatorIsRejected()
    {
        var fixture = new Fixture();
        fixture.M.Write(6442463236uL, 16u);
        fixture.Rejects();
    }

    [Fact]
    public void RttiLocatorSelfReferenceIsChecked()
    {
        var fixture = new Fixture();
        fixture.M.Write(6442463252uL, 12292u);
        fixture.Rejects();
    }

    [Fact]
    public void RttiOutsideModuleIsRejected()
    {
        var fixture = new Fixture();
        fixture.M.Write(6442455032uL, 15728640uL);
        fixture.Rejects();
    }

    [Theory]
    [InlineData("")]
    [InlineData("other_name")]
    [InlineData("modifier_bad name")]
    public void InvalidModifierIdentifierIsRejected(string name)
    {
        var fixture = new Fixture();
        fixture.Name(name);
        fixture.Rejects();
    }

    [Fact]
    public void UnterminatedNameIsRejected()
    {
        var fixture = new Fixture();
        fixture.M.WriteBytes(4194304uL, Enumerable.Repeat((byte)97, 192).ToArray());
        fixture.Rejects();
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(-0.1f)]
    [InlineData(1.1f)]
    public void InvalidEffectIntensityIsRejected(float value)
    {
        var fixture = new Fixture();
        fixture.M.Write(1051748uL, value);
        fixture.Rejects();
    }

    [Fact]
    public void MissingInitialContextPerformsNoModifierReads()
    {
        var fixture = new Fixture();
        var reads = 0;
        fixture.M.BeforeRead = (_, _) => reads++;
        fixture.Rejects(() => false);
        Assert.Equal(0, reads);
    }

    [Fact]
    public void ChangedContextAfterReadsDiscardsSnapshot()
    {
        var fixture = new Fixture();
        var calls = 0;
        fixture.Rejects(() => ++calls == 1);
    }

    [Theory]
    [InlineData("manager")]
    [InlineData("array")]
    [InlineData("token")]
    [InlineData("name")]
    public void ObservedConcurrentChangesDiscardSnapshot(string kind)
    {
        var f = new Fixture();
        var reads = 0;
        var target = kind switch
        {
            "manager" => 1051968uL,
            "array" => 2097152uL,
            "token" => 3145728uL,
            _ => 4194304uL,
        };
        f.M.BeforeRead = (address, _) =>
        {
            if (address == target && ++reads == 2)
            {
                switch (kind)
                {
                    case "manager":
                        f.M.Write(1051976uL, 0);
                        break;
                    case "array":
                        f.M.Write(2097152uL, 0uL);
                        break;
                    case "token":
                        f.M.Write(3145800uL, 999);
                        break;
                    default:
                        f.Name("modifier_changed");
                        break;
                }
            }
        };
        f.Rejects();
    }

    [Fact]
    public void CacheValidityIsRequiredEvenWhenValueBitIsSet()
    {
        Assert.Null(new ModifierTeamCache(8, 0, 0, 0).TrueSightPropertyForTeam(3));
        Assert.Null(new ModifierTeamCache(0, 0, 0, 0).TrueSightPropertyForTeam(3));
        Assert.True(new ModifierTeamCache(8, 8, 0, 0).TrueSightPropertyForTeam(3));
        Assert.False(new ModifierTeamCache(0, 8, 0, 0).TrueSightPropertyForTeam(3));
        Assert.Null(new ModifierTeamCache(ushort.MaxValue, ushort.MaxValue, 0, 0).TrueSightPropertyForTeam(15));
        Assert.Null(new ModifierTeamCache(ushort.MaxValue, ushort.MaxValue, 0, 0).TrueSightPropertyForTeam(-1));
    }

    [Fact]
    public void ValidTrueSightCacheStillDoesNotDriveVisibilityOverlay()
    {
        var fixture = new Fixture();
        fixture.M.Write(1052850uL, (ushort)8);
        fixture.M.Write(1052852uL, (ushort)8);
        var ownerModifierSnapshot = fixture.Read();
        Assert.True(ownerModifierSnapshot.TeamCache.TrueSightPropertyForTeam(3));
        Assert.Equal("Unknown", ownerModifierSnapshot.CurrentHeroVisibility);
        Assert.False(ownerModifierSnapshot.CanDriveCurrentOverlay);
    }
}
