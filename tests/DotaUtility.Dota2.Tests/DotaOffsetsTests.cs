using DotaUtility.Dota2;

namespace DotaUtility.Dota2.Tests;

public sealed class DotaOffsetsTests
{
    [Fact]
    public void Current_UsesLiveEntityIdentityStride()
    {
        Assert.Equal(0x70, DotaOffsets.Current.EntityListEntryStride);
        Assert.Equal(0x10, DotaOffsets.Current.EntityIdentityHandle);
        Assert.Equal(0x778, DotaOffsets.Current.PlayerControllerIsLocalPlayer);
        Assert.Equal(0x6D8, DotaOffsets.Current.PlayerControllerIsHltv);
        Assert.Equal(0x910, DotaOffsets.Current.PlayerControllerPlayerId);
        Assert.Equal(0x914, DotaOffsets.Current.PlayerControllerAssignedHero);
        Assert.Equal(0x3E7, DotaOffsets.Current.BaseEntityTeamNumber);
        Assert.Equal(0x330, DotaOffsets.Current.BaseEntityGameSceneNode);
        Assert.Equal(0x788, DotaOffsets.Current.BaseModelEntityTeamVisibilityBitmask);
        Assert.Equal(0xD8, DotaOffsets.Current.GameSceneNodeAbsOrigin);
        Assert.Equal(0xE58, DotaOffsets.Current.DataNonSpectatorNpcVisibleState);
        Assert.Equal(0x58, DotaOffsets.Current.FogSystemContextMapSlots);
        Assert.Equal(0x278, DotaOffsets.Current.FogLayerArrayBase);
        Assert.Equal(0x4000, DotaOffsets.Current.FogCurrentVisibleBit);
    }
}
