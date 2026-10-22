using DotaUtility.Domain;

namespace DotaUtility.Application;

public sealed class VisibilityMonitor : IAsyncDisposable
{
    private readonly IVisibilityProbe _probe;
    private readonly TimeSpan _interval;
    private readonly object _lifecycleGate = new();
    private VisibilitySnapshot _current = VisibilitySnapshot.Unknown("Monitor has not started.");
    private CancellationTokenSource? _cancellation;
    private Task? _worker;
    private Task? _stopping;
    private bool _disposed;

    public VisibilityMonitor(IVisibilityProbe probe, TimeSpan interval)
    {
        ArgumentNullException.ThrowIfNull(probe);
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), interval, "Polling interval must be positive.");
        }

        _probe = probe;
        _interval = interval;
    }

    public event EventHandler<VisibilitySnapshot>? SnapshotChanged;

    public VisibilitySnapshot Current => Volatile.Read(ref _current);

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task? stopping;
            lock (_lifecycleGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                stopping = _stopping;
                if (stopping is null || stopping.IsCompleted)
                {
                    if (_worker is { IsCompleted: false })
                    {
                        return;
                    }

                    _cancellation?.Dispose();
                    var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    _cancellation = cancellation;
                    // Keep the worker tied to this source, not the mutable field.
                    _worker = Task.Run(() => RunLoopAsync(cancellation.Token), CancellationToken.None);
                    return;
                }
            }

            // Never overlap a new probe worker with a still-stopping one.
            await stopping.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    public Task StopAsync()
    {
        CancellationTokenSource cancellation;
        Task? worker;
        TaskCompletionSource completion;
        VisibilitySnapshot stopped;
        lock (_lifecycleGate)
        {
            if (_stopping is { IsCompleted: false } stopping)
            {
                return stopping;
            }

            if (_cancellation is null)
            {
                return Task.CompletedTask;
            }

            cancellation = _cancellation;
            worker = _worker;
            _cancellation = null;
            _worker = null;

            // Invalidate immediately and establish a stop barrier before invoking
            // cancellation callbacks, which may reenter this monitor.
            stopped = VisibilitySnapshot.Unknown("Monitor stopped.");
            Volatile.Write(ref _current, stopped);
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _stopping = completion.Task;
        }

        // Never invoke user-owned cancellation callbacks while holding
        // _lifecycleGate. A callback may call StopAsync on another thread.
        _ = CompleteStopAsync(cancellation, worker, stopped, completion);
        return completion.Task;
    }

    public async ValueTask DisposeAsync()
    {
        lock (_lifecycleGate)
        {
            _disposed = true;
        }

        await StopAsync().ConfigureAwait(false);
    }

    private async Task CompleteStopAsync(
        CancellationTokenSource cancellation,
        Task? worker,
        VisibilitySnapshot stopped,
        TaskCompletionSource completion)
    {
        Exception? stopFailure = null;
        try
        {
            try
            {
                cancellation.Cancel();
            }
            catch (AggregateException)
            {
                // Cancellation callbacks are external. Still wait for the worker
                // and invalidate all subscribers instead of abandoning cleanup.
            }

            if (worker is not null)
            {
                await worker.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            stopFailure = exception;
        }
        finally
        {
            cancellation.Dispose();
            Volatile.Write(ref _current, stopped);
            NotifyLifecycleTransition(stopped);

            if (stopFailure is not null)
            {
                completion.TrySetException(stopFailure);
            }
            else
            {
                completion.TrySetResult();
            }
        }
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                VisibilitySnapshot snapshot;
                try
                {
                    snapshot = await _probe.ProbeAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    snapshot = VisibilitySnapshot.Unknown($"Probe failed: {exception.Message}");
                }

                // Some IVisibilityProbe implementations may return a result after
                // cancellation instead of throwing. Never publish that late sample.
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                lock (_lifecycleGate)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    Volatile.Write(ref _current, snapshot);
                }

                // Deliver to every subscriber, even if one observer throws.
                // Then publish and broadcast Unknown to converge every view.
                Exception? observerFailure = null;
                var subscribers = SnapshotChanged?.GetInvocationList();
                if (subscribers is not null)
                {
                    foreach (EventHandler<VisibilitySnapshot> subscriber in subscribers)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            break;
                        }

                        try
                        {
                            subscriber(this, snapshot);
                        }
                        catch (Exception exception)
                        {
                            observerFailure ??= exception;
                        }
                    }
                }

                if (observerFailure is not null)
                {
                    VisibilitySnapshot? failure = null;
                    lock (_lifecycleGate)
                    {
                        // A concurrent stop/cancel takes priority over this
                        // sample. Never replace a newer Unknown transition.
                        if (!cancellationToken.IsCancellationRequested
                            && ReferenceEquals(_current, snapshot))
                        {
                            failure = VisibilitySnapshot.Unknown(
                                $"Snapshot observer failed: {observerFailure.Message}");
                            Volatile.Write(ref _current, failure);
                        }
                    }

                    if (failure is not null)
                    {
                        NotifyLifecycleTransition(failure);
                    }
                }

                try
                {
                    await Task.Delay(_interval, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        finally
        {
            VisibilitySnapshot? canceled = null;
            lock (_lifecycleGate)
            {
                // StopAsync owns its own cancellation/notification. Only an
                // external token cancellation with this source still active
                // should end the loop by publishing a fresh Unknown state.
                if (cancellationToken.IsCancellationRequested
                    && _cancellation is not null
                    && _cancellation.Token == cancellationToken)
                {
                    canceled = VisibilitySnapshot.Unknown("Monitor canceled.");
                    Volatile.Write(ref _current, canceled);
                }
            }

            if (canceled is not null)
            {
                NotifyLifecycleTransition(canceled);
            }
        }
    }

    private void NotifyLifecycleTransition(VisibilitySnapshot snapshot)
    {
        // Even if one observer fails, the others must receive the Unknown
        // transition so stale UI can be dismissed.
        var subscribers = SnapshotChanged?.GetInvocationList();
        if (subscribers is null)
        {
            return;
        }

        foreach (EventHandler<VisibilitySnapshot> subscriber in subscribers)
        {
            try
            {
                subscriber(this, snapshot);
            }
            catch (Exception)
            {
                // Cleanup/cancellation must not fail because of a UI observer.
            }
        }
    }
}
