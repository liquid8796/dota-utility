using DotaUtility.Memory;

namespace DotaUtility.Dota2;

public readonly record struct LocalPlayerContext(
    ulong ControllerAddress,
    uint ControllerHandle,
    int ControllerEntityIndex,
    int DotaPlayerId,
    ulong HeroAddress,
    uint HeroHandle,
    int HeroEntityIndex,
    int LocalTeam);

/// <summary>
/// Reads the primary controller selected by the engine, then its Dota PlayerID and
/// assigned hero. Network CPlayerSlot and Dota PlayerID are separate namespaces.
/// No fallback scans other players for an ID numerically equal to a network slot.
/// </summary>
public sealed class LocalPlayerContextReader
{
    private readonly IProcessMemoryAccessor _memory;
    private readonly DotaOffsets _offsets;
    private readonly Source2EntityResolver _entities;

    public LocalPlayerContextReader(IProcessMemoryAccessor memory, DotaOffsets offsets)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _offsets = offsets ?? throw new ArgumentNullException(nameof(offsets));
        _entities = new Source2EntityResolver(memory, offsets);
    }

    public bool TryRead(
        ulong entitySystem,
        ulong localControllerArray,
        out LocalPlayerContext context,
        out string detail)
    {
        context = default;
        if (!IsUserPointer(entitySystem)
            || !IsUserPointer(localControllerArray)
            || !_memory.TryRead<ulong>(localControllerArray, out var controller)
            || !IsUserPointer(controller))
        {
            detail = "The native local controller is unavailable; Dota may be at the dashboard or loading.";
            return false;
        }

        // Dota's native primary local-controller getter reads array[0]. The table
        // index is a split-screen index, not a CPlayerSlot and not a Dota PlayerID.
        if (!_memory.TryRead<byte>(controller + (ulong)_offsets.PlayerControllerIsLocalPlayer, out var localFlag)
            || localFlag != 1
            || !_memory.TryRead<byte>(controller + (ulong)_offsets.PlayerControllerIsHltv, out var hltvFlag)
            || hltvFlag != 0)
        {
            detail = "The native controller is not a confirmed local, non-HLTV controller.";
            return false;
        }

        if (!_memory.TryRead<ulong>(controller + (ulong)_offsets.EntityInstanceIdentity, out var identity)
            || !IsUserPointer(identity)
            || !_memory.TryRead<uint>(identity + (ulong)_offsets.EntityIdentityHandle, out var controllerHandle)
            || _entities.ResolveByHandle(entitySystem, controllerHandle) != controller)
        {
            detail = "The local controller identity or handle generation is not valid.";
            return false;
        }

        if (!_memory.TryRead<int>(controller + (ulong)_offsets.PlayerControllerPlayerId, out var playerId)
            || playerId is < 0 or > 63
            || !_memory.TryRead<byte>(controller + (ulong)_offsets.BaseEntityTeamNumber, out var controllerTeam)
            || controllerTeam is not (2 or 3))
        {
            detail = "The local controller has no valid playing-team Dota PlayerID yet.";
            return false;
        }

        if (!_memory.TryRead<uint>(controller + (ulong)_offsets.PlayerControllerAssignedHero, out var heroHandle))
        {
            detail = "The local controller assigned-hero handle could not be read.";
            return false;
        }

        var hero = _entities.ResolveByHandle(entitySystem, heroHandle);
        if (!IsUserPointer(hero)
            || !_memory.TryRead<byte>(hero + (ulong)_offsets.BaseEntityTeamNumber, out var heroTeam)
            || heroTeam != controllerTeam)
        {
            detail = "The local controller has no valid assigned hero on its playing team yet.";
            return false;
        }

        if (!_memory.TryRead<ulong>(localControllerArray, out var confirmedController)
            || confirmedController != controller
            || !_memory.TryRead<ulong>(controller + (ulong)_offsets.EntityInstanceIdentity, out var confirmedIdentity)
            || confirmedIdentity != identity
            || _entities.ResolveByHandle(entitySystem, controllerHandle) != controller)
        {
            detail = "The native local controller identity changed during acquisition.";
            return false;
        }

        context = new LocalPlayerContext(
            controller,
            controllerHandle,
            (int)(controllerHandle & (uint)_offsets.EntityHandleIndexMask),
            playerId,
            hero,
            heroHandle,
            (int)(heroHandle & (uint)_offsets.EntityHandleIndexMask),
            heroTeam);
        detail = $"Native local controller entity #{context.ControllerEntityIndex}, Dota PlayerID {playerId}.";
        return true;
    }

    private static bool IsUserPointer(ulong pointer) =>
        pointer != 0 && pointer <= 0x0000_7FFF_FFFF_FFFF;
}
