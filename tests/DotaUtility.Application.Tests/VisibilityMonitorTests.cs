using DotaUtility.Application;
using DotaUtility.Domain;

namespace DotaUtility.Application.Tests;

public sealed class VisibilityMonitorTests
{
    [Fact]
    public async Task StartAsync_PublishesProbeSnapshotImmediately()
    {
        var expected = new VisibilitySnapshot(
            VisibilityState.Visible,
            DateTimeOffset.UtcNow,
            true,
            4242,
            2,
            3,
            1 << 3,
            "Enemy team currently has vision.");
        var probe = new SequenceProbe(expected);
        await using var monitor = new VisibilityMonitor(probe, TimeSpan.FromSeconds(30));
        var observed = new TaskCompletionSource<VisibilitySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.SnapshotChanged += (_, snapshot) => observed.TrySetResult(snapshot);

        await monitor.StartAsync();
        var actual = await observed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(expected, actual);
        Assert.Equal(expected, monitor.Current);
    }

    [Fact]
    public async Task ProbeException_IsPublishedAsUnknownInsteadOfEscapingBackgroundLoop()
    {
        var probe = new ThrowingProbe(new InvalidOperationException("schema moved"));
        await using var monitor = new VisibilityMonitor(probe, TimeSpan.FromSeconds(30));
        var observed = new TaskCompletionSource<VisibilitySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.SnapshotChanged += (_, snapshot) => observed.TrySetResult(snapshot);

        await monitor.StartAsync();
        var actual = await observed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(VisibilityState.Unknown, actual.State);
        Assert.Contains("schema moved", actual.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.False(actual.IsAttached);
    }

    [Fact]
    public async Task StopAsync_StopsAdditionalPolling()
    {
        var probe = new CountingProbe();
        await using var monitor = new VisibilityMonitor(probe, TimeSpan.FromMilliseconds(15));

        await monitor.StartAsync();
        await probe.ReachedTwoCalls.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await monitor.StopAsync();
        var callsAfterStop = probe.CallCount;
        await Task.Delay(80);

        Assert.Equal(callsAfterStop, probe.CallCount);
        Assert.Equal(VisibilityState.Unknown, monitor.Current.State);
    }

    [Fact]
    public async Task StopAsync_InvalidatesVisibleSnapshotAndBroadcastsUnknown()
    {
        var visible = new VisibilitySnapshot(
            VisibilityState.Visible,
            DateTimeOffset.UtcNow,
            true,
            4242,
            2,
            3,
            1 << 3,
            "Visible.");
        await using var monitor = new VisibilityMonitor(new SequenceProbe(visible), TimeSpan.FromSeconds(30));
        var firstPublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.SnapshotChanged += (_, s) =>
        {
            if (s.State == VisibilityState.Visible)
            {
                firstPublished.TrySetResult();
            }
        };

        await monitor.StartAsync();
        await firstPublished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await monitor.StopAsync();

        Assert.Equal(VisibilityState.Unknown, monitor.Current.State);
        Assert.Contains("stopped", monitor.Current.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DisposeAsync_RejectsSubsequentStartAsync()
    {
        var monitor = new VisibilityMonitor(new SequenceProbe(VisibilitySnapshot.Unknown("idle")), TimeSpan.FromSeconds(30));
        await monitor.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => monitor.StartAsync());
    }

    [Fact]
    public async Task SubscriberException_DoesNotPreventOtherSubscribersAndConvergesToUnknown()
    {
        var visible = new VisibilitySnapshot(
            VisibilityState.Visible,
            DateTimeOffset.UtcNow,
            true,
            4242,
            2,
            3,
            1 << 3,
            "Visible.");
        await using var monitor = new VisibilityMonitor(new SequenceProbe(visible), TimeSpan.FromSeconds(30));
        var convergedToUnknown = new TaskCompletionSource<VisibilitySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.SnapshotChanged += (_, s) =>
        {
            if (s.State == VisibilityState.Visible)
            {
                throw new InvalidOperationException("observer crash");
            }
        };
        monitor.SnapshotChanged += (_, s) =>
        {
            if (s.State == VisibilityState.Unknown && s.Detail.Contains("observer failed", StringComparison.OrdinalIgnoreCase))
            {
                convergedToUnknown.TrySetResult(s);
            }
        };

        await monitor.StartAsync();
        var fallback = await convergedToUnknown.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(VisibilityState.Unknown, fallback.State);
        Assert.Equal(VisibilityState.Unknown, monitor.Current.State);
    }

    private sealed class SequenceProbe(params VisibilitySnapshot[] snapshots) : IVisibilityProbe
    {
        private int _index;

        public Task<VisibilitySnapshot> ProbeAsync(CancellationToken cancellationToken)
        {
            var index = Math.Min(Interlocked.Increment(ref _index) - 1, snapshots.Length - 1);
            return Task.FromResult(snapshots[index]);
        }
    }

    private sealed class ThrowingProbe(Exception error) : IVisibilityProbe
    {
        public Task<VisibilitySnapshot> ProbeAsync(CancellationToken cancellationToken) => Task.FromException<VisibilitySnapshot>(error);
    }

    private sealed class CountingProbe : IVisibilityProbe
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);
        public TaskCompletionSource ReachedTwoCalls { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<VisibilitySnapshot> ProbeAsync(CancellationToken cancellationToken)
        {
            var count = Interlocked.Increment(ref _callCount);
            if (count >= 2)
            {
                ReachedTwoCalls.TrySetResult();
            }

            return Task.FromResult(VisibilitySnapshot.Unknown("Waiting for Dota 2."));
        }
    }
}
