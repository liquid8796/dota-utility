using System.Buffers.Binary;

namespace DotaUtility.Memory;

public static class ModulePatternScanner
{
    private const int ScanChunkSize = 64 * 1024;

    public static ulong ResolveRipRelativeAddress(
        ReadOnlySpan<byte> moduleImage,
        ulong moduleBase,
        BytePattern pattern,
        int displacementOffset,
        int instructionLength)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ValidateRipArguments(pattern, displacementOffset, instructionLength);

        var matchOffset = pattern.FindFirst(moduleImage);
        if (matchOffset < 0)
        {
            throw new InvalidOperationException("Pattern was not found in the supplied module image.");
        }

        return ResolveRipRelativeAddressAtMatch(
            moduleImage,
            moduleBase,
            matchOffset,
            displacementOffset,
            instructionLength);
    }

    public static bool TryResolveRipRelativeAddress(
        IProcessMemoryAccessor memory,
        ProcessModuleSnapshot module,
        BytePattern pattern,
        int displacementOffset,
        int instructionLength,
        out ulong address)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(pattern);
        ValidateRipArguments(pattern, displacementOffset, instructionLength);

        address = 0;
        if (module.Size <= 0 || module.BaseAddress > (ulong)long.MaxValue)
        {
            return false;
        }

        var overlap = Math.Max(pattern.Length - 1, 0);
        for (var offset = 0; offset < module.Size; offset += ScanChunkSize)
        {
            var remaining = module.Size - offset;
            var length = Math.Min(ScanChunkSize + overlap, remaining);
            var unsignedOffset = (ulong)offset;
            if (module.BaseAddress > (ulong)long.MaxValue - unsignedOffset)
            {
                return false;
            }

            var chunkBase = module.BaseAddress + unsignedOffset;
            if (!memory.TryReadBytes(chunkBase, length, out var chunk))
            {
                continue;
            }

            var localMatch = pattern.FindFirst(chunk);
            if (localMatch < 0)
            {
                continue;
            }

            try
            {
                address = ResolveRipRelativeAddressAtMatch(
                    chunk,
                    chunkBase,
                    localMatch,
                    displacementOffset,
                    instructionLength);
                return true;
            }
            catch (OverflowException)
            {
                address = 0;
                return false;
            }
        }

        return false;
    }

    private static ulong ResolveRipRelativeAddressAtMatch(
        ReadOnlySpan<byte> image,
        ulong imageBase,
        int matchOffset,
        int displacementOffset,
        int instructionLength)
    {
        var displacementStart = checked(matchOffset + displacementOffset);
        if (displacementStart < 0 || displacementStart + sizeof(int) > image.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(displacementOffset));
        }

        var displacement = BinaryPrimitives.ReadInt32LittleEndian(image.Slice(displacementStart, sizeof(int)));
        var instructionEnd = checked((long)imageBase + matchOffset + instructionLength);
        var target = checked(instructionEnd + displacement);
        if (target < 0)
        {
            throw new OverflowException("Resolved RIP-relative address was negative.");
        }

        return (ulong)target;
    }

    private static void ValidateRipArguments(BytePattern pattern, int displacementOffset, int instructionLength)
    {
        if (displacementOffset < 0 || displacementOffset + sizeof(int) > pattern.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(displacementOffset));
        }

        if (instructionLength <= displacementOffset + sizeof(int) - 1)
        {
            throw new ArgumentOutOfRangeException(nameof(instructionLength));
        }
    }
}
