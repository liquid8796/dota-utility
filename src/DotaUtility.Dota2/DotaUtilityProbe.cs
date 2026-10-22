using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using DotaUtility.Application;
using DotaUtility.Domain;
using DotaUtility.Memory;

namespace DotaUtility.Dota2;

public sealed class DotaUtilityProbe : IVisibilityProbe
{
    private const string DotaProcessName = "dota2";
    private const string ServerModuleName = "server.dll";

    private static readonly BytePattern ServerEntitySystemPattern =
        BytePattern.Parse("48 89 0D ?? ?? ?? ?? E9 ?? ?? ?? ?? CC");

    private static readonly BytePattern ServerEntitySystemFallbackPattern =
        BytePattern.Parse("48 8B 1D ?? ?? ?? ?? 48 89 1D ?? ?? ?? ?? 4C 63 B3");

    private static readonly BytePattern ClientFogOfWarSystemPattern =
        BytePattern.Parse("41 8B D4 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 8B D8 48 85 C0 0F 84");

    private readonly IProcessMemoryAccessor _memory;
    private readonly IDotaAddressResolver _addressResolver;
    private readonly DotaOffsets _offsets;
    private readonly LocalPlayerContextReader _localPlayer;
    private readonly ClientTeamVisibilityReader _teamVisibility;
    private readonly Source2EntityResolver _entities;
    private readonly IDotaBuildCompatibilityGate? _buildCompatibility;
    private readonly object _serverGate = new();
    private readonly Dictionary<int, EnemyHeroMinimapMarker> _lastKnownEnemyHeroes = new();
    private ulong _lastKnownHeroContextEntitySystem;
    private ServerCacheKey? _serverCacheKey;
    private ulong _cachedServerEntitySystemGlobal;
    private ClientFogCacheKey? _clientFogCacheKey;
    private ulong _cachedClientFogSystemAddress;

