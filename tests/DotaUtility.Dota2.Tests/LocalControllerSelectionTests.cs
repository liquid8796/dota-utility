using DotaUtility.Domain;

namespace DotaUtility.Dota2.Tests;

public sealed class LocalControllerSelectionTests
{
    private const ulong SystemAddress = 0x10000;
    private const ulong Chunk = 0x20000;
    private const ulong LocalController = 0x30000;
    private const ulong DecoyController = 0x32000;
    private const ulong LocalHero = 0x40000;
    private const ulong DecoyHero = 0x43000;
    private const ulong OwnData = 0x50000;
    private const ulong EnemyData = 0x54000;
    private const ulong Network = 0x60000;
    private const ulong NativeArray = 0x4000;
    private const ulong NetworkGlobal = 0x4010;
    private const ulong EntityGlobal = 0x4018;
    private const ulong TeamArray = 0x5000;
    private static DotaOffsets O => DotaOffsets.Current;
    private static ulong Word => EnemyData + (ulong)O.DataNonSpectatorNpcVisibleState;
    private static ulong Entry(int i) => Chunk + (ulong)(i * O.EntityListEntryStride);

    [Fact]
    public async Task NativeLocalControllerWinsOverPlayerIdNumericallyEqualToNetworkSlot()
    {
        var f = Seed();
        var s = await f.Probe.ProbeAsync(CancellationToken.None);
        Assert.Equal(LocalHero, s.HeroAddress);
        Assert.Equal(9, s.LocalPlayerId);
        Assert.Equal(3, s.LocalTeam);
        Assert.Equal(2, s.EnemyTeam);
        Assert.Equal(VisibilityState.Visible, s.State);
        Assert.Contains("Local Dota PlayerID 9", s.Detail, StringComparison.Ordinal);
        Assert.Contains("controller entity #5", s.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrimarySplitScreenArrayIsNotIndexedByNetworkSlot()
    {
        var f = Seed();
        f.Memory.Write(NativeArray + 4UL * sizeof(ulong), DecoyController);
        Assert.Equal(LocalHero, (await f.Probe.ProbeAsync(CancellationToken.None)).HeroAddress);
    }

    [Fact]
    public async Task MissingEnemyDataDoesNotUseOwnTeamBitmapForAnotherHero()
    {
        var f = Seed();
        f.Memory.Write(TeamArray + 2UL * sizeof(uint), uint.MaxValue);
        var s = await f.Probe.ProbeAsync(CancellationToken.None);
        Assert.Equal(VisibilityState.Unknown, s.State);
        Assert.Equal(LocalHero, s.HeroAddress);
        Assert.Equal(9, s.LocalPlayerId);
        Assert.Contains("team 2", s.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ClearLocalHeroBitRemainsHiddenDespiteVisibleDecoyBit()
    {
        var f = Seed();
        f.Memory.Write(Word, 1UL << 34);
        Assert.Equal(VisibilityState.Hidden, (await f.Probe.ProbeAsync(CancellationToken.None)).State);
    }

    [Theory]
    [InlineData(0ul)]
    [InlineData(0xFFFF_FFFF_FFFF_FFFFul)]
    public async Task InvalidNativePointerNeverFallsBackToRemotePlayer(ulong pointer)
    {
        var f = Seed();
        f.Memory.Write(NativeArray, pointer);
        await AssertUnavailable(f);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task NativeControllerMustHaveExactLocalFlag(int flag)
    {
        var f = Seed();
        f.Memory.Write(LocalController + (ulong)O.PlayerControllerIsLocalPlayer, (byte)flag);
        await AssertUnavailable(f);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task HltvOrInvalidRoleIsNotNormalPlayerContext(int flag)
    {
        var f = Seed();
        f.Memory.Write(LocalController + (ulong)O.PlayerControllerIsHltv, (byte)flag);
        await AssertUnavailable(f);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(64)]
    public async Task InvalidDotaPlayerIdDoesNotUseNetworkSlotAsReplacement(int id)
    {
        var f = Seed();
        f.Memory.Write(LocalController + (ulong)O.PlayerControllerPlayerId, id);
        await AssertUnavailable(f);
    }

    [Fact]
    public async Task SameNumericSlotAndPlayerIdStillUsesNativePointer()
    {
        var f = Seed();
        f.Memory.Write(LocalController + (ulong)O.PlayerControllerPlayerId, 4);
        var s = await f.Probe.ProbeAsync(CancellationToken.None);
        Assert.Equal(LocalHero, s.HeroAddress);
        Assert.Equal(4, s.LocalPlayerId);
    }

    [Fact]
    public async Task ControllerAndAssignedHeroTeamMismatchIsRejected()
    {
        var f = Seed();
        f.Memory.Write(LocalController + (ulong)O.BaseEntityTeamNumber, (byte)2);
        await AssertUnavailable(f);
    }

    [Fact]
    public async Task UnreadableControllerIdentityIsRejected()
    {
        var f = Seed();
        f.Memory.Write(LocalController + (ulong)O.EntityInstanceIdentity, 0UL);
        await AssertUnavailable(f);
    }

    [Theory]
    [InlineData("nativePointer")]
    [InlineData("localFlag")]
    [InlineData("playerId")]
    [InlineData("controllerGeneration")]
    [InlineData("hltv")]
    public async Task LocalIdentityChangesDuringBitmapReadInvalidateSample(string change)
    {
        var f = Seed();
        f.Memory.BeforeRead = (address, _) =>
        {
            if (address != Word) return;
            switch (change)
            {
                case "nativePointer": f.Memory.Write(NativeArray, DecoyController); break;
                case "localFlag": f.Memory.Write(LocalController + (ulong)O.PlayerControllerIsLocalPlayer, (byte)0); break;
                case "playerId": f.Memory.Write(LocalController + (ulong)O.PlayerControllerPlayerId, 10); break;
                case "controllerGeneration": f.Memory.Write(Entry(5) + (ulong)O.EntityIdentityHandle, 0x20005u); break;
                case "hltv": f.Memory.Write(LocalController + (ulong)O.PlayerControllerIsHltv, (byte)1); break;
            }
        };
        var s = await f.Probe.ProbeAsync(CancellationToken.None);
        Assert.Equal(VisibilityState.Unknown, s.State);
        Assert.Null(s.HeroAddress);
        Assert.Contains("identity changed", s.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NativeControllerCanBeReacquiredAfterDashboardAndPlayerChange()
    {
        var f = Seed();
        Assert.Equal(LocalHero, (await f.Probe.ProbeAsync(CancellationToken.None)).HeroAddress);
        f.Memory.Write(NativeArray, 0UL);
        await AssertUnavailable(f);
        f.Memory.Write(LocalController + (ulong)O.PlayerControllerIsLocalPlayer, (byte)0);
        f.Memory.Write(DecoyController + (ulong)O.PlayerControllerIsLocalPlayer, (byte)1);
        f.Memory.Write(NativeArray, DecoyController);
        f.Memory.Write(Network + (ulong)O.NetworkGameClientLocalPlayer, 0);
        var s = await f.Probe.ProbeAsync(CancellationToken.None);
        Assert.Equal(DecoyHero, s.HeroAddress);
        Assert.Equal(4, s.LocalPlayerId);
        Assert.Equal(2, s.LocalTeam);
    }

    [Fact]
    public async Task ControllerSwitchDuringInitialAcquisitionIsRejected()
    {
        var f = Seed();
        var reads = 0;
        f.Memory.BeforeRead = (address, _) =>
        {
            if (address == NativeArray && ++reads == 2) f.Memory.Write(NativeArray, DecoyController);
        };
        await AssertUnavailable(f);
    }

    private static async Task AssertUnavailable(Fixture f)
    {
        var s = await f.Probe.ProbeAsync(CancellationToken.None);
        Assert.Equal(VisibilityState.Unknown, s.State);
        Assert.Null(s.HeroAddress);
        Assert.Null(s.LocalPlayerId);
    }

    private static Fixture Seed()
    {
        var m = new FakeProcessMemoryAccessor();
        m.TryAttach(1337);
        m.Write(EntityGlobal, SystemAddress);
        m.Write(NetworkGlobal, Network);
        m.Write(Network + (ulong)O.NetworkGameClientLocalPlayer, 4);
        m.Write(SystemAddress + (ulong)O.EntityListFirstChunkOffset, Chunk);
        m.Write(NativeArray, LocalController);
        Bind(m, 5, 0x18005u, LocalController);
        Bind(m, 1, 0x18001u, DecoyController);
        Bind(m, 33, 0x18021u, LocalHero);
        Bind(m, 34, 0x18022u, DecoyHero);
        Bind(m, 55, 0x18037u, EnemyData);
        Bind(m, 56, 0x18038u, OwnData);
        Controller(m, LocalController, 9, 3, 0x18021u, true);
        Controller(m, DecoyController, 4, 2, 0x18022u, false);
        m.Write(LocalHero + (ulong)O.BaseEntityTeamNumber, (byte)3);
        m.Write(DecoyHero + (ulong)O.BaseEntityTeamNumber, (byte)2);
        m.Write(LocalHero + (ulong)O.BaseModelEntityTeamVisibilityBitmask, 0);
        m.Write(EnemyData + (ulong)O.BaseEntityTeamNumber, (byte)2);
        m.Write(OwnData + (ulong)O.BaseEntityTeamNumber, (byte)3);
        m.Write(TeamArray + 2UL * sizeof(uint), 0x18037u);
        m.Write(TeamArray + 3UL * sizeof(uint), 0x18038u);
        m.Write(Word, 1UL << 33);
        m.Write(OwnData + (ulong)O.DataNonSpectatorNpcVisibleState, 1UL << 34);
        var a = new DotaResolvedAddresses(EntityGlobal, NetworkGlobal, TeamArray, NativeArray);
        return new Fixture(m, new DotaUtilityProbe(m, new FixedResolver(a), O));
    }

    private static void Bind(FakeProcessMemoryAccessor m, int index, uint handle, ulong entity)
    {
        m.Write(Entry(index), entity);
        m.Write(Entry(index) + (ulong)O.EntityIdentityHandle, handle);
        m.Write(entity + (ulong)O.EntityInstanceIdentity, Entry(index));
    }

    private static void Controller(FakeProcessMemoryAccessor m, ulong address, int playerId, byte team, uint hero, bool local)
    {
        m.Write(address + (ulong)O.PlayerControllerPlayerId, playerId);
        m.Write(address + (ulong)O.BaseEntityTeamNumber, team);
        m.Write(address + (ulong)O.PlayerControllerAssignedHero, hero);
        m.Write(address + (ulong)O.PlayerControllerIsLocalPlayer, local ? (byte)1 : (byte)0);
        m.Write(address + (ulong)O.PlayerControllerIsHltv, (byte)0);
    }

    private sealed record Fixture(FakeProcessMemoryAccessor Memory, DotaUtilityProbe Probe);
    private sealed class FixedResolver(DotaResolvedAddresses addresses) : IDotaAddressResolver
    {
        public bool TryResolve(out DotaResolvedAddresses result, out string detail)
        {
            result = addresses;
            detail = "Native-controller test fixture.";
            return true;
        }
    }
}
