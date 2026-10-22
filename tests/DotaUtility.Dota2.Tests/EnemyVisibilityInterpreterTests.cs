using DotaUtility.Domain;

namespace DotaUtility.Dota2.Tests;

public sealed class EnemyVisibilityInterpreterTests
{
    [Theory]
    [InlineData(2, 1 << 3, VisibilityState.Visible, 3)]
    [InlineData(2, 1 << 2, VisibilityState.Hidden, 3)]
    [InlineData(3, 1 << 2, VisibilityState.Visible, 2)]
    [InlineData(3, 1 << 3, VisibilityState.Hidden, 2)]
    public void Evaluate_UsesOpposingDotaTeamBit(int localTeam, int mask, VisibilityState expectedState, int expectedEnemyTeam)
    {
        var result = EnemyVisibilityInterpreter.Evaluate(localTeam, mask);

        Assert.Equal(expectedState, result.State);
        Assert.Equal(expectedEnemyTeam, result.EnemyTeam);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(14)]
    public void Evaluate_ReturnsUnknownForNonRadiantOrDireTeam(int localTeam)
    {
        var result = EnemyVisibilityInterpreter.Evaluate(localTeam, 0);

        Assert.Equal(VisibilityState.Unknown, result.State);
        Assert.Null(result.EnemyTeam);
    }
}
