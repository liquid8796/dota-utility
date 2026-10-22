using DotaUtility.Memory;

namespace DotaUtility.Memory.Tests;

public sealed class BytePatternTests
{
    [Fact]
    public void FindFirst_MatchesWildcardsWithoutMatchingWrongPrefix()
    {
        var pattern = BytePattern.Parse("48 89 0D ?? ?? ?? ?? E9 ?? ?? ?? ?? CC");
        byte[] image =
        [
            0x90,
            0x48, 0x89, 0x0C, 0x11, 0x22, 0x33, 0x44, 0xE9, 0x01, 0x02, 0x03, 0x04, 0xCC,
            0x90,
            0x48, 0x89, 0x0D, 0xAA, 0xBB, 0xCC, 0xDD, 0xE9, 0x10, 0x20, 0x30, 0x40, 0xCC,
        ];

        var offset = pattern.FindFirst(image);

        Assert.Equal(15, offset);
    }

    [Fact]
    public void FindFirst_ReturnsMinusOneWhenPatternDoesNotExist()
    {
        var pattern = BytePattern.Parse("48 89 0D ?? ?? ?? ??");

        var offset = pattern.FindFirst([0x48, 0x89, 0x0C, 0, 0, 0, 0]);

        Assert.Equal(-1, offset);
    }

    [Fact]
    public void ResolveRipRelativeAddress_UsesSignedDisplacementFromInstructionEnd()
    {
        const ulong moduleBase = 0x0000_7FF6_1000_0000;
        var pattern = BytePattern.Parse("48 89 0D ?? ?? ?? ?? E9");
        var image = new byte[64];
        const int matchOffset = 8;
        image[matchOffset] = 0x48;
        image[matchOffset + 1] = 0x89;
        image[matchOffset + 2] = 0x0D;
        BitConverter.GetBytes(0x1234).CopyTo(image, matchOffset + 3);
        image[matchOffset + 7] = 0xE9;

        var address = ModulePatternScanner.ResolveRipRelativeAddress(
            image,
            moduleBase,
            pattern,
            displacementOffset: 3,
            instructionLength: 7);

        Assert.Equal(moduleBase + matchOffset + 7UL + 0x1234UL, address);
    }
}
