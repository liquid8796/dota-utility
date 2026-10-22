using DotaUtility.CombatLogAudit;
using Xunit;

namespace DotaUtility.Dota2.Tests;

public sealed class CombatLogCacheReaderTests
{
    private const ulong Hud = 1048576uL;

    private const ulong Nodes = 2097152uL;

    private const ulong Records = 3145728uL;

    private const ulong Vtable = 4194304uL;

    private readonly FakeProcessMemoryAccessor _memory = new();

    public CombatLogCacheReaderTests()
    {
        _memory.WriteBytes(1048640uL, new byte[48]);
        _memory.Write(1048656uL, -1);
    }

    [Fact]
    public void EmptyInitializedCache_IsNotHiddenEvidence()
    {
        var combatLogCacheReader = new CombatLogCacheReader(_memory);
        Assert.True(combatLogCacheReader.TryRead(1048576uL, 4194304uL, out var snapshot, out _));
        Assert.NotNull(snapshot);
        Assert.Empty(snapshot!.Events);
    }

    [Fact]
    public void PresentFlagsAreEventObservations_NotHeroState()
    {
        SeedOne(14337u);
        _memory.Write(3145776uL, 42u);
        _memory.Write(3145808uL, 12.5f);
        _memory.Write(3145812uL, (byte)1);
        Assert.True(new CombatLogCacheReader(_memory).TryRead(1048576uL, 4194304uL, out var snapshot, out _));
        var cachedCombatEvent = Assert.Single(snapshot!.Events);
        Assert.Equal(42u, cachedCombatEvent.TargetNameIndex);
        Assert.Equal(12.5f, cachedCombatEvent.Timestamp);
        Assert.True(cachedCombatEvent.VisibleRadiant);
        Assert.False(cachedCombatEvent.VisibleDire);
        Assert.Equal("Unknown", cachedCombatEvent.CurrentHeroVisibility);
        Assert.False(cachedCombatEvent.CanDriveCurrentOverlay);
    }

    [Fact]
    public void MissingPresenceBitsIgnoreStaleFieldStorage()
    {
        SeedOne(0u);
        _memory.Write(3145776uL, uint.MaxValue);
        _memory.Write(3145808uL, float.NaN);
        _memory.Write(3145812uL, (byte)1);
        _memory.Write(3145813uL, (byte)1);
        Assert.True(new CombatLogCacheReader(_memory).TryRead(1048576uL, 4194304uL, out var snapshot, out _));
        var cachedCombatEvent = Assert.Single(snapshot!.Events);
        Assert.Null(cachedCombatEvent.TargetNameIndex);
        Assert.Null(cachedCombatEvent.Timestamp);
        Assert.Null(cachedCombatEvent.VisibleRadiant);
        Assert.Null(cachedCombatEvent.VisibleDire);
        Assert.False(cachedCombatEvent.CanDriveCurrentOverlay);
    }

    [Fact]
    public void WrongRecordVtableRejected()
    {
        SeedOne(0u);
        _memory.Write(3145728uL, 4194312uL);
        Reject();
    }

    [Fact]
    public void NonfinitePresentTimestampRejected()
    {
        SeedOne(2048u);
        _memory.Write(3145808uL, float.NaN);
        Reject();
    }

    [Fact]
    public void CycleRejected()
    {
        SeedOne(0u);
        _memory.Write(2097152uL, 0);
        Reject();
    }

    [Fact]
    public void DuplicateChildRejected()
    {
        SeedOne(0u);
        _memory.Write(1048640uL, 2);
        _memory.Write(2097152uL, 1);
        _memory.Write(2097156uL, 1);
        SeedNode(1, -1, 0, 0uL);
        Reject();
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(1)]
    public void InvalidRootRejected(int root)
    {
        SeedOne(0u);
        _memory.Write(1048656uL, root);
        Reject();
    }

    [Fact]
    public void ExcessiveAllocationRejected()
    {
        _memory.Write(1048640uL, 4097);
        Reject();
    }

    [Fact]
    public void NegativeAllocationRejected()
    {
        _memory.Write(1048640uL, -1);
        Reject();
    }

    [Fact]
    public void InvalidAllocationFlagsRejected()
    {
        SeedOne(0u);
        _memory.Write(1048644uL, 2147483648u);
        Reject();
    }

