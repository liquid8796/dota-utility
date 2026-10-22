using DotaUtility.Domain;

namespace DotaUtility.Dota2.Tests;

public sealed class EntityIdentityConsistencyTests
{
    private const ulong SystemAddress = 0x1000;
    private const ulong Chunk = 0x10000;
    private const ulong Controller = 0x20000;
    private const ulong Hero = 0x30000;
    private const ulong TeamData = 0x40000;
    private const ulong TeamArray = 0x50000;
    private const ulong NetworkClient = 0x60000;
    private const ulong EntityGlobal = 0x800;
    private const ulong NetworkGlobal = 0x808;
    private const int HeroIndex = 33;
    private const int DataIndex = 55;
    private const uint HeroHandle = 0x18021;
    private const uint DataHandle = 0x18037;
    private static DotaOffsets Offsets => DotaOffsets.Current;
    private static ulong Entry(int index) => Chunk + (ulong)(index * Offsets.EntityListEntryStride);
    private static ulong StoredHandle(int index) => Entry(index) + (ulong)Offsets.EntityIdentityHandle;
    private static ulong VisibilityWord => TeamData + (ulong)Offsets.DataNonSpectatorNpcVisibleState;
    private static ulong AssignedHero => Controller + (ulong)Offsets.PlayerControllerAssignedHero;
    private static ulong EnemyHandleSlot => TeamArray + 3UL * sizeof(uint);

    [Fact]
    public void HandleResolver_RejectsRecycledSlotWithSameIndex()
    {
        var f = Seed();
        f.Memory.Write(StoredHandle(HeroIndex), HeroHandle + 0x8000u);
        Assert.Equal(0UL, f.Entities.ResolveByHandle(SystemAddress, HeroHandle));
    }

    [Fact]
    public void HandleResolver_AcceptsNewGenerationAfterReacquisition()
    {
        var f = Seed();
        var next = HeroHandle + 0x8000u;
        f.Memory.Write(StoredHandle(HeroIndex), next);
        Assert.Equal(Hero, f.Entities.ResolveByHandle(SystemAddress, next));
    }

    [Fact]
    public void HandleResolver_RejectsUnreadableStoredHandle()
    {
        var memory = new FakeProcessMemoryAccessor();
        memory.Write(SystemAddress + (ulong)Offsets.EntityListFirstChunkOffset, Chunk);
        memory.Write(Entry(HeroIndex), Hero);
        var resolver = new Source2EntityResolver(memory, Offsets);
        Assert.Equal(0UL, resolver.ResolveByHandle(SystemAddress, HeroHandle));
    }

    [Fact]
    public void IndexEnumeration_IsNotOldHandleValidation()
    {
        var f = Seed();
        f.Memory.Write(StoredHandle(HeroIndex), HeroHandle + 0x8000u);
        Assert.Equal(Hero, f.Entities.ResolveByIndex(SystemAddress, HeroIndex));
        Assert.Equal(0UL, f.Entities.ResolveByHandle(SystemAddress, HeroHandle));
    }

    [Fact]
    public void HandleResolver_RejectsGenerationChangeDuringPointerRead()
    {
        var f = Seed();
        f.Memory.BeforeRead = (address, _) =>
        {
            if (address == Entry(HeroIndex))
                f.Memory.Write(StoredHandle(HeroIndex), HeroHandle + 0x8000u);
        };
        Assert.Equal(0UL, f.Entities.ResolveByHandle(SystemAddress, HeroHandle));
    }

    [Fact]
    public void HandleResolver_RejectsPointerChangeDuringConfirmation()
    {
        var f = Seed();
        var pointerReads = 0;
        f.Memory.BeforeRead = (address, _) =>
        {
            if (address == Entry(HeroIndex) && ++pointerReads == 2)
                f.Memory.Write(Entry(HeroIndex), Hero + 0x1000);
        };
        Assert.Equal(0UL, f.Entities.ResolveByHandle(SystemAddress, HeroHandle));
    }

