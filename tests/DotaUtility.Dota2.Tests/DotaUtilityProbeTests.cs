using System.Buffers.Binary;
using System.Numerics;
using DotaUtility.Domain;
using DotaUtility.Memory;

namespace DotaUtility.Dota2.Tests;

public sealed class DotaUtilityProbeTests
{
    [Fact]
    public async Task ProbeAsync_UsesEnemyTeamNpcVisibilityBitmapWhenLegacyMaskIsZero()
    {
        var memory = new FakeProcessMemoryAccessor();
        var offsets = DotaOffsets.Current;
        var addresses = SeedScenario(
            memory,
            offsets,
            enemyCanSeeHero: true,
            legacyVisibilityMask: 0);
        var probe = new DotaUtilityProbe(
            memory,
            new FixedAddressResolver(addresses),
            offsets);

        var snapshot = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(VisibilityState.Visible, snapshot.State);
        Assert.True(snapshot.IsAttached);
        Assert.Equal(1337, snapshot.ProcessId);
        Assert.Equal(2, snapshot.LocalTeam);
        Assert.Equal(3, snapshot.EnemyTeam);
        Assert.Equal(0, snapshot.RawVisibilityMask);
        Assert.Equal(0x6000UL, snapshot.HeroAddress);
        Assert.Equal(5, snapshot.LocalPlayerId);
        Assert.Contains("enemy-team NPC visibility bitmap", snapshot.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("visible", snapshot.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProbeAsync_UsesEnemyTeamNpcVisibilityBitmapWhenLegacyMaskClaimsVisible()
    {
        var memory = new FakeProcessMemoryAccessor();
        var offsets = DotaOffsets.Current;
        var addresses = SeedScenario(
            memory,
            offsets,
            enemyCanSeeHero: false,
            legacyVisibilityMask: 1 << 3);
        var probe = new DotaUtilityProbe(
            memory,
            new FixedAddressResolver(addresses),
            offsets);

        var snapshot = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(VisibilityState.Hidden, snapshot.State);
        Assert.Equal(3, snapshot.EnemyTeam);
        Assert.Equal(1 << 3, snapshot.RawVisibilityMask);
        Assert.Contains("enemy-team NPC visibility bitmap", snapshot.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("hidden", snapshot.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProbeAsync_ReturnsUnknownWhenEnemyTeamDataIsUnavailable()
    {
        var memory = new FakeProcessMemoryAccessor();
        var offsets = DotaOffsets.Current;
        var addresses = SeedScenario(
            memory,
            offsets,
            enemyCanSeeHero: true,
            legacyVisibilityMask: 1 << 3);

        memory.Write(
            addresses.TeamDataHandleArrayAddress + (3UL * sizeof(uint)),
            uint.MaxValue);

        var probe = new DotaUtilityProbe(
            memory,
            new FixedAddressResolver(addresses),
            offsets);

        var snapshot = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(VisibilityState.Unknown, snapshot.State);
        Assert.Equal(1 << 3, snapshot.RawVisibilityMask);
        Assert.Contains("team 3 visibility data", snapshot.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true, 0x0C, VisibilityState.Visible)]
    [InlineData(false, 0x04, VisibilityState.Hidden)]
    public async Task ProbeAsync_UsesLocalServerHeroVisibilityMaskWhenClientEnemyTeamDataIsUnavailable(
        bool enemyCanSeeHero,
        int serverMask,
        VisibilityState expectedState)
    {
        var memory = new FakeProcessMemoryAccessor();
        var offsets = DotaOffsets.Current;
        var addresses = SeedScenario(
            memory,
            offsets,
            enemyCanSeeHero: enemyCanSeeHero,
            legacyVisibilityMask: 0);

        memory.Write(
            addresses.TeamDataHandleArrayAddress + (3UL * sizeof(uint)),
            uint.MaxValue);

        const ulong serverBase = 0x10_0000;
        const int serverSize = 0x2000;
        const ulong serverEntitySystemGlobal = serverBase + 0x500;
        const ulong serverEntitySystem = 0x20_0000;
        const ulong serverChunk = 0x21_0000;
        const ulong serverHero = 0x22_0000;
        const int heroIndex = 33;
        const uint serverHeroHandle = 0x0002_8021;

        var serverBytes = new byte[serverSize];
        // 48 89 0D [disp32] E9 00 00 00 00 CC
        const int patternOffset = 0x100;
        serverBytes[patternOffset + 0] = 0x48;
        serverBytes[patternOffset + 1] = 0x89;
        serverBytes[patternOffset + 2] = 0x0D;
        var disp = checked((int)((long)serverEntitySystemGlobal - (long)(serverBase + patternOffset + 7)));
        BinaryPrimitives.WriteInt32LittleEndian(serverBytes.AsSpan(patternOffset + 3, 4), disp);
        serverBytes[patternOffset + 7] = 0xE9;
        serverBytes[patternOffset + 12] = 0xCC;

        memory.WriteBytes(serverBase, serverBytes);
        memory.AddModule(new ProcessModuleSnapshot("server.dll", @"C:\dota\server.dll", serverBase, serverSize));
        memory.Write(serverEntitySystemGlobal, serverEntitySystem);
        memory.Write(serverEntitySystem + (ulong)offsets.EntityListFirstChunkOffset, serverChunk);

        var serverHeroIdentity = serverChunk + (ulong)(heroIndex * offsets.EntityListEntryStride);
        memory.Write(serverHeroIdentity, serverHero);
        memory.Write(serverHeroIdentity + (ulong)offsets.EntityIdentityHandle, serverHeroHandle);
        memory.Write(serverHero + (ulong)offsets.EntityInstanceIdentity, serverHeroIdentity);
        memory.Write(serverHero + (ulong)offsets.ServerBaseEntityTeamNumber, (byte)2);
        memory.Write(serverHero + (ulong)offsets.ServerBaseModelEntityTeamVisibilityBitmask, serverMask);

        var probe = new DotaUtilityProbe(
            memory,
            new FixedAddressResolver(addresses),
            offsets);

        var snapshot = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(expectedState, snapshot.State);
        Assert.Equal(serverMask, snapshot.RawVisibilityMask);
        Assert.Contains("Local-host server entity #33", snapshot.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false, VisibilityState.Hidden)]
    [InlineData(true, VisibilityState.Visible)]
    public async Task ProbeAsync_UsesClientOnlineVisibilityWhenEnemyTeamDataAndServerAreUnavailable(
        bool enemyUnitWithinVisionRange,
        VisibilityState expectedState)
    {
        var memory = new FakeProcessMemoryAccessor();
        var offsets = DotaOffsets.Current;
        var addresses = SeedScenario(
            memory,
            offsets,
            enemyCanSeeHero: false,
            legacyVisibilityMask: 0);

        // Enemy team data is unavailable in non-host online matches.
        memory.Write(
            addresses.TeamDataHandleArrayAddress + (3UL * sizeof(uint)),
            uint.MaxValue);

        const ulong chunk = 0x4000;
        const ulong hero = 0x6000;
        const ulong ownTeamData = 0x7800;
        const int ownTeamDataIndex = 56;
        const uint ownTeamDataHandle = 0x0001_8038;
        const ulong enemyHero = 0x8000;
        const ulong enemySceneNode = 0x8800;
        const int enemyHeroIndex = 70;
        const uint enemyHeroHandle = 0x0001_8046;

        // Populate local hero modifier manager header (owner == hero) and state fields.
        var manager = hero + (ulong)offsets.BaseNpcModifierManager;
        memory.Write(manager + 0x08, hero);
        memory.Write(manager + 0x10, 0);
        memory.Write(manager + 0x18, 0UL);
        memory.Write(manager + 0x20, 0);
        memory.Write(manager + 0x37A, ushort.MaxValue);
        memory.Write(manager + 0x37C, ushort.MaxValue);
        memory.Write(manager + 0x380, (ushort)0x0008);
        memory.Write(manager + 0x382, (ushort)0x0008);
        memory.Write(manager + 0x384, (ushort)0);
        memory.Write(manager + 0x386, (ushort)0);
        memory.Write(hero + (ulong)offsets.BaseEntityLifeState, (byte)0);
        memory.Write(hero + (ulong)offsets.BaseNpcInvisibilityLevel, 0f);
        memory.Write(hero + (ulong)offsets.BaseNpcUnitState64, 0UL);

        // Populate own-team C_DOTA_DataNonSpectator (team 2) and its 256-word NPC visible bitmap.
        var ownTeamIdentity = chunk + (ulong)(ownTeamDataIndex * offsets.EntityListEntryStride);
        memory.Write(ownTeamIdentity, ownTeamData);
        memory.Write(ownTeamIdentity + (ulong)offsets.EntityIdentityHandle, ownTeamDataHandle);
        memory.Write(ownTeamData + (ulong)offsets.EntityInstanceIdentity, ownTeamIdentity);
        memory.Write(ownTeamData + (ulong)offsets.BaseEntityTeamNumber, (byte)2);
        memory.Write(addresses.TeamDataHandleArrayAddress + (2UL * sizeof(uint)), ownTeamDataHandle);
        memory.WriteBytes(ownTeamData + (ulong)offsets.DataNonSpectatorNpcVisibleState, new byte[256 * sizeof(ulong)]);

        if (enemyUnitWithinVisionRange)
        {
            var enemyIdentity = chunk + (ulong)(enemyHeroIndex * offsets.EntityListEntryStride);
            memory.Write(enemyIdentity, enemyHero);
            memory.Write(enemyIdentity + (ulong)offsets.EntityIdentityHandle, enemyHeroHandle);
            memory.Write(enemyHero + (ulong)offsets.EntityInstanceIdentity, enemyIdentity);
            memory.Write(enemyHero + (ulong)offsets.BaseEntityTeamNumber, (byte)3);
            memory.Write(enemyHero + (ulong)offsets.BaseEntityLifeState, (byte)0);
            memory.Write(enemyHero + (ulong)offsets.BaseEntityGameSceneNode, enemySceneNode);
            memory.Write(enemySceneNode + (ulong)offsets.GameSceneNodeAbsOrigin, new Vector3(1200f, 0f, 0f));
            memory.Write(enemyHero + (ulong)offsets.BaseNpcDayTimeVisionRange, 1800);
            memory.Write(enemyHero + (ulong)offsets.BaseNpcNightTimeVisionRange, 800);

            var wordIndex = enemyHeroIndex / 64;
            var bitMask = 1UL << (enemyHeroIndex % 64);
            memory.Write(
                ownTeamData + (ulong)offsets.DataNonSpectatorNpcVisibleState + (ulong)(wordIndex * sizeof(ulong)),
                bitMask);
        }

        var probe = new DotaUtilityProbe(
            memory,
            new FixedAddressResolver(addresses),
            offsets);

        var snapshot = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(expectedState, snapshot.State);
        Assert.Contains("Client online visibility", snapshot.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProbeAsync_ReturnsDisconnectedUnknownWhenDotaIsNotRunning()
    {
        var memory = new FakeProcessMemoryAccessor { AttachSucceeds = false };
        var probe = new DotaUtilityProbe(
            memory,
            new FixedAddressResolver(
                new DotaResolvedAddresses(
                    0x1000,
                    0x1100,
                    0x1200,
                    0x1300)),
            DotaOffsets.Current);

        var snapshot = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(VisibilityState.Unknown, snapshot.State);
        Assert.False(snapshot.IsAttached);
        Assert.Contains("dota2", snapshot.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProbeAsync_ReturnsAttachedUnknownWhenNoAssignedHeroCanBeResolved()
    {
        var memory = new FakeProcessMemoryAccessor();
        memory.TryAttach(1337);
        const ulong entitySystemGlobal = 0x1000;
        const ulong networkClientGlobal = 0x1100;
        const ulong teamDataHandleArray = 0x1200;
        memory.Write(entitySystemGlobal, 0x2000UL);
        memory.Write(networkClientGlobal, 0x3000UL);
        memory.Write(
            0x3000UL + (ulong)DotaOffsets.Current.NetworkGameClientLocalPlayer,
            5);

        var probe = new DotaUtilityProbe(
            memory,
            new FixedAddressResolver(
                new DotaResolvedAddresses(
                    entitySystemGlobal,
                    networkClientGlobal,
                    teamDataHandleArray,
                    0x1300)),
            DotaOffsets.Current);

        var snapshot = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(VisibilityState.Unknown, snapshot.State);
        Assert.True(snapshot.IsAttached);
        Assert.Contains("controller", snapshot.Detail, StringComparison.OrdinalIgnoreCase);
    }

    private static DotaResolvedAddresses SeedScenario(
        FakeProcessMemoryAccessor memory,
        DotaOffsets offsets,
        bool enemyCanSeeHero,
        int legacyVisibilityMask)
    {
        const ulong entitySystemGlobal = 0x1000;
        const ulong networkClientGlobal = 0x1100;
        const ulong teamDataHandleArray = 0x1200;
        const ulong entitySystem = 0x2000;
        const ulong networkClient = 0x3000;
        const ulong chunk = 0x4000;
        const ulong controller = 0x5000;
        const ulong hero = 0x6000;
        const ulong sceneNode = 0x6800;
        const ulong enemyTeamData = 0x7000;
        const int controllerIndex = 12;
        const int heroIndex = 33;
        const int enemyTeamDataIndex = 55;
        const uint heroHandle = 0x0001_8021;
        const uint enemyTeamDataHandle = 0x0001_8037;

        memory.TryAttach(1337);
        memory.Write(entitySystemGlobal, entitySystem);
        memory.Write(networkClientGlobal, networkClient);
        memory.Write(
            networkClient + (ulong)offsets.NetworkGameClientLocalPlayer,
            controllerIndex - 1);

        memory.Write(
            entitySystem + (ulong)offsets.EntityListFirstChunkOffset,
            chunk);
        memory.Write(
            chunk + (ulong)(controllerIndex * offsets.EntityListEntryStride),
            controller);
        memory.Write(0x1300UL, controller);
        var controllerIdentity = chunk + (ulong)(controllerIndex * offsets.EntityListEntryStride);
        memory.Write(controllerIdentity + (ulong)offsets.EntityIdentityHandle, 0x1800Cu);
        memory.Write(controller + (ulong)offsets.EntityInstanceIdentity, controllerIdentity);
        memory.Write(controller + (ulong)offsets.PlayerControllerIsLocalPlayer, (byte)1);
        memory.Write(controller + (ulong)offsets.PlayerControllerIsHltv, (byte)0);
        memory.Write(controller + (ulong)offsets.BaseEntityTeamNumber, (byte)2);
        memory.Write(
            controller + (ulong)offsets.PlayerControllerPlayerId,
            5);
        memory.Write(
            controller + (ulong)offsets.PlayerControllerAssignedHero,
            heroHandle);
        memory.Write(
            chunk + (ulong)(heroIndex * offsets.EntityListEntryStride),
            hero);
        memory.Write(chunk + (ulong)(heroIndex * offsets.EntityListEntryStride) + (ulong)offsets.EntityIdentityHandle, heroHandle);
        memory.Write(
            chunk + (ulong)(enemyTeamDataIndex * offsets.EntityListEntryStride),
            enemyTeamData);
        memory.Write(chunk + (ulong)(enemyTeamDataIndex * offsets.EntityListEntryStride) + (ulong)offsets.EntityIdentityHandle, enemyTeamDataHandle);

        memory.Write(
            hero + (ulong)offsets.BaseEntityTeamNumber,
            (byte)2);
        memory.Write(
            hero + (ulong)offsets.BaseEntityGameSceneNode,
            sceneNode);
        memory.Write(
            sceneNode + (ulong)offsets.GameSceneNodeAbsOrigin,
            Vector3.Zero);
        memory.Write(
            hero + (ulong)offsets.BaseModelEntityTeamVisibilityBitmask,
            legacyVisibilityMask);

        memory.Write(
            teamDataHandleArray + (3UL * sizeof(uint)),
            enemyTeamDataHandle);
        memory.Write(
            enemyTeamData + (ulong)offsets.BaseEntityTeamNumber,
            (byte)3);

        var heroWordIndex = heroIndex / 64;
        var heroBit = 1UL << (heroIndex % 64);
        memory.Write(
            enemyTeamData
            + (ulong)offsets.DataNonSpectatorNpcVisibleState
            + (ulong)(heroWordIndex * sizeof(ulong)),
            enemyCanSeeHero ? heroBit : 0UL);

        return new DotaResolvedAddresses(
            entitySystemGlobal,
            networkClientGlobal,
            teamDataHandleArray,
            0x1300);
    }

    private sealed class FixedAddressResolver(
        DotaResolvedAddresses addresses) : IDotaAddressResolver
    {
        public bool TryResolve(
            out DotaResolvedAddresses resolvedAddresses,
            out string detail)
        {
            resolvedAddresses = addresses;
            detail = "Resolved test addresses.";
            return true;
        }
    }
}