    [Fact]
    public void CountLargerThanCapacityRejected()
    {
        SeedOne(0u);
        _memory.Write(2097176uL, 2);
        Reject();
    }

    [Fact]
    public void NegativeRecordCountRejected()
    {
        SeedOne(0u);
        _memory.Write(2097176uL, -1);
        Reject();
    }

    [Fact]
    public void ExcessivePerNodeCountRejected()
    {
        SeedOne(0u);
        _memory.Write(2097176uL, 129);
        _memory.Write(2097192uL, 129);
        Reject();
    }

    [Fact]
    public void ExcessiveNodeTraversalRejected()
    {
        _memory.Write(1048640uL, 129);
        _memory.Write(1048644uL, 1u);
        _memory.Write(1048648uL, 2097152uL);
        _memory.Write(1048656uL, 0);
        for (var i = 0; i < 129; i++)
        {
            SeedNode(i, (i == 128) ? (-1) : (i + 1), 0, 0uL);
        }

        Reject();
    }

    [Fact]
    public void ExcessiveTotalEventCountRejected()
    {
        _memory.Write(1048640uL, 5);
        _memory.Write(1048644uL, 1u);
        _memory.Write(1048648uL, 2097152uL);
        _memory.Write(1048656uL, 0);
        for (var i = 0; i < 5; i++)
        {
            var num = 3145728uL + (ulong)(i * 65536);
            SeedNode(i, (i == 4) ? (-1) : (i + 1), 128, num);
            _memory.WriteBytes(num, new byte[36864]);
            for (var j = 0; j < 128; j++)
            {
                _memory.Write(num + (ulong)(j * 288), 4194304uL);
            }
        }

        Reject();
    }

    [Fact]
    public void UnreadableRecordsRejected()
    {
        SeedOne(0u);
        _memory.Write(2097184uL, 7827456uL);
        Reject();
    }

    [Fact]
    public void PointerOverflowRejected()
    {
        Assert.False(new CombatLogCacheReader(_memory).TryRead(ulong.MaxValue, 4194304uL, out _, out _));
    }

    [Fact]
    public void NullHudRejected()
    {
        Assert.False(new CombatLogCacheReader(_memory).TryRead(0uL, 4194304uL, out _, out _));
    }

    [Fact]
    public void NullExpectedTypeRejected()
    {
        Assert.False(new CombatLogCacheReader(_memory).TryRead(1048576uL, 0uL, out _, out _));
    }

    [Fact]
    public void HeaderMutationDiscardsWholeSample()
    {
        SeedOne(0u);
        var reads = 0;
        _memory.BeforeRead = (address, _) =>
        {
            if (address == 1048640 && ++reads == 2)
            {
                _memory.Write(1048656uL, -1);
            }
        };
        Reject();
    }

    [Fact]
    public void RecordMutationDiscardsWholeSample()
    {
        SeedOne(4096u);
        var reads = 0;
        _memory.BeforeRead = (address, _) =>
        {
            if (address == 3145728 && ++reads == 2)
            {
                _memory.Write(3145812uL, (byte)1);
            }
        };
        Reject();
    }

    private void SeedOne(uint presence)
    {
        _memory.Write(1048640uL, 1);
        _memory.Write(1048644uL, 1u);
        _memory.Write(1048648uL, 2097152uL);
        _memory.Write(1048656uL, 0);
        SeedNode(0, -1, 1, 3145728uL);
        _memory.WriteBytes(3145728uL, new byte[288]);
        _memory.Write(3145728uL, 4194304uL);
        _memory.Write(3145744uL, presence);
    }

    private void SeedNode(int index, int left, int count, ulong records)
    {
        var num = 2097152uL + (ulong)(index * 48);
        _memory.WriteBytes(num, new byte[48]);
        _memory.Write(num, left);
        _memory.Write(num + 4, -1);
        _memory.Write(num + 24, count);
        _memory.Write(num + 32, records);
        _memory.Write(num + 40, count);
    }

    private void Reject()
    {
        Assert.False(new CombatLogCacheReader(_memory).TryRead(1048576uL, 4194304uL, out var snapshot, out var detail));
        Assert.Null(snapshot);
        Assert.False(string.IsNullOrWhiteSpace(detail));
    }
}