    [Fact]
    public void HandleResolver_RejectsMinusTwoEvenWhenItsIndexedSlotIsPopulated()
    {
        var memory = new FakeProcessMemoryAccessor();
        const int index = 0x7FFE;
        var chunkIndex = index >> Offsets.EntityListChunkShift;
        var within = index & Offsets.EntityListChunkMask;
        var entry = Chunk + (ulong)(within * Offsets.EntityListEntryStride);
        memory.Write(SystemAddress + (ulong)Offsets.EntityListFirstChunkOffset
            + (ulong)(chunkIndex * Offsets.EntityListChunkPointerStride), Chunk);
        memory.Write(entry, Hero);
        memory.Write(entry + (ulong)Offsets.EntityIdentityHandle, 0xFFFFFFFEu);
        Assert.Equal(0UL, new Source2EntityResolver(memory, Offsets).ResolveByHandle(SystemAddress, 0xFFFFFFFEu));
    }

    [Fact]
    public void TeamReader_RejectsStaleTeamDataGeneration()
    {
        var f = Seed();
        f.Memory.Write(StoredHandle(DataIndex), DataHandle + 0x8000u);
        Assert.False(ReadTeam(f));
    }

    [Fact]
    public void TeamReader_RejectsTableChangeDuringBitmapRead()
    {
        var f = Seed();
        OnBitmapRead(f, () => f.Memory.Write(EnemyHandleSlot, uint.MaxValue));
        Assert.False(ReadTeam(f));
    }

    [Fact]
    public void TeamReader_RejectsGenerationChangeDuringBitmapRead()
    {
        var f = Seed();
        OnBitmapRead(f, () => f.Memory.Write(StoredHandle(DataIndex), DataHandle + 0x8000u));
        Assert.False(ReadTeam(f));
    }

    [Fact]
    public void TeamReader_RejectsTeamChangeDuringBitmapRead()
    {
        var f = Seed();
        OnBitmapRead(f, () => f.Memory.Write(TeamData + (ulong)Offsets.BaseEntityTeamNumber, (byte)2));
        Assert.False(ReadTeam(f));
    }

    [Fact]
    public async Task Probe_RejectsAssignedHeroChangeDuringBitmapRead()
    {
        var f = Seed();
        OnBitmapRead(f, () => f.Memory.Write(AssignedHero, uint.MaxValue));
        await AssertContextRejected(f);
    }

