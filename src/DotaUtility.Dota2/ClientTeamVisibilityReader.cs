using DotaUtility.Memory;

namespace DotaUtility.Dota2;

public readonly record struct ClientTeamVisibilitySample(
    bool IsVisible,
    ulong VisibilityWord,
    int HeroEntityIndex,
    ulong TeamDataAddress,
    uint TeamDataHandle);

/// <summary>
/// Reads a per-team NPC visibility bitmap only when its team-data handle resolves.
/// One session exposing an opposing-team object does not establish availability
/// for every non-host client. Missing data is unknown, never a hidden sample.
/// </summary>
public sealed class ClientTeamVisibilityReader
{
    private const int MaximumTeamIndex = 14;
    private const int VisibilityWordCount = 256;
    private const int BitsPerWord = sizeof(ulong) * 8;

    private readonly IProcessMemoryAccessor _memory;
    private readonly DotaOffsets _offsets;
    private readonly Source2EntityResolver _entities;

    public ClientTeamVisibilityReader(
        IProcessMemoryAccessor memory,
        DotaOffsets offsets)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _offsets = offsets ?? throw new ArgumentNullException(nameof(offsets));
        _entities = new Source2EntityResolver(memory, offsets);
    }

    public bool TryReadEnemyVisibility(
        ulong teamDataHandleArray,
        ulong entitySystem,
        int enemyTeam,
        int heroEntityIndex,
        out ClientTeamVisibilitySample sample,
        out string detail)
    {
        sample = default;
        detail = string.Empty;

        if (teamDataHandleArray == 0 || entitySystem == 0)
        {
            detail = "Team-data visibility addresses are unavailable.";
            return false;
        }

        if (enemyTeam is < 0 or > MaximumTeamIndex)
        {
            detail = $"Enemy team {enemyTeam} is outside the supported Dota team range.";
            return false;
        }

        if (heroEntityIndex is < 0 or >= VisibilityWordCount * BitsPerWord)
        {
            detail = $"Local hero entity index {heroEntityIndex} is outside the team visibility bitmap.";
            return false;
        }

        var handleAddress = teamDataHandleArray + (ulong)(enemyTeam * sizeof(uint));
        if (!_memory.TryRead<uint>(handleAddress, out var teamDataHandle)
            || teamDataHandle is 0 or uint.MaxValue or 0xFFFF_FFFEu)
        {
            detail = $"Enemy team {enemyTeam} visibility data is not available in this client context. Missing data does not mean Hidden.";
            return false;
        }

        var teamDataAddress = _entities.ResolveByHandle(entitySystem, teamDataHandle);
        if (teamDataAddress == 0)
        {
            detail = $"Enemy team {enemyTeam} visibility entity could not be resolved.";
            return false;
        }

        if (!_memory.TryRead<byte>(
                teamDataAddress + (ulong)_offsets.BaseEntityTeamNumber,
                out var teamNumber)
            || teamNumber != enemyTeam)
        {
            detail = $"Resolved team-data entity does not belong to enemy team {enemyTeam}.";
            return false;
        }

        var wordIndex = heroEntityIndex / BitsPerWord;
        var bitIndex = heroEntityIndex % BitsPerWord;
        var wordAddress = teamDataAddress
            + (ulong)_offsets.DataNonSpectatorNpcVisibleState
            + (ulong)(wordIndex * sizeof(ulong));

        if (!_memory.TryRead<ulong>(wordAddress, out var visibilityWord))
        {
            detail = "Could not read the enemy team NPC visibility bitmap.";
            return false;
        }

        // Best-effort consistency check; these reads are not an atomic game frame.
        if (!_memory.TryRead<uint>(handleAddress, out var confirmedHandle)
            || confirmedHandle != teamDataHandle
            || _entities.ResolveByHandle(entitySystem, confirmedHandle) != teamDataAddress
            || !_memory.TryRead<byte>(teamDataAddress + (ulong)_offsets.BaseEntityTeamNumber, out var confirmedTeam)
            || confirmedTeam != enemyTeam)
        {
            detail = "Enemy team visibility identity changed during the sample; waiting for a fresh context.";
            return false;
        }

        var isVisible = (visibilityWord & (1UL << bitIndex)) != 0;
        sample = new ClientTeamVisibilitySample(
            isVisible,
            visibilityWord,
            heroEntityIndex,
            teamDataAddress,
            teamDataHandle);

        detail = isVisible
            ? $"Enemy-team NPC visibility bitmap reports the local hero visible at entity #{heroEntityIndex}."
            : $"Enemy-team NPC visibility bitmap reports the local hero hidden at entity #{heroEntityIndex}.";
        return true;
    }
}
