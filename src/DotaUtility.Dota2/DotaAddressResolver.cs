using DotaUtility.Memory;

namespace DotaUtility.Dota2;

/// <summary>
/// Resolves patch-sensitive addresses from instructions in the currently loaded Dota modules.
/// Entity/network results are global pointer slots; team-data and local-controller results are array addresses.
/// </summary>
public sealed class DotaAddressResolver : IDotaAddressResolver
{
    private static readonly BytePattern EntitySystemPattern =
        BytePattern.Parse("48 89 0D ?? ?? ?? ?? E9 ?? ?? ?? ?? CC");

    private static readonly BytePattern EntitySystemFallbackPattern =
        BytePattern.Parse("48 8B 1D ?? ?? ?? ?? 48 89 1D ?? ?? ?? ?? 4C 63 B3");

    private static readonly BytePattern NetworkGameClientPattern =
        BytePattern.Parse("48 89 3D ?? ?? ?? ?? FF 87");

    // OnReplayComap_TaggedAsVisibleByTeam contains a reference to the team-data array.
    // Its registration identifies a replay-compatibility callback, not proof of a
    // live network callback. The pattern is used only to locate the array.
    private static readonly BytePattern TeamDataHandleArrayPattern =
        BytePattern.Parse(
            "41 56 48 83 EC 40 48 8B 02 4C 8B F1 48 8B 50 08 8B 02 " +
            "39 05 ?? ?? ?? ?? 0F 85 ?? ?? ?? ?? 48 8B 42 10 48 89 5C 24 50 " +
            "BB 01 00 00 00 48 89 6C 24 60 48 89 74 24 38 48 8D 35 ?? ?? ?? ?? " +
            "4C 89 64 24 28 45 33 E4");

    // Native primary local-controller getter: split-screen index 0 reads array[0].
    // The array holds controller pointers, not Dota PlayerIDs or network slot IDs.
    private static readonly BytePattern LocalControllerArrayPattern =
        BytePattern.Parse("33 C0 83 F9 FF 0F 44 C8 48 63 C1 48 8D 0D ?? ?? ?? ?? 48 8B 04 C1 C3");

    private readonly IProcessMemoryAccessor _memory;
    private readonly bool _resolveNetworkGameClient;
    private readonly object _gate = new();
    private CacheKey? _cachedKey;
    private DotaResolvedAddresses? _cachedAddresses;

    public DotaAddressResolver(
        IProcessMemoryAccessor memory,
        bool resolveNetworkGameClient = true)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _resolveNetworkGameClient = resolveNetworkGameClient;
    }

    public bool TryResolve(out DotaResolvedAddresses addresses, out string detail)
    {
        addresses = default!;
        detail = string.Empty;

        if (!_memory.IsAttached || _memory.ProcessId is not int processId)
        {
            detail = "Dota process is not attached.";
            InvalidateCache();
            return false;
        }

        var attachmentGeneration = (_memory as IProcessAttachmentGeneration)?.AttachmentGeneration;
        if (!_memory.TryGetModule("client.dll", out var clientModule))
        {
            detail = "client.dll is not loaded yet.";
            return false;
        }

        ProcessModuleSnapshot? engineModule = null;
        if (_resolveNetworkGameClient)
        {
            if (!_memory.TryGetModule("engine2.dll", out engineModule))
            {
                detail = "engine2.dll is not loaded yet.";
                return false;
            }
        }

        var cacheKey = new CacheKey(
            processId,
            attachmentGeneration,
            clientModule.BaseAddress,
            clientModule.Size,
            engineModule?.BaseAddress ?? 0,
            engineModule?.Size ?? 0);
        lock (_gate)
        {
            if (attachmentGeneration is not null
                && _cachedKey == cacheKey
                && _cachedAddresses is not null
                && (_memory as IProcessAttachmentGeneration)?.AttachmentGeneration == attachmentGeneration)
            {
                addresses = _cachedAddresses;
                detail = "Using cached Source 2 global addresses.";
                return true;
            }
        }

        if (!TryResolveEntitySystem(clientModule, out var entitySystemGlobal))
        {
            detail = "Could not locate the Source 2 entity-system global in client.dll; the game signature may have changed.";
            return false;
        }

        ulong networkGameClientGlobal = 0;
        if (engineModule is not null)
        {
            if (!ModulePatternScanner.TryResolveRipRelativeAddress(
                    _memory,
                    engineModule,
                    NetworkGameClientPattern,
                    displacementOffset: 3,
                    instructionLength: 7,
                    out networkGameClientGlobal))
            {
                detail = "Could not locate CNetworkGameClient in engine2.dll; the game signature may have changed.";
                return false;
            }
        }

        if (!ModulePatternScanner.TryResolveRipRelativeAddress(
                _memory,
                clientModule,
                TeamDataHandleArrayPattern,
                displacementOffset: 57,
                instructionLength: 61,
                out var teamDataHandleArray))
        {
            detail = "Could not locate the Dota team-data handle array in client.dll; the game signature may have changed.";
            return false;
        }

        if (!ModulePatternScanner.TryResolveRipRelativeAddress(
                _memory,
                clientModule,
                LocalControllerArrayPattern,
                displacementOffset: 14,
                instructionLength: 18,
                out var localControllerArray))
        {
            detail = "Could not locate the native local-controller array in client.dll; refusing to guess from a Dota PlayerID.";
            return false;
        }

        if (!_memory.IsAttached
            || _memory.ProcessId != processId
            || (_memory as IProcessAttachmentGeneration)?.AttachmentGeneration != attachmentGeneration)
        {
            detail = "Process attachment changed during address resolution; stale addresses were discarded.";
            InvalidateCache();
            return false;
        }

        addresses = new DotaResolvedAddresses(
            entitySystemGlobal,
            networkGameClientGlobal,
            teamDataHandleArray,
            localControllerArray);
        lock (_gate)
        {
            // Without an attachment generation, do not cache absolute addresses.
            _cachedKey = attachmentGeneration is null ? null : cacheKey;
            _cachedAddresses = attachmentGeneration is null ? null : addresses;
        }

        detail = _resolveNetworkGameClient
            ? "Resolved Source 2 entity-system, network-client, team-data, and local-controller addresses by signature."
            : "Resolved Source 2 entity-system, team-data, and local-controller addresses by signature.";
        return true;
    }

    private bool TryResolveEntitySystem(ProcessModuleSnapshot clientModule, out ulong address)
    {
        if (ModulePatternScanner.TryResolveRipRelativeAddress(
                _memory,
                clientModule,
                EntitySystemPattern,
                displacementOffset: 3,
                instructionLength: 7,
                out address))
        {
            return true;
        }

        return ModulePatternScanner.TryResolveRipRelativeAddress(
            _memory,
            clientModule,
            EntitySystemFallbackPattern,
            displacementOffset: 3,
            instructionLength: 7,
            out address);
    }

    private void InvalidateCache()
    {
        lock (_gate)
        {
            _cachedKey = null;
            _cachedAddresses = null;
        }
    }

    private readonly record struct CacheKey(
        int ProcessId,
        long? AttachmentGeneration,
        ulong ClientBaseAddress,
        int ClientSize,
        ulong EngineBaseAddress,
        int EngineSize);
}