    public DotaUtilityProbe(
        IProcessMemoryAccessor memory,
        IDotaAddressResolver addressResolver,
        DotaOffsets offsets,
        IDotaBuildCompatibilityGate? buildCompatibility = null)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _addressResolver = addressResolver ?? throw new ArgumentNullException(nameof(addressResolver));
        _offsets = offsets ?? throw new ArgumentNullException(nameof(offsets));
        _buildCompatibility = buildCompatibility;
        _localPlayer = new LocalPlayerContextReader(memory, offsets);
        _teamVisibility = new ClientTeamVisibilityReader(memory, offsets);
        _entities = new Source2EntityResolver(memory, offsets);
    }

    public Task<VisibilitySnapshot> ProbeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_memory.IsAttached && !_memory.TryAttach(DotaProcessName))
        {
            return Task.FromResult(VisibilitySnapshot.Unknown(
                "dota2.exe is not running or cannot be opened with read/query access."));
        }

        var processId = _memory.ProcessId;
        var attachmentGeneration = (_memory as IProcessAttachmentGeneration)?.AttachmentGeneration;
        if (AttachmentChanged(processId, attachmentGeneration))
        {
            return Task.FromResult(AttachmentChangedUnknown());
        }

        if (_buildCompatibility is not null
            && !_buildCompatibility.TryValidate(out var compatibilityDetail))
        {
            return Task.FromResult(AttachedUnknown(processId, compatibilityDetail));
        }

        if (AttachmentChanged(processId, attachmentGeneration))
        {
            return Task.FromResult(AttachmentChangedUnknown());
        }

        if (!_addressResolver.TryResolve(out var addresses, out var resolverDetail))
        {
            return Task.FromResult(AttachedUnknown(processId, resolverDetail));
        }

        if (AttachmentChanged(processId, attachmentGeneration))
        {
            return Task.FromResult(AttachmentChangedUnknown());
        }

        if (!TryReadPointer(addresses.EntitySystemGlobalAddress, out var entitySystem))
        {
            return Task.FromResult(AttachedUnknown(processId,
                "Entity-system global resolved, but its pointer could not be read. Dota may be loading or the signature may need an update."));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!_localPlayer.TryRead(entitySystem, addresses.LocalControllerArrayAddress, out var local, out var localDetail))
        {
            return Task.FromResult(AttachedUnknown(processId, localDetail));
        }

        var enemyTeam = local.LocalTeam == 2 ? 3 : 2;
        int? legacyMask = null;
        if (_memory.TryRead<int>(local.HeroAddress + (ulong)_offsets.BaseModelEntityTeamVisibilityBitmask, out var rawMask))
        {
            legacyMask = rawMask;
        }

        var hasVisibility = _teamVisibility.TryReadEnemyVisibility(
            addresses.TeamDataHandleArrayAddress,
            entitySystem,
            enemyTeam,
            local.HeroEntityIndex,
            out var sample,
            out var visibilityDetail);
        var isVisible = sample.IsVisible;

        if (!hasVisibility
            && TryReadLocalServerHeroVisibility(
                processId,
                attachmentGeneration,
                local,
                enemyTeam,
                out var serverVisible,
                out var serverMask,
                out var serverDetail))
        {
            hasVisibility = true;
            isVisible = serverVisible;
            legacyMask = serverMask;
            visibilityDetail = serverDetail;
        }

        if (!hasVisibility
            && TryReadClientOnlineHeroVisibility(
                processId,
                attachmentGeneration,
                addresses.TeamDataHandleArrayAddress,
                entitySystem,
                local,
                enemyTeam,
                out var clientOnlineVisible,
                out var clientOnlineDetail))
        {
            hasVisibility = true;
            isVisible = clientOnlineVisible;
            visibilityDetail = clientOnlineDetail;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!TryReadPointer(addresses.EntitySystemGlobalAddress, out var currentEntitySystem)
            || currentEntitySystem != entitySystem
            || !_localPlayer.TryRead(currentEntitySystem, addresses.LocalControllerArrayAddress, out var currentLocal, out _)
            || currentLocal != local)
        {
            return Task.FromResult(AttachedUnknown(processId,
                "Match or local controller/hero identity changed during the visibility sample; waiting for a fresh context."));
        }

        // A process can detach/re-attach with identical PID, module layout and
        // controller data. Identity rereads alone cannot detect that transition.
        if (AttachmentChanged(processId, attachmentGeneration))
        {
            return Task.FromResult(AttachmentChangedUnknown());
        }

        var isInEnemySentry = IsLocalHeroInEnemySentryRange(
            addresses.TeamDataHandleArrayAddress,
            entitySystem,
            local,
            enemyTeam);
        var enemyHeroes = ReadEnemyHeroMinimapMarkers(
            processId,
            attachmentGeneration,
            entitySystem,
            local,
            enemyTeam);

        // Unavailable opposing-team data is not replaced with own-team data,
        // legacy hero masks, terrain FoW, or any other unvalidated heuristic.
        var state = hasVisibility
            ? isVisible ? VisibilityState.Visible : VisibilityState.Hidden
            : VisibilityState.Unknown;
        return Task.FromResult(new VisibilitySnapshot(
            state,
            DateTimeOffset.UtcNow,
            IsAttached: true,
            ProcessId: processId,
            LocalTeam: local.LocalTeam,
            EnemyTeam: enemyTeam,
            RawVisibilityMask: legacyMask,
            Detail: $"{visibilityDetail} Local Dota PlayerID {local.DotaPlayerId}; controller entity #{local.ControllerEntityIndex}.",
            HeroAddress: local.HeroAddress,
            LocalPlayerId: local.DotaPlayerId,
            IsInEnemySentry: isInEnemySentry,
            EnemyHeroes: enemyHeroes));
    }

    private IReadOnlyList<EnemyHeroMinimapMarker> ReadEnemyHeroMinimapMarkers(
        int? processId,
        long? attachmentGeneration,
        ulong entitySystem,
        LocalPlayerContext local,
        int enemyTeam)
    {
        if (entitySystem != _lastKnownHeroContextEntitySystem)
        {
            _lastKnownEnemyHeroes.Clear();
            _lastKnownHeroContextEntitySystem = entitySystem;
        }

        var liveMarkers = new Dictionary<int, EnemyHeroMinimapMarker>();

        // 1. If local server.dll is active (Local Lobby / Hero Demo / Arcade Host), read authoritative server positions for all enemy heroes.
        if (processId is int pid
            && TryResolveServerEntitySystemGlobal(pid, attachmentGeneration, out var serverEntitySystemGlobal)
            && TryReadPointer(serverEntitySystemGlobal, out var serverEntitySystem)
            && IsUserPointer(serverEntitySystem))
        {
            for (var index = 1; index < 1024; index++)
            {
                if (index == local.HeroEntityIndex)
                {
                    continue;
                }

                var serverEntity = _entities.ResolveByIndex(serverEntitySystem, index);
                if (!IsUserPointer(serverEntity)
                    || !_memory.TryRead<byte>(serverEntity + (ulong)_offsets.ServerBaseEntityTeamNumber, out var serverTeam)
                    || serverTeam != enemyTeam
                    || !_memory.TryRead<byte>(serverEntity + 0x2D8, out var serverLifeState)
                    || serverLifeState != 0
                    || !TryReadHeroShortName(serverEntity, out var heroName))
                {
                    continue;
                }

                if (TryReadPointer(serverEntity + 0x30, out var bodyComponent)
                    && TryReadPointer(bodyComponent + 0x08, out var serverSceneNode)
                    && _memory.TryRead<Vector3>(serverSceneNode + 0xC8, out var serverOrigin)
                    && IsValidWorldOrigin(serverOrigin))
                {
                    var marker = new EnemyHeroMinimapMarker(index, heroName, serverOrigin.X, serverOrigin.Y, IsLive: true);
                    liveMarkers[index] = marker;
                    _lastKnownEnemyHeroes[index] = marker;
                }
            }
        }

        // 2. Also read enemy heroes from client.dll (via player controllers and active hero entities).
        var maxControllers = Math.Clamp(_offsets.MaxPlayerControllerEntityIndex, 1, 64);
        for (var ctrlIndex = 1; ctrlIndex <= maxControllers; ctrlIndex++)
        {
            if (ctrlIndex == local.ControllerEntityIndex)
            {
                continue;
            }

            var controller = _entities.ResolveByIndex(entitySystem, ctrlIndex);
            if (!IsUserPointer(controller)
                || !_memory.TryRead<byte>(controller + (ulong)_offsets.BaseEntityTeamNumber, out var ctrlTeam)
                || ctrlTeam != enemyTeam
                || !_memory.TryRead<uint>(controller + (ulong)_offsets.PlayerControllerAssignedHero, out var assignedHandle)
                || assignedHandle is 0 or uint.MaxValue or 0xFFFF_FFFEu)
            {
                continue;
            }

            var heroIndex = (int)(assignedHandle & (uint)_offsets.EntityHandleIndexMask);
            if (heroIndex <= 0 || heroIndex == local.HeroEntityIndex || liveMarkers.ContainsKey(heroIndex))
            {
                continue;
            }

            var heroEntity = _entities.ResolveByHandle(entitySystem, assignedHandle);
            if (!IsUserPointer(heroEntity)
                || !_memory.TryRead<byte>(heroEntity + (ulong)_offsets.BaseEntityLifeState, out var lifeState)
                || lifeState != 0
                || !TryReadPointer(heroEntity + (ulong)_offsets.BaseEntityGameSceneNode, out var sceneNode)
                || !_memory.TryRead<Vector3>(sceneNode + (ulong)_offsets.GameSceneNodeAbsOrigin, out var origin)
                || !IsValidWorldOrigin(origin))
            {
                continue;
            }

            var heroName = TryReadHeroShortName(heroEntity, out var parsedName)
                ? parsedName
                : $"H{ctrlIndex}";
            var marker = new EnemyHeroMinimapMarker(heroIndex, heroName, origin.X, origin.Y, IsLive: true);
            liveMarkers[heroIndex] = marker;
            _lastKnownEnemyHeroes[heroIndex] = marker;
        }

        for (var index = 1; index < 768; index++)
        {
            if (index == local.HeroEntityIndex || liveMarkers.ContainsKey(index))
            {
                continue;
            }

            var clientEntity = _entities.ResolveByIndex(entitySystem, index);
            if (!IsUserPointer(clientEntity)
                || !_memory.TryRead<byte>(clientEntity + (ulong)_offsets.BaseEntityTeamNumber, out var entityTeam)
                || entityTeam != enemyTeam
                || !_memory.TryRead<byte>(clientEntity + (ulong)_offsets.BaseEntityLifeState, out var lifeState)
                || lifeState != 0
                || !TryReadHeroShortName(clientEntity, out var heroName)
                || !TryReadPointer(clientEntity + (ulong)_offsets.BaseEntityGameSceneNode, out var sceneNode)
                || !_memory.TryRead<Vector3>(sceneNode + (ulong)_offsets.GameSceneNodeAbsOrigin, out var origin)
                || !IsValidWorldOrigin(origin))
            {
                continue;
            }

            var marker = new EnemyHeroMinimapMarker(index, heroName, origin.X, origin.Y, IsLive: true);
            liveMarkers[index] = marker;
            _lastKnownEnemyHeroes[index] = marker;
        }

        // Merge any cached enemy heroes that are currently in Fog of War so their minimap icon stays visible at last known coords.
        foreach (var (index, cached) in _lastKnownEnemyHeroes)
        {
            if (!liveMarkers.ContainsKey(index))
            {
                liveMarkers[index] = cached with { IsLive = false };
            }
        }

        return liveMarkers.Values
            .OrderBy(static m => m.EntityIndex)
            .ToArray();
    }

    private bool TryReadHeroShortName(ulong entityAddress, out string shortName)
    {
        shortName = string.Empty;
        if (!TryReadPointer(entityAddress + (ulong)_offsets.EntityInstanceIdentity, out var identity))
        {
            return false;
        }

        const string HeroPrefix = "npc_dota_hero_";
        foreach (var stringOffset in new ulong[] { 0x20, 0x18, 0x28 })
        {
            if (TryReadPointer(identity + stringOffset, out var namePtr)
                && TryReadAsciiIdentifier(namePtr, out var rawName)
                && rawName.StartsWith(HeroPrefix, StringComparison.OrdinalIgnoreCase)
                && rawName.Length > HeroPrefix.Length)
            {
                shortName = rawName.ToLowerInvariant();
                return true;
            }
        }

        return false;
    }

    private static bool IsValidWorldOrigin(Vector3 origin) =>
        float.IsFinite(origin.X)
        && float.IsFinite(origin.Y)
        && float.IsFinite(origin.Z)
        && MathF.Abs(origin.X) <= 16384f
        && MathF.Abs(origin.Y) <= 16384f
        && (MathF.Abs(origin.X) > 0.01f || MathF.Abs(origin.Y) > 0.01f || MathF.Abs(origin.Z) > 0.01f);

    private bool IsLocalHeroInEnemySentryRange(
        ulong teamDataHandleArray,
        ulong entitySystem,
        LocalPlayerContext local,
        int enemyTeam)
    {
        if (!IsUserPointer(local.HeroAddress)
            || entitySystem == 0
            || local.LocalTeam is < 2 or > 14
            || enemyTeam is < 2 or > 14)
        {
            return false;
        }

        if (_memory.TryRead<byte>(local.HeroAddress + (ulong)_offsets.BaseEntityLifeState, out var localLifeState)
            && localLifeState != 0)
        {
            return false;
        }

        var manager = local.HeroAddress + (ulong)_offsets.BaseNpcModifierManager;
        if (_memory.TryRead<ulong>(manager + 0x08, out var managerOwner)
            && managerOwner == local.HeroAddress
            && _memory.TryRead<int>(manager + 0x10, out var modifierCount)
            && _memory.TryRead<ulong>(manager + 0x18, out var modifierArray)
            && _memory.TryRead<int>(manager + 0x20, out var modifierCapacity)
            && modifierCount is > 0 and <= 256
            && modifierCapacity >= modifierCount
            && modifierCapacity <= 4096
            && IsUserPointer(modifierArray)
            && _memory.TryReadBytes(modifierArray, modifierCount * sizeof(ulong), out var slotBytes)
            && slotBytes.Length == modifierCount * sizeof(ulong))
        {
            for (var i = 0; i < modifierCount; i++)
            {
                var buffAddress = BinaryPrimitives.ReadUInt64LittleEndian(slotBytes.AsSpan(i * sizeof(ulong), sizeof(ulong)));
                if (!IsUserPointer(buffAddress)
                    || !_memory.TryReadBytes(buffAddress, 0xA0, out var buffBytes)
                    || buffBytes.Length != 0xA0)
                {
                    continue;
                }

                var buffManager = BinaryPrimitives.ReadUInt64LittleEndian(buffBytes.AsSpan(0x30, 8));
                var flags = buffBytes[0x9E];
                if (buffManager != manager || (flags & 0x04) != 0)
                {
                    continue;
                }

                var namePtr = BinaryPrimitives.ReadUInt64LittleEndian(buffBytes.AsSpan(0x28, 8));
                if (!IsUserPointer(namePtr)
                    || !TryReadAsciiIdentifier(namePtr, out var buffName)
                    || !string.Equals(buffName, "modifier_truesight", StringComparison.Ordinal))
                {
                    continue;
                }

                var casterHandle = BinaryPrimitives.ReadUInt32LittleEndian(buffBytes.AsSpan(0x68, 4));
                var cachedTeam = BinaryPrimitives.ReadInt32LittleEndian(buffBytes.AsSpan(0x94, 4));
                if (casterHandle == local.HeroHandle)
                {
                    continue;
                }

                if (casterHandle is not (0 or uint.MaxValue or 0xFFFF_FFFEu))
                {
                    var casterEntity = _entities.ResolveByHandle(entitySystem, casterHandle);
                    if (IsUserPointer(casterEntity)
                        && _memory.TryRead<byte>(casterEntity + (ulong)_offsets.BaseEntityTeamNumber, out var casterTeam))
                    {
                        if (casterTeam == enemyTeam)
                        {
                            return true;
                        }
                    }
                    else
                    {
                        // Allied units/wards are always present in the client entity table; an unresolvable
                        // valid caster handle on modifier_truesight comes from an enemy ward/unit in Fog of War.
                        return true;
                    }
                }
                else if (cachedTeam == enemyTeam)
                {
                    return true;
                }
            }
        }

        // Also check visible enemy Sentry Wards within 1050 radius of the local hero.
        const float SentryTrueSightRadiusSq = 1050f * 1050f;
        const int VisibilityBitmapBytes = 256 * sizeof(ulong);
        if (teamDataHandleArray != 0
            && TryReadPointer(local.HeroAddress + (ulong)_offsets.BaseEntityGameSceneNode, out var localSceneNode)
            && _memory.TryRead<Vector3>(localSceneNode + (ulong)_offsets.GameSceneNodeAbsOrigin, out var localHeroOrigin)
            && _memory.TryRead<uint>(teamDataHandleArray + (ulong)(local.LocalTeam * sizeof(uint)), out var ownTeamHandle)
            && ownTeamHandle is not (0 or uint.MaxValue or 0xFFFF_FFFEu))
        {
            var ownTeamDataAddress = _entities.ResolveByHandle(entitySystem, ownTeamHandle);
            if (IsUserPointer(ownTeamDataAddress)
                && _memory.TryReadBytes(
                    ownTeamDataAddress + (ulong)_offsets.DataNonSpectatorNpcVisibleState,
                    VisibilityBitmapBytes,
                    out var bitmapBytes)
                && bitmapBytes.Length == VisibilityBitmapBytes)
            {
                for (var wordIndex = 0; wordIndex < 256; wordIndex++)
                {
                    var word = BinaryPrimitives.ReadUInt64LittleEndian(bitmapBytes.AsSpan(wordIndex * sizeof(ulong), sizeof(ulong)));
                    while (word != 0)
                    {
                        var bitIndex = BitOperations.TrailingZeroCount(word);
                        word &= word - 1;
                        var entityIndex = (wordIndex * 64) + bitIndex;
                        if (entityIndex == local.HeroEntityIndex)
                        {
                            continue;
                        }

                        var entityAddress = _entities.ResolveByIndex(entitySystem, entityIndex);
                        if (!IsUserPointer(entityAddress)
                            || !_memory.TryRead<byte>(entityAddress + (ulong)_offsets.BaseEntityTeamNumber, out var entityTeam)
                            || entityTeam != enemyTeam
                            || !_memory.TryRead<byte>(entityAddress + (ulong)_offsets.BaseEntityLifeState, out var entityLifeState)
                            || entityLifeState != 0
                            || !TryReadPointer(entityAddress + (ulong)_offsets.BaseEntityGameSceneNode, out var enemySceneNode)
                            || !_memory.TryRead<Vector3>(enemySceneNode + (ulong)_offsets.GameSceneNodeAbsOrigin, out var enemyOrigin))
                        {
                            continue;
                        }

                        var dx = localHeroOrigin.X - enemyOrigin.X;
                        var dy = localHeroOrigin.Y - enemyOrigin.Y;
                        if ((dx * dx) + (dy * dy) > SentryTrueSightRadiusSq)
                        {
                            continue;
                        }

                        if (EntityHasModifier(entityAddress, "modifier_item_ward_true_sight"))
                        {
                            return true;
                        }
                    }
                }
            }
        }

        return false;
    }

    private bool EntityHasModifier(ulong entityAddress, string targetModifierName)
    {
        var manager = entityAddress + (ulong)_offsets.BaseNpcModifierManager;
        if (!_memory.TryRead<ulong>(manager + 0x08, out var managerOwner)
            || managerOwner != entityAddress
            || !_memory.TryRead<int>(manager + 0x10, out var modifierCount)
            || !_memory.TryRead<ulong>(manager + 0x18, out var modifierArray)
            || !_memory.TryRead<int>(manager + 0x20, out var modifierCapacity)
            || modifierCount is <= 0 or > 64
            || modifierCapacity < modifierCount
            || modifierCapacity > 4096
            || !IsUserPointer(modifierArray)
            || !_memory.TryReadBytes(modifierArray, modifierCount * sizeof(ulong), out var slotBytes)
            || slotBytes.Length != modifierCount * sizeof(ulong))
        {
            return false;
        }

        for (var i = 0; i < modifierCount; i++)
        {
            var buffAddress = BinaryPrimitives.ReadUInt64LittleEndian(slotBytes.AsSpan(i * sizeof(ulong), sizeof(ulong)));
            if (!IsUserPointer(buffAddress)
                || !_memory.TryReadBytes(buffAddress, 0xA0, out var buffBytes)
                || buffBytes.Length != 0xA0)
            {
                continue;
            }

            var buffManager = BinaryPrimitives.ReadUInt64LittleEndian(buffBytes.AsSpan(0x30, 8));
            var flags = buffBytes[0x9E];
            if (buffManager != manager || (flags & 0x04) != 0)
            {
                continue;
            }

            var namePtr = BinaryPrimitives.ReadUInt64LittleEndian(buffBytes.AsSpan(0x28, 8));
            if (IsUserPointer(namePtr)
                && TryReadAsciiIdentifier(namePtr, out var buffName)
                && string.Equals(buffName, targetModifierName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryReadLocalServerHeroVisibility(
        int? processId,
        long? attachmentGeneration,
        LocalPlayerContext local,
        int enemyTeam,
        out bool isVisible,
        out int visibilityMask,
        out string detail)
    {
        isVisible = false;
        visibilityMask = 0;
        detail = string.Empty;

        if (processId is not int pid
            || !TryResolveServerEntitySystemGlobal(pid, attachmentGeneration, out var serverEntitySystemGlobal)
            || !TryReadPointer(serverEntitySystemGlobal, out var serverEntitySystem))
        {
            return false;
        }

        var serverHero = _entities.ResolveByIndex(serverEntitySystem, local.HeroEntityIndex);
        if (!IsUserPointer(serverHero)
            || !_memory.TryRead<ulong>(serverHero + (ulong)_offsets.EntityInstanceIdentity, out var serverIdentity)
            || !IsUserPointer(serverIdentity)
            || !_memory.TryRead<uint>(serverIdentity + (ulong)_offsets.EntityIdentityHandle, out var serverHandle)
            || (int)(serverHandle & (uint)_offsets.EntityHandleIndexMask) != local.HeroEntityIndex
            || _entities.ResolveByHandle(serverEntitySystem, serverHandle) != serverHero)
        {
            return false;
        }

        if (!_memory.TryRead<byte>(serverHero + (ulong)_offsets.ServerBaseEntityTeamNumber, out var serverTeam)
            || serverTeam != local.LocalTeam
            || !_memory.TryRead<int>(serverHero + (ulong)_offsets.ServerBaseModelEntityTeamVisibilityBitmask, out var rawServerMask)
            || (rawServerMask & ~0x7FFF) != 0)
        {
            return false;
        }

        if (!TryReadPointer(serverEntitySystemGlobal, out var confirmedServerEntitySystem)
            || confirmedServerEntitySystem != serverEntitySystem
            || _entities.ResolveByHandle(serverEntitySystem, serverHandle) != serverHero
            || !_memory.TryRead<byte>(serverHero + (ulong)_offsets.ServerBaseEntityTeamNumber, out var confirmedServerTeam)
            || confirmedServerTeam != local.LocalTeam)
        {
            return false;
        }

        visibilityMask = rawServerMask;
        isVisible = (rawServerMask & (1 << enemyTeam)) != 0;
        detail = isVisible
            ? $"Local-host server entity #{local.HeroEntityIndex} visibility bitmask (0x{unchecked((uint)rawServerMask):X8}) reports the local hero visible to enemy team {enemyTeam}."
            : $"Local-host server entity #{local.HeroEntityIndex} visibility bitmask (0x{unchecked((uint)rawServerMask):X8}) reports the local hero hidden from enemy team {enemyTeam}.";
        return true;
    }

    private bool TryReadClientOnlineHeroVisibility(
        int? processId,
        long? attachmentGeneration,
        ulong teamDataHandleArray,
        ulong entitySystem,
        LocalPlayerContext local,
        int enemyTeam,
        out bool isVisible,
        out string detail)
    {
        isVisible = false;
        detail = string.Empty;

        if (!IsUserPointer(local.HeroAddress)
            || teamDataHandleArray == 0
            || entitySystem == 0
            || local.LocalTeam is < 2 or > 14
            || enemyTeam is < 2 or > 14)
        {
            return false;
        }

        // Validate that local.HeroAddress has an initialized CDOTA_ModifierManager whose owner points back to local.HeroAddress.
        var manager = local.HeroAddress + (ulong)_offsets.BaseNpcModifierManager;
        if (!_memory.TryRead<ulong>(manager + 0x08, out var managerOwner)
            || !_memory.TryRead<int>(manager + 0x10, out var modifierCount)
            || !_memory.TryRead<ulong>(manager + 0x18, out var modifierArray)
            || !_memory.TryRead<int>(manager + 0x20, out var modifierCapacity))
        {
            return false;
        }

        if (managerOwner != local.HeroAddress
            || modifierCount is < 0 or > 256
            || modifierCapacity < modifierCount
            || modifierCapacity > 4096)
        {
            return false;
        }

        // Require the local player's own team C_DOTA_DataNonSpectator entity to be present and valid.
        var ownTeamHandleAddress = teamDataHandleArray + (ulong)(local.LocalTeam * sizeof(uint));
        if (!_memory.TryRead<uint>(ownTeamHandleAddress, out var ownTeamHandle)
            || ownTeamHandle is 0 or uint.MaxValue or 0xFFFF_FFFEu)
        {
            return false;
        }

        var ownTeamDataAddress = _entities.ResolveByHandle(entitySystem, ownTeamHandle);
        if (!IsUserPointer(ownTeamDataAddress)
            || !_memory.TryRead<byte>(ownTeamDataAddress + (ulong)_offsets.BaseEntityTeamNumber, out var ownTeamNumber)
            || ownTeamNumber != local.LocalTeam)
        {
            return false;
        }

        if (!_memory.TryRead<byte>(local.HeroAddress + (ulong)_offsets.BaseEntityLifeState, out var localLifeState)
            || !_memory.TryRead<float>(local.HeroAddress + (ulong)_offsets.BaseNpcInvisibilityLevel, out var invisLevel)
            || !_memory.TryRead<ulong>(local.HeroAddress + (ulong)_offsets.BaseNpcUnitState64, out var unitState64))
        {
            return false;
        }

        // In the 2026-10-20 client.dll, TrueSightMask/ValidMask/FowPositionMask/ValidMask sit at manager + 0x380.
        ushort trueSightMask = 0;
        ushort trueSightValidMask = 0;
        ushort fowPositionMask = 0;
        ushort fowPositionValidMask = 0;
        if (_memory.TryReadBytes(manager + 0x380, 8, out var teamCacheBytes) && teamCacheBytes.Length == 8)
        {
            trueSightMask = BinaryPrimitives.ReadUInt16LittleEndian(teamCacheBytes.AsSpan(0, 2));
            trueSightValidMask = BinaryPrimitives.ReadUInt16LittleEndian(teamCacheBytes.AsSpan(2, 2));
            fowPositionMask = BinaryPrimitives.ReadUInt16LittleEndian(teamCacheBytes.AsSpan(4, 2));
            fowPositionValidMask = BinaryPrimitives.ReadUInt16LittleEndian(teamCacheBytes.AsSpan(6, 2));
        }

        var enemyBit = 1 << enemyTeam;
        // TrueSight only counters invisibility when an enemy vision source sees the hero; it does NOT grant FoW vision by itself.
        var enemyHasTrueSight = (trueSightValidMask & enemyBit) != 0 && (trueSightMask & enemyBit) != 0;
        var enemyHasFowPosition = (fowPositionValidMask & enemyBit) != 0 && (fowPositionMask & enemyBit) != 0;

        const ulong ModifierStateInvisible = 1UL << 7;
        const ulong ModifierStateProvidesVision = 1UL << 10;
        const ulong ModifierStateTrueSightImmune = 1UL << 29;

        var hasSlarkPassive = false;
        var hasSlarkPassiveRegen = false;
        string? enemyRevealModifier = null;

        if (modifierCount > 0 && IsUserPointer(modifierArray)
            && _memory.TryReadBytes(modifierArray, modifierCount * sizeof(ulong), out var slotBytes)
            && slotBytes.Length == modifierCount * sizeof(ulong))
        {
            for (var i = 0; i < modifierCount; i++)
            {
                var buffAddress = BinaryPrimitives.ReadUInt64LittleEndian(slotBytes.AsSpan(i * sizeof(ulong), sizeof(ulong)));
                if (!IsUserPointer(buffAddress)
                    || !_memory.TryReadBytes(buffAddress, 0xA0, out var buffBytes)
                    || buffBytes.Length != 0xA0)
                {
                    continue;
                }

                var buffManager = BinaryPrimitives.ReadUInt64LittleEndian(buffBytes.AsSpan(0x30, 8));
                var flags = buffBytes[0x9E];
                if (buffManager != manager || (flags & 0x04) != 0)
                {
                    continue;
                }

                var namePtr = BinaryPrimitives.ReadUInt64LittleEndian(buffBytes.AsSpan(0x28, 8));
                if (!IsUserPointer(namePtr) || !TryReadAsciiIdentifier(namePtr, out var buffName))
                {
                    continue;
                }

                if (string.Equals(buffName, "modifier_slark_shadow_dance_passive", StringComparison.Ordinal))
                {
                    hasSlarkPassive = true;
                }
                else if (string.Equals(buffName, "modifier_slark_shadow_dance_passive_regen", StringComparison.Ordinal))
                {
                    hasSlarkPassiveRegen = true;
                }

                var casterHandle = BinaryPrimitives.ReadUInt32LittleEndian(buffBytes.AsSpan(0x68, 4));
                var cachedTeam = BinaryPrimitives.ReadInt32LittleEndian(buffBytes.AsSpan(0x94, 4));
                var isEnemyModifier = false;
                if (casterHandle != local.HeroHandle)
                {
                    if (casterHandle is not (0 or uint.MaxValue or 0xFFFF_FFFEu))
                    {
                        var casterEntity = _entities.ResolveByHandle(entitySystem, casterHandle);
                        if (IsUserPointer(casterEntity)
                            && _memory.TryRead<byte>(casterEntity + (ulong)_offsets.BaseEntityTeamNumber, out var casterTeam))
                        {
                            isEnemyModifier = casterTeam == enemyTeam;
                        }
                        else
                        {
                            isEnemyModifier = cachedTeam == enemyTeam
                                || string.Equals(buffName, "modifier_truesight", StringComparison.Ordinal);
                        }
                    }
                    else
                    {
                        isEnemyModifier = cachedTeam == enemyTeam;
                    }
                }

                if (isEnemyModifier)
                {
                    if (IsEnemyTrueSightModifier(buffName))
                    {
                        enemyHasTrueSight = true;
                    }

                    if (IsEnemyFowRevealModifier(buffName))
                    {
                        enemyRevealModifier ??= buffName;
                    }
                }
            }
        }

        if (hasSlarkPassive && localLifeState == 0)
        {
            isVisible = !hasSlarkPassiveRegen;
            detail = isVisible
                ? $"Client online visibility: Slark Shadow Dance passive regen is inactive on hero #{local.HeroEntityIndex} (visible to enemy team {enemyTeam})."
                : $"Client online visibility: Slark Shadow Dance passive regen is active on hero #{local.HeroEntityIndex} (hidden from enemy team {enemyTeam}).";
            return true;
        }

        var isTrueSightImmune = (unitState64 & ModifierStateTrueSightImmune) != 0;
        var isHeroInvisible = ((unitState64 & ModifierStateInvisible) != 0
            || (float.IsFinite(invisLevel) && invisLevel >= 0.99f))
            && (!enemyHasTrueSight || isTrueSightImmune);

        if (!isHeroInvisible && (enemyHasFowPosition || (unitState64 & ModifierStateProvidesVision) != 0 || enemyRevealModifier is not null))
        {
            isVisible = true;
            var reason = enemyRevealModifier is not null
                ? $"modifier '{enemyRevealModifier}'"
                : "provides-vision state";
            detail = $"Client online visibility: local hero #{local.HeroEntityIndex} is revealed to enemy team {enemyTeam} via {reason}.";
            return true;
        }

        if (localLifeState == 0
            && !isHeroInvisible
            && TryReadPointer(local.HeroAddress + (ulong)_offsets.BaseEntityGameSceneNode, out var localSceneNode)
            && _memory.TryRead<Vector3>(localSceneNode + (ulong)_offsets.GameSceneNodeAbsOrigin, out var localHeroOrigin))
        {
            ClientFogOcclusionGrid fogGrid = default;
            var hasClientModule = _memory.TryGetModule("client.dll", out var clientModule);
            var hasFogOcclusion = processId is int pid
                && hasClientModule
                && TryReadClientFogOcclusionGrid(pid, attachmentGeneration, clientModule, out fogGrid);

            const int VisibilityBitmapBytes = 256 * sizeof(ulong);
            if (_memory.TryReadBytes(
                    ownTeamDataAddress + (ulong)_offsets.DataNonSpectatorNpcVisibleState,
                    VisibilityBitmapBytes,
                    out var bitmapBytes)
                && bitmapBytes.Length == VisibilityBitmapBytes)
            {
                for (var wordIndex = 0; wordIndex < 256; wordIndex++)
                {
                    var word = BinaryPrimitives.ReadUInt64LittleEndian(bitmapBytes.AsSpan(wordIndex * sizeof(ulong), sizeof(ulong)));
                    while (word != 0)
                    {
                        var bitIndex = BitOperations.TrailingZeroCount(word);
                        word &= word - 1;
                        var entityIndex = (wordIndex * 64) + bitIndex;
                        if (entityIndex == local.HeroEntityIndex)
                        {
                            continue;
                        }

                        var entityAddress = _entities.ResolveByIndex(entitySystem, entityIndex);
                        if (!IsUserPointer(entityAddress)
                            || !_memory.TryRead<byte>(entityAddress + (ulong)_offsets.BaseEntityTeamNumber, out var entityTeam)
                            || entityTeam != enemyTeam
                            || !_memory.TryRead<byte>(entityAddress + (ulong)_offsets.BaseEntityLifeState, out var entityLifeState)
                            || entityLifeState != 0)
                        {
                            continue;
                        }

                        // In a real client.dll process, ensure the entity is an actual C_DOTA_BaseNPC (its embedded modifier manager points back to entityAddress).
                        if (hasClientModule
                            && (!_memory.TryRead<ulong>(entityAddress + (ulong)_offsets.BaseNpcModifierManager + 0x08, out var entityMgrOwner)
                                || entityMgrOwner != entityAddress))
                        {
                            continue;
                        }

                        if (!TryReadPointer(entityAddress + (ulong)_offsets.BaseEntityGameSceneNode, out var enemySceneNode)
                            || !_memory.TryRead<Vector3>(enemySceneNode + (ulong)_offsets.GameSceneNodeAbsOrigin, out var enemyOrigin))
                        {
                            continue;
                        }

                        // Ground enemy units on lower elevation cannot see up into highground Fog of War.
                        if (localHeroOrigin.Z > enemyOrigin.Z + 64f)
                        {
                            continue;
                        }

                        _memory.TryRead<int>(entityAddress + (ulong)_offsets.BaseNpcDayTimeVisionRange, out var dayVision);
                        _memory.TryRead<int>(entityAddress + (ulong)_offsets.BaseNpcNightTimeVisionRange, out var nightVision);
                        var visionRange = dayVision is > 0 and <= 4000
                            ? dayVision
                            : nightVision is > 0 and <= 4000
                                ? nightVision
                                : 0;
                        if (visionRange <= 0)
                        {
                            continue;
                        }

                        var dx = localHeroOrigin.X - enemyOrigin.X;
                        var dy = localHeroOrigin.Y - enemyOrigin.Y;
                        var distanceSq = (dx * dx) + (dy * dy);
                        if (distanceSq > (float)visionRange * visionRange)
                        {
                            continue;
                        }

                        if (hasFogOcclusion && !HasUnobstructedLineOfSight(fogGrid, enemyOrigin, localHeroOrigin))
                        {
                            continue;
                        }

                        isVisible = true;
                        detail = $"Client online visibility: active enemy entity #{entityIndex} (vision {visionRange}) has unobstructed sight ({MathF.Sqrt(distanceSq):F0}) of local hero #{local.HeroEntityIndex}.";
                        return true;
                    }
                }
            }
        }

        isVisible = false;
        detail = isHeroInvisible
            ? $"Client online visibility: local hero #{local.HeroEntityIndex} is invisible without enemy true sight."
            : $"Client online visibility: no active enemy vision source or reveal modifier has sight of local hero #{local.HeroEntityIndex}.";
        return true;
    }

    private static bool IsEnemyTrueSightModifier(string modifierName) =>
        modifierName is
            "modifier_truesight"
            or "modifier_item_dustofappearance"
            or "modifier_bounty_hunter_track"
            or "modifier_slardar_amplify_damage"
            or "modifier_sniper_assassinate";

    private static bool IsEnemyFowRevealModifier(string modifierName) =>
        modifierName is
            "modifier_bounty_hunter_track"
            or "modifier_slardar_amplify_damage"
            or "modifier_bloodseeker_thirst_vision"
            or "modifier_spirit_breaker_charge_of_darkness_vision"
            or "modifier_sniper_assassinate";

    private bool TryReadClientFogOcclusionGrid(
        int processId,
        long? attachmentGeneration,
        ProcessModuleSnapshot clientModule,
        out ClientFogOcclusionGrid grid)
    {
        grid = default;
        var cacheKey = new ClientFogCacheKey(
            processId,
            attachmentGeneration,
            clientModule.BaseAddress,
            clientModule.Size);
        ulong fogSystemAddress = 0;
        lock (_serverGate)
        {
            if (attachmentGeneration is not null
                && _clientFogCacheKey == cacheKey
                && _cachedClientFogSystemAddress != 0
                && (_memory as IProcessAttachmentGeneration)?.AttachmentGeneration == attachmentGeneration)
            {
                fogSystemAddress = _cachedClientFogSystemAddress;
            }
        }

        if (fogSystemAddress == 0)
        {
            if (!ModulePatternScanner.TryResolveRipRelativeAddress(
                    _memory,
                    clientModule,
                    ClientFogOfWarSystemPattern,
                    displacementOffset: 6,
                    instructionLength: 10,
                    out fogSystemAddress))
            {
                return false;
            }

            lock (_serverGate)
            {
                _clientFogCacheKey = attachmentGeneration is null ? null : cacheKey;
                _cachedClientFogSystemAddress = attachmentGeneration is null ? 0 : fogSystemAddress;
            }
        }

        // CDOTAFogOfWarSystem context map: slots at +0x60, count at +0x68, capacity at +0x6C, entry stride 0x18, value (CFoW*) at +0x10.
        if (!TryReadPointer(fogSystemAddress + 0x60, out var slots)
            || !_memory.TryRead<int>(fogSystemAddress + 0x68, out var count)
            || !_memory.TryRead<int>(fogSystemAddress + 0x6C, out var capacity)
            || count <= 0
            || capacity is <= 0 or > 64)
        {
            return false;
        }

        ulong cfow = 0;
        for (var i = 0; i < capacity; i++)
        {
            var entryAddr = slots + (ulong)(i * 0x18);
            if (_memory.TryRead<int>(entryAddr + 0x08, out var key)
                && key == 0
                && TryReadPointer(entryAddr + 0x10, out var candidate))
            {
                cfow = candidate;
                break;
            }
        }

        if (!IsUserPointer(cfow)
            || !_memory.TryRead<float>(cfow + (ulong)_offsets.FogMinX, out var minX)
            || !_memory.TryRead<float>(cfow + (ulong)_offsets.FogMinY, out var minY)
            || !_memory.TryRead<float>(cfow + (ulong)_offsets.FogInvCellSize, out var invCellSize)
            || !_memory.TryRead<int>(cfow + (ulong)_offsets.FogGridWidth, out var width)
            || !_memory.TryRead<int>(cfow + (ulong)_offsets.FogGridHeight, out var height)
            || !TryReadPointer(cfow + 0x3E0, out var occlusionGridPtr)
            || !float.IsFinite(minX)
            || !float.IsFinite(minY)
            || !float.IsFinite(invCellSize)
            || invCellSize <= 0f
            || width is <= 0 or > 1024
            || height is <= 0 or > 1024)
        {
            return false;
        }

        var cellCount = width * height;
        if (!_memory.TryReadBytes(occlusionGridPtr, cellCount, out var cells)
            || cells.Length != cellCount)
        {
            return false;
        }

        grid = new ClientFogOcclusionGrid(minX, minY, invCellSize, width, height, cells);
        return true;
    }

    private static bool HasUnobstructedLineOfSight(
        ClientFogOcclusionGrid grid,
        Vector3 viewerOrigin,
        Vector3 targetOrigin)
    {
        var x0 = (int)((viewerOrigin.X - grid.MinX) * grid.InvCellSize);
        var y0 = (int)((viewerOrigin.Y - grid.MinY) * grid.InvCellSize);
        var x1 = (int)((targetOrigin.X - grid.MinX) * grid.InvCellSize);
        var y1 = (int)((targetOrigin.Y - grid.MinY) * grid.InvCellSize);

        if (x0 < 0 || x0 >= grid.Width || y0 < 0 || y0 >= grid.Height
            || x1 < 0 || x1 >= grid.Width || y1 < 0 || y1 >= grid.Height)
        {
            return false;
        }

        var viewerCell = grid.Cells[(x0 * grid.Height) + y0];
        var viewerHeight = viewerCell & 0x1F;
        var targetCell = grid.Cells[(x1 * grid.Height) + y1];
        if ((targetCell & 0x1F) > viewerHeight || (targetCell & 0xE0) != 0)
        {
            return false;
        }

        var dx = Math.Abs(x1 - x0);
        var dy = Math.Abs(y1 - y0);
        var sx = x0 < x1 ? 1 : -1;
        var sy = y0 < y1 ? 1 : -1;
        var err = dx - dy;
        var cx = x0;
        var cy = y0;

        while (cx != x1 || cy != y1)
        {
            if (cx != x0 || cy != y0)
            {
                var cell = grid.Cells[(cx * grid.Height) + cy];
                // Low 5 bits (0x1F) are terrain elevation level; high 3 bits (0xE0) are tree / vision blocker flags.
                if ((cell & 0x1F) > viewerHeight || (cell & 0xE0) != 0)
                {
                    return false;
                }
            }

            var e2 = 2 * err;
            if (e2 > -dy)
            {
                err -= dy;
                cx += sx;
            }

            if (e2 < dx)
            {
                err += dx;
                cy += sy;
            }
        }

        return true;
    }

    private bool TryReadAsciiIdentifier(ulong address, out string identifier)
    {
        identifier = string.Empty;
        if (!_memory.TryReadBytes(address, 96, out var bytes) || bytes.Length == 0)
        {
            return false;
        }

        var length = 0;
        while (length < bytes.Length && bytes[length] != 0)
        {
            var b = bytes[length];
            if (b is < 32 or > 126)
            {
                return false;
            }

            length++;
        }

        if (length is 0 or >= 96)
        {
            return false;
        }

        identifier = Encoding.ASCII.GetString(bytes, 0, length);
        return true;
    }

    private bool TryResolveServerEntitySystemGlobal(
        int processId,
        long? attachmentGeneration,
        out ulong serverEntitySystemGlobal)
    {
        serverEntitySystemGlobal = 0;
        if (!_memory.TryGetModule(ServerModuleName, out var serverModule))
        {
            return false;
        }

        var cacheKey = new ServerCacheKey(
            processId,
            attachmentGeneration,
            serverModule.BaseAddress,
            serverModule.Size);
        lock (_serverGate)
        {
            if (attachmentGeneration is not null
                && _serverCacheKey == cacheKey
                && _cachedServerEntitySystemGlobal != 0
                && (_memory as IProcessAttachmentGeneration)?.AttachmentGeneration == attachmentGeneration)
            {
                serverEntitySystemGlobal = _cachedServerEntitySystemGlobal;
                return true;
            }
        }

        if (!ModulePatternScanner.TryResolveRipRelativeAddress(
                _memory,
                serverModule,
                ServerEntitySystemPattern,
                displacementOffset: 3,
                instructionLength: 7,
                out var resolved)
            && !ModulePatternScanner.TryResolveRipRelativeAddress(
                _memory,
                serverModule,
                ServerEntitySystemFallbackPattern,
                displacementOffset: 3,
                instructionLength: 7,
                out resolved))
        {
            return false;
        }

        if (AttachmentChanged(processId, attachmentGeneration))
        {
            return false;
        }

        lock (_serverGate)
        {
            _serverCacheKey = attachmentGeneration is null ? null : cacheKey;
            _cachedServerEntitySystemGlobal = attachmentGeneration is null ? 0 : resolved;
        }

        serverEntitySystemGlobal = resolved;
        return true;
    }

    private bool AttachmentChanged(int? processId, long? attachmentGeneration) =>
        !_memory.IsAttached
        || _memory.ProcessId != processId
        || (_memory as IProcessAttachmentGeneration)?.AttachmentGeneration != attachmentGeneration;

    private VisibilitySnapshot AttachmentChangedUnknown() =>
        VisibilitySnapshot.Unknown(
            "Process attachment changed during the visibility sample; discarded mixed-generation data.",
            isAttached: _memory.IsAttached,
            processId: _memory.ProcessId);

    private bool TryReadPointer(ulong address, out ulong pointer) =>
        _memory.TryRead(address, out pointer) && IsUserPointer(pointer);

    private static bool IsUserPointer(ulong pointer) =>
        pointer != 0 && pointer <= 0x0000_7FFF_FFFF_FFFF;

    private static VisibilitySnapshot AttachedUnknown(int? processId, string detail) =>
        VisibilitySnapshot.Unknown(detail, isAttached: true, processId: processId);

    private readonly record struct ServerCacheKey(
        int ProcessId,
        long? AttachmentGeneration,
        ulong ServerBaseAddress,
        int ServerSize);

    private readonly record struct ClientFogCacheKey(
        int ProcessId,
        long? AttachmentGeneration,
        ulong ClientBaseAddress,
        int ClientSize);

    private readonly record struct ClientFogOcclusionGrid(
        float MinX,
        float MinY,
        float InvCellSize,
        int Width,
        int Height,
        byte[] Cells);
}
