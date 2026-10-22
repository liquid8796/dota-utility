using System.Numerics;
using DotaUtility.Memory;

namespace DotaUtility.Dota2;

public readonly record struct DotaScreenPoint(double X, double Y);

/// <summary>
/// Projects the local hero's world-space feet position into Dota's client viewport.
/// The reader stays external/read-only and resolves the view-projection matrix by signature.
/// </summary>
public sealed class DotaHeroScreenProjector
{
    private const string ClientModuleName = "client.dll";
    private const float MinimumClipW = 0.001f;

    private static readonly BytePattern ViewMatrixPattern =
        BytePattern.Parse("48 8D 0D ?? ?? ?? ?? 48 C1 E0 06");

    private readonly IProcessMemoryAccessor _memory;
    private readonly DotaOffsets _offsets;
    private readonly IDotaBuildCompatibilityGate? _buildCompatibility;
    private readonly object _gate = new();
    private CacheKey? _cachedKey;
    private ulong _cachedViewMatrixAddress;

    public DotaHeroScreenProjector(
        IProcessMemoryAccessor memory,
        DotaOffsets offsets,
        IDotaBuildCompatibilityGate? buildCompatibility = null)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _offsets = offsets ?? throw new ArgumentNullException(nameof(offsets));
        _buildCompatibility = buildCompatibility;
    }

    public bool TryProject(
        ulong heroAddress,
        double viewportWidth,
        double viewportHeight,
        out DotaScreenPoint point)
    {
        point = default;
        if (_buildCompatibility is not null
            && !_buildCompatibility.TryValidate(out _))
        {
            return false;
        }

        if (heroAddress == 0
            || viewportWidth <= 0
            || viewportHeight <= 0
            || !_memory.IsAttached
            || _memory.ProcessId is not int processId)
        {
            return false;
        }

        var attachmentGeneration = (_memory as IProcessAttachmentGeneration)?.AttachmentGeneration;
        if (!_memory.TryRead<ulong>(
                heroAddress + (ulong)_offsets.BaseEntityGameSceneNode,
                out var sceneNode)
            || !IsPlausibleUserPointer(sceneNode)
            || !_memory.TryRead<Vector3>(
                sceneNode + (ulong)_offsets.GameSceneNodeAbsOrigin,
                out var worldPosition)
            || !IsFinite(worldPosition)
            || !TryResolveViewMatrix(processId, out var viewMatrixAddress)
            || !_memory.TryRead<Matrix4x4>(viewMatrixAddress, out var matrix)
            || !IsFinite(matrix)
            || !_memory.IsAttached
            || _memory.ProcessId != processId
            || (_memory as IProcessAttachmentGeneration)?.AttachmentGeneration != attachmentGeneration)
        {
            return false;
        }

        return TryProjectWorldPoint(worldPosition, matrix, viewportWidth, viewportHeight, out point);
    }

    internal static bool TryProjectWorldPoint(
        Vector3 worldPosition,
        Matrix4x4 matrix,
        double viewportWidth,
        double viewportHeight,
        out DotaScreenPoint point)
    {
        point = default;
        if (viewportWidth <= 0 || viewportHeight <= 0 || !IsFinite(worldPosition) || !IsFinite(matrix))
        {
            return false;
        }

        var clipX = (worldPosition.X * matrix.M11)
            + (worldPosition.Y * matrix.M12)
            + (worldPosition.Z * matrix.M13)
            + matrix.M14;
        var clipY = (worldPosition.X * matrix.M21)
            + (worldPosition.Y * matrix.M22)
            + (worldPosition.Z * matrix.M23)
            + matrix.M24;
        var clipW = (worldPosition.X * matrix.M41)
            + (worldPosition.Y * matrix.M42)
            + (worldPosition.Z * matrix.M43)
            + matrix.M44;

        if (!float.IsFinite(clipX)
            || !float.IsFinite(clipY)
            || !float.IsFinite(clipW)
            || MathF.Abs(clipW) < MinimumClipW)
        {
            return false;
        }

        var normalizedX = clipX / clipW;
        var normalizedY = clipY / clipW;
        if (!float.IsFinite(normalizedX)
            || !float.IsFinite(normalizedY)
            || normalizedX is < -1f or > 1f
            || normalizedY is < -1f or > 1f)
        {
            return false;
        }

        point = new DotaScreenPoint(
            (normalizedX + 1d) * 0.5d * viewportWidth,
            (1d - normalizedY) * 0.5d * viewportHeight);
        return true;
    }

    private bool TryResolveViewMatrix(int processId, out ulong address)
    {
        address = 0;
        var attachmentGeneration = (_memory as IProcessAttachmentGeneration)?.AttachmentGeneration;
        if (!_memory.TryGetModule(ClientModuleName, out var clientModule))
        {
            InvalidateViewMatrix();
            return false;
        }

        var cacheKey = new CacheKey(
            processId,
            attachmentGeneration,
            clientModule.BaseAddress,
            clientModule.Size);
        lock (_gate)
        {
            if (attachmentGeneration is not null
                && _cachedKey == cacheKey
                && _cachedViewMatrixAddress != 0
                && (_memory as IProcessAttachmentGeneration)?.AttachmentGeneration == attachmentGeneration)
            {
                address = _cachedViewMatrixAddress;
                return true;
            }
        }

        if (!ModulePatternScanner.TryResolveRipRelativeAddress(
                _memory,
                clientModule,
                ViewMatrixPattern,
                displacementOffset: 3,
                instructionLength: 7,
                out address)
            || !_memory.IsAttached
            || _memory.ProcessId != processId
            || (_memory as IProcessAttachmentGeneration)?.AttachmentGeneration != attachmentGeneration)
        {
            InvalidateViewMatrix();
            address = 0;
            return false;
        }

        lock (_gate)
        {
            _cachedKey = attachmentGeneration is null ? null : cacheKey;
            _cachedViewMatrixAddress = attachmentGeneration is null ? 0 : address;
        }

        return true;
    }

    private void InvalidateViewMatrix()
    {
        lock (_gate)
        {
            _cachedKey = null;
            _cachedViewMatrixAddress = 0;
        }
    }

    private readonly record struct CacheKey(
        int ProcessId,
        long? AttachmentGeneration,
        ulong ModuleBaseAddress,
        int ModuleSize);

    private static bool IsPlausibleUserPointer(ulong pointer) =>
        pointer != 0 && pointer <= 0x0000_7FFF_FFFF_FFFF;

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool IsFinite(Matrix4x4 value) =>
        float.IsFinite(value.M11) && float.IsFinite(value.M12) && float.IsFinite(value.M13) && float.IsFinite(value.M14)
        && float.IsFinite(value.M21) && float.IsFinite(value.M22) && float.IsFinite(value.M23) && float.IsFinite(value.M24)
        && float.IsFinite(value.M31) && float.IsFinite(value.M32) && float.IsFinite(value.M33) && float.IsFinite(value.M34)
        && float.IsFinite(value.M41) && float.IsFinite(value.M42) && float.IsFinite(value.M43) && float.IsFinite(value.M44);
}
