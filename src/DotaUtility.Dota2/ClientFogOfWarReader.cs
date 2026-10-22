using System.Numerics;
using DotaUtility.Memory;

namespace DotaUtility.Dota2;

public readonly record struct ClientFogOfWarSample(
    bool IsTileVisible,
    ushort Cell,
    int GridX,
    int GridY,
    ulong ContextAddress,
    ulong GridAddress);

/// <summary>
/// Reads the primary client-side FoW rendering context without calling into or writing to Dota.
/// Reverse engineering on the Sep 24 2026 client established that CFoW cell bit 14
/// (0x4000) is cleared every solve pass and set when the tile is currently visible in
/// that context. The primary context/layer is diagnostic only: it is not a stable mapping
/// to the opposing team's authoritative hero-visibility state.
/// </summary>
public sealed class ClientFogOfWarReader
{
    private const int MaximumContextCapacity = 512;
    private const int MaximumLayerCount = 8;
    private const int MaximumGridDimension = 4096;

    private readonly IProcessMemoryAccessor _memory;
    private readonly DotaOffsets _offsets;

    public ClientFogOfWarReader(
        IProcessMemoryAccessor memory,
        DotaOffsets offsets)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _offsets = offsets ?? throw new ArgumentNullException(nameof(offsets));
    }

    public bool TryReadPrimaryContextTileVisibility(
        ulong fogSystem,
        ulong heroAddress,
        out ClientFogOfWarSample sample,
        out string detail)
    {
        sample = default;
        detail = string.Empty;

        if (!IsPlausiblePointer(fogSystem))
        {
            detail = "Client FoW system address is invalid.";
            return false;
        }

        if (!TryResolvePrimaryContext(fogSystem, out var fogContext))
        {
            detail = "Client FoW system is present, but its primary context is not available yet.";
            return false;
        }

        if (!_memory.TryRead<int>(
                fogContext + (ulong)_offsets.FogLayerCount,
                out var layerCount)
            || layerCount is <= 0 or > MaximumLayerCount)
        {
            detail = "Client FoW context has an invalid layer count.";
            return false;
        }

        if (!_memory.TryRead<float>(
                fogContext + (ulong)_offsets.FogMinX,
                out var minX)
            || !_memory.TryRead<float>(
                fogContext + (ulong)_offsets.FogMinY,
                out var minY)
            || !_memory.TryRead<float>(
                fogContext + (ulong)_offsets.FogInvCellSize,
                out var inverseCellSize)
            || !_memory.TryRead<int>(
                fogContext + (ulong)_offsets.FogGridWidth,
                out var width)
            || !_memory.TryRead<int>(
                fogContext + (ulong)_offsets.FogGridHeight,
                out var height)
            || !float.IsFinite(minX)
            || !float.IsFinite(minY)
            || !float.IsFinite(inverseCellSize)
            || inverseCellSize <= 0
            || width is <= 0 or > MaximumGridDimension
            || height is <= 0 or > MaximumGridDimension)
        {
            detail = "Client FoW grid metadata is not valid yet.";
            return false;
        }

        if (!_memory.TryRead<ulong>(
                heroAddress + (ulong)_offsets.BaseEntityGameSceneNode,
                out var sceneNode)
            || !IsPlausiblePointer(sceneNode)
            || !_memory.TryRead<Vector3>(
                sceneNode + (ulong)_offsets.GameSceneNodeAbsOrigin,
                out var origin)
            || !float.IsFinite(origin.X)
            || !float.IsFinite(origin.Y))
        {
            detail = "Local hero world position is unavailable for the client FoW lookup.";
            return false;
        }

        var gridX = (int)MathF.Floor((origin.X - minX) * inverseCellSize);
        var gridY = (int)MathF.Floor((origin.Y - minY) * inverseCellSize);
        if (gridX < 0 || gridY < 0 || gridX >= width || gridY >= height)
        {
            detail = $"Local hero is outside the client FoW grid at ({gridX}, {gridY}).";
            return false;
        }

        // Layer 0 belongs to the primary client rendering context. Live validation showed
        // that it can report the local Radiant fountain tile as visible, so it must not be
        // interpreted as the opposing team's authoritative view.
        if (!_memory.TryRead<ulong>(
                fogContext + (ulong)_offsets.FogLayerArrayBase,
                out var gridAddress)
            || !IsPlausiblePointer(gridAddress))
        {
            detail = "Primary client FoW layer is not available yet.";
            return false;
        }

        var cellIndex = checked(gridY * width + gridX);
        if (!_memory.TryRead<ushort>(
                gridAddress + (ulong)(cellIndex * sizeof(ushort)),
                out var cell))
        {
            detail = "Could not read the primary client FoW cell under the local hero.";
            return false;
        }

        var visible = (cell & _offsets.FogCurrentVisibleBit) != 0;
        sample = new ClientFogOfWarSample(
            visible,
            cell,
            gridX,
            gridY,
            fogContext,
            gridAddress);

        detail = visible
            ? $"Primary client FoW context marks hero tile ({gridX}, {gridY}) visible; cell=0x{cell:X4}."
            : $"Primary client FoW context marks hero tile ({gridX}, {gridY}) hidden; cell=0x{cell:X4}.";
        return true;
    }

    private bool TryResolvePrimaryContext(
        ulong fogSystem,
        out ulong context)
    {
        context = 0;

        if (!_memory.TryRead<ulong>(
                fogSystem + (ulong)_offsets.FogSystemContextMapSlots,
                out var slots)
            || !_memory.TryRead<int>(
                fogSystem + (ulong)_offsets.FogSystemContextMapCount,
                out var count)
            || !_memory.TryRead<int>(
                fogSystem + (ulong)_offsets.FogSystemContextMapCapacity,
                out var capacity)
            || !IsPlausiblePointer(slots)
            || count <= 0
            || capacity <= 0
            || capacity > MaximumContextCapacity)
        {
            return false;
        }

        for (var index = 0; index < capacity; index++)
        {
            var entry = slots + (ulong)(index * _offsets.FogSystemContextEntryStride);
            if (!_memory.TryRead<int>(
                    entry + (ulong)_offsets.FogSystemContextEntryKey,
                    out var key)
                || key != 0
                || !_memory.TryRead<ulong>(
                    entry + (ulong)_offsets.FogSystemContextEntryValue,
                    out var candidate)
                || !IsPlausiblePointer(candidate))
            {
                continue;
            }

            if (_memory.TryRead<int>(
                    candidate + (ulong)_offsets.FogLayerCount,
                    out var layerCount)
                && layerCount is > 0 and <= MaximumLayerCount)
            {
                context = candidate;
                return true;
            }
        }

        return false;
    }

    private static bool IsPlausiblePointer(ulong pointer) =>
        pointer != 0 && pointer <= 0x0000_7FFF_FFFF_FFFF;
}