    [Fact]
    public async Task Probe_RejectsSamePidReattachmentDuringBitmapRead()
    {
        var f = Seed();
        OnBitmapRead(f, () => f.Memory.TryAttach(1337));
        var snapshot = await f.Probe.ProbeAsync(CancellationToken.None);
        Assert.Equal(VisibilityState.Unknown, snapshot.State);
        Assert.Null(snapshot.HeroAddress);
        Assert.Contains("attachment changed", snapshot.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Probe_RejectsProcessSwitchDuringBitmapRead()
    {
        var f = Seed();
        OnBitmapRead(f, () => f.Memory.TryAttach(1338));
        var snapshot = await f.Probe.ProbeAsync(CancellationToken.None);
        Assert.Equal(VisibilityState.Unknown, snapshot.State);
        Assert.Null(snapshot.HeroAddress);
        Assert.Contains("attachment changed", snapshot.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Probe_RejectsHeroGenerationChangeDuringBitmapRead()
    {
        var f = Seed();
        OnBitmapRead(f, () => f.Memory.Write(StoredHandle(HeroIndex), HeroHandle + 0x8000u));
        await AssertContextRejected(f);
    }

    [Fact]
    public async Task Probe_RejectsHeroTeamChangeDuringBitmapRead()
    {
        var f = Seed();
        OnBitmapRead(f, () => f.Memory.Write(Hero + (ulong)Offsets.BaseEntityTeamNumber, (byte)3));
        await AssertContextRejected(f);
    }

    [Fact]
    public async Task Probe_RejectsEntitySystemChangeDuringBitmapRead()
    {
        var f = Seed();
        OnBitmapRead(f, () => f.Memory.Write(EntityGlobal, SystemAddress + 0x1000));
        await AssertContextRejected(f);
    }

    [Fact]
    public async Task Probe_DashboardThenFreshGenerationDoesNotReuseOldHeroHandle()
    {
        var f = Seed();
        Assert.Equal(VisibilityState.Visible, (await f.Probe.ProbeAsync(CancellationToken.None)).State);
        f.Memory.Write(AssignedHero, uint.MaxValue);
        var idle = await f.Probe.ProbeAsync(CancellationToken.None);
        Assert.Equal(VisibilityState.Unknown, idle.State);
        Assert.Null(idle.HeroAddress);
        var next = HeroHandle + 0x8000u;
        f.Memory.Write(StoredHandle(HeroIndex), next);
        f.Memory.Write(AssignedHero, next);
        Assert.Equal(VisibilityState.Visible, (await f.Probe.ProbeAsync(CancellationToken.None)).State);
    }

    [Fact]
    public async Task Probe_MissingEnemyDataDoesNotBecomeHidden()
    {
        var f = Seed();
        f.Memory.Write(EnemyHandleSlot, uint.MaxValue);
        Assert.Equal(VisibilityState.Unknown, (await f.Probe.ProbeAsync(CancellationToken.None)).State);
    }

    [Fact]
    public void TeamReader_UnreadableWordIsNotAHiddenSample()
    {
        var f = Seed();
        Assert.False(f.Reader.TryReadEnemyVisibility(TeamArray, SystemAddress, 3, 100, out _, out _));
    }

    private static bool ReadTeam(Fixture f) =>
        f.Reader.TryReadEnemyVisibility(TeamArray, SystemAddress, 3, HeroIndex, out _, out _);

    private static void OnBitmapRead(Fixture f, Action mutation)
    {
        f.Memory.BeforeRead = (address, _) =>
        {
            if (address == VisibilityWord) mutation();
        };
    }

    private static async Task AssertContextRejected(Fixture f)
    {
        var snapshot = await f.Probe.ProbeAsync(CancellationToken.None);
        Assert.Equal(VisibilityState.Unknown, snapshot.State);
        Assert.Null(snapshot.HeroAddress);
        Assert.Contains("identity changed", snapshot.Detail, StringComparison.OrdinalIgnoreCase);
    }

    private static Fixture Seed()
    {
        var m = new FakeProcessMemoryAccessor();
        m.TryAttach(1337);
        m.Write(EntityGlobal, SystemAddress);
        m.Write(NetworkGlobal, NetworkClient);
        m.Write(NetworkClient + (ulong)Offsets.NetworkGameClientLocalPlayer, 11);
        m.Write(SystemAddress + (ulong)Offsets.EntityListFirstChunkOffset, Chunk);
        m.Write(Entry(12), Controller);
        m.Write(StoredHandle(12), 0x1800Cu);
        m.Write(0x900UL, Controller);
        m.Write(Controller + (ulong)Offsets.EntityInstanceIdentity, Entry(12));
        m.Write(Controller + (ulong)Offsets.PlayerControllerIsLocalPlayer, (byte)1);
        m.Write(Controller + (ulong)Offsets.PlayerControllerIsHltv, (byte)0);
        m.Write(Controller + (ulong)Offsets.BaseEntityTeamNumber, (byte)2);
        m.Write(Controller + (ulong)Offsets.PlayerControllerPlayerId, 5);
        m.Write(AssignedHero, HeroHandle);
        m.Write(Entry(HeroIndex), Hero);
        m.Write(StoredHandle(HeroIndex), HeroHandle);
        m.Write(Hero + (ulong)Offsets.BaseEntityTeamNumber, (byte)2);
        m.Write(Hero + (ulong)Offsets.BaseModelEntityTeamVisibilityBitmask, 0);
        m.Write(Entry(DataIndex), TeamData);
        m.Write(StoredHandle(DataIndex), DataHandle);
        m.Write(EnemyHandleSlot, DataHandle);
        m.Write(TeamData + (ulong)Offsets.BaseEntityTeamNumber, (byte)3);
        m.Write(VisibilityWord, 1UL << HeroIndex);
        var addresses = new DotaResolvedAddresses(EntityGlobal, NetworkGlobal, TeamArray, 0x900);
        return new Fixture(m, new Source2EntityResolver(m, Offsets), new ClientTeamVisibilityReader(m, Offsets),
            new DotaUtilityProbe(m, new FixedResolver(addresses), Offsets));
    }

    private sealed record Fixture(FakeProcessMemoryAccessor Memory, Source2EntityResolver Entities,
        ClientTeamVisibilityReader Reader, DotaUtilityProbe Probe);

    private sealed class FixedResolver(DotaResolvedAddresses addresses) : IDotaAddressResolver
    {
        public bool TryResolve(out DotaResolvedAddresses result, out string detail)
        {
            result = addresses;
            detail = "Fixture addresses.";
            return true;
        }
    }
}