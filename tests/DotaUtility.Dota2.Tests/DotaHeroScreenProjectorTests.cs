using System.Numerics;
using DotaUtility.Memory;

namespace DotaUtility.Dota2.Tests;

public sealed class DotaHeroScreenProjectorTests
{
    [Fact]
    public void TryProject_ProjectsHeroFeetIntoViewport()
    {
        var memory = new FakeProcessMemoryAccessor();
        var offsets = DotaOffsets.Current;
        const ulong heroAddress = 0x5000;
        const ulong sceneNode = 0x6000;
        var projector = SeedProjector(
            memory,
            offsets,
            heroAddress,
            sceneNode,
            new Vector3(0.25f, -0.5f, 0f),
            Matrix4x4.Identity);

        var projected = projector.TryProject(heroAddress, 1920, 1080, out var point);

        Assert.True(projected);
        Assert.Equal(1200d, point.X, precision: 3);
        Assert.Equal(810d, point.Y, precision: 3);
    }

    [Fact]
    public void TryProject_ReturnsFalseWhenHeroIsOutsideViewport()
    {
        var memory = new FakeProcessMemoryAccessor();
        var offsets = DotaOffsets.Current;
        const ulong heroAddress = 0x5000;
        const ulong sceneNode = 0x6000;
        var projector = SeedProjector(
            memory,
            offsets,
            heroAddress,
            sceneNode,
            new Vector3(2f, 0f, 0f),
            Matrix4x4.Identity);

        var projected = projector.TryProject(heroAddress, 1920, 1080, out _);

        Assert.False(projected);
    }

    private static DotaHeroScreenProjector SeedProjector(
        FakeProcessMemoryAccessor memory,
        DotaOffsets offsets,
        ulong heroAddress,
        ulong sceneNode,
        Vector3 position,
        Matrix4x4 matrix)
    {
        const ulong clientBase = 0x1000;
        const ulong matrixAddress = 0x3000;

        memory.TryAttach(1337);
        var signature = BuildViewMatrixInstruction(clientBase, matrixAddress);
        memory.AddModule(new ProcessModuleSnapshot(
            "client.dll",
            "client.dll",
            clientBase,
            signature.Length));
        memory.WriteBytes(clientBase, signature);
        memory.Write(heroAddress + (ulong)offsets.BaseEntityGameSceneNode, sceneNode);
        memory.Write(sceneNode + (ulong)offsets.GameSceneNodeAbsOrigin, position);
        memory.Write(matrixAddress, matrix);

        return new DotaHeroScreenProjector(memory, offsets);
    }

    private static byte[] BuildViewMatrixInstruction(ulong instructionAddress, ulong targetAddress)
    {
        var displacement = checked((int)((long)targetAddress - ((long)instructionAddress + 7)));
        var displacementBytes = BitConverter.GetBytes(displacement);
        return
        [
            0x48, 0x8D, 0x0D,
            displacementBytes[0], displacementBytes[1], displacementBytes[2], displacementBytes[3],
            0x48, 0xC1, 0xE0, 0x06,
        ];
    }
}
