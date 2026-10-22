using DotaUtility.Domain;

namespace DotaUtility.Dota2;

public sealed record EnemyVisibilityEvaluation(
    VisibilityState State,
    int? EnemyTeam,
    string Detail);

public static class EnemyVisibilityInterpreter
{
    public static EnemyVisibilityEvaluation Evaluate(int localTeam, int teamVisibilityMask)
    {
        int? enemyTeam = localTeam switch
        {
            2 => 3,
            3 => 2,
            _ => null,
        };

        if (enemyTeam is null)
        {
            return new EnemyVisibilityEvaluation(
                VisibilityState.Unknown,
                null,
                $"Local hero team {localTeam} is not Radiant (2) or Dire (3).");
        }

        var visible = (teamVisibilityMask & (1 << enemyTeam.Value)) != 0;
        return new EnemyVisibilityEvaluation(
            visible ? VisibilityState.Visible : VisibilityState.Hidden,
            enemyTeam,
            visible
                ? $"Enemy team {enemyTeam.Value} currently has vision of the local hero."
                : $"Enemy team {enemyTeam.Value} does not currently have vision of the local hero.");
    }
}
