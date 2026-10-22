namespace DotaUtility.Dota2.Tests;

public sealed class ClientTeamVisibilityReaderTests
{
    [Fact]
    public void TryReadEnemyVisibility_ReturnsVisibleFromEnemyTeamBitmap()
    {
        var scenario = CreateScenario(heroEntityIndex: 33, visibilityWord: 1UL << 33);

        var success = scenario.Reader.TryReadEnemyVisibility(
            scenario.TeamDataHandleArray,
            scenario.EntitySystem,
            enemyTeam: 3,
            heroEntityIndex: 33,
            out var sample,
            out var detail);

        Assert.True(success);
        Assert.True(sample.IsVisible);
        Assert.Equal(33, sample.HeroEntityIndex);
        Assert.Equal(1UL << 33, sample.VisibilityWord);
        Assert.Contains("visible", detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryReadEnemyVisibility_ReturnsHiddenWhenHeroBitIsClear()
    {
        var scenario = CreateScenario(heroEntityIndex: 33, visibilityWord: 1UL << 7);

        var success = scenario.Reader.TryReadEnemyVisibility(
            scenario.TeamDataHandleArray,
            scenario.EntitySystem,
            enemyTeam: 3,
            heroEntityIndex: 33,
            out var sample,
            out var detail);

        Assert.True(success);
        Assert.False(sample.IsVisible);
        Assert.Contains("hidden", detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryReadEnemyVisibility_RejectsInvalidTeamDataHandle()
    {
        var scenario = CreateScenario(heroEntityIndex: 33, visibilityWord: 0);
        scenario.Memory.Write(
            scenario.TeamDataHandleArray + (3UL * sizeof(uint)),
            0xFFFF_FFFEu);

        var success = scenario.Reader.TryReadEnemyVisibility(
            scenario.TeamDataHandleArray,
            scenario.EntitySystem,
            enemyTeam: 3,
            heroEntityIndex: 33,
            out _,
            out var detail);

        Assert.False(success);
        Assert.Contains("not available", detail, StringComparison.OrdinalIgnoreCase);
    }

    private static Scenario CreateScenario(
        int heroEntityIndex,
        ulong visibilityWord)
    {
        var offsets = DotaOffsets.Current;
        var memory = new FakeProcessMemoryAccessor();
        memory.TryAttach(1337);

        const ulong entitySystem = 0x1000;
        const ulong chunk = 0x2000;
        const ulong teamDataHandleArray = 0x3000;
        const ulong teamData = 0x4000;
        const int teamDataEntityIndex = 55;
        const uint teamDataHandle = teamDataEntityIndex;

        memory.Write(
            entitySystem + (ulong)offsets.EntityListFirstChunkOffset,
            chunk);
        memory.Write(
            chunk + (ulong)(teamDataEntityIndex * offsets.EntityListEntryStride),
            teamData);
        memory.Write(chunk + (ulong)(teamDataEntityIndex * offsets.EntityListEntryStride) + (ulong)offsets.EntityIdentityHandle, teamDataHandle);
        memory.Write(
            teamDataHandleArray + (3UL * sizeof(uint)),
            teamDataHandle);
        memory.Write(
            teamData + (ulong)offsets.BaseEntityTeamNumber,
            (byte)3);

        var wordIndex = heroEntityIndex / 64;
        memory.Write(
            teamData
            + (ulong)offsets.DataNonSpectatorNpcVisibleState
            + (ulong)(wordIndex * sizeof(ulong)),
            visibilityWord);

        return new Scenario(
            memory,
            new ClientTeamVisibilityReader(memory, offsets),
            entitySystem,
            teamDataHandleArray);
    }

    private sealed record Scenario(
        FakeProcessMemoryAccessor Memory,
        ClientTeamVisibilityReader Reader,
        ulong EntitySystem,
        ulong TeamDataHandleArray);
}
