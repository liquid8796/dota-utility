# DotaUtility lifecycle reliability checkpoint — 2026-10-09

This note follows docs/current-build-status-2026-10-08.md. The work is
strictly about monitor lifecycle, UI state invalidation and retry-safe
diagnostic logging. It does **not** approve any new Dota client build, change
native addresses or signatures, modify recipient/visibility interpretation, or
enable unsupported-build reads.

## Changes

- VisibilityMonitor.StopAsync immediately replaces its current snapshot with
  Unknown("Monitor stopped."), clearing prior hero/process metadata. Once the
  previous worker has finished, it publishes that Unknown state to subscribers.
- A new StartAsync waits for an in-flight stop to complete; old and new poll
  workers no longer overlap during a stop/restart transition. A disposed monitor
  rejects attempts to start it again.
- Worker publication checks cancellation while holding the same lifecycle lock
  used by StopAsync, preventing new snapshot publication after stop
  cancellation has been recorded.
- Cancellation of the externally supplied StartAsync token also publishes an
  Unknown("Monitor canceled.") transition, without waiting for a manual stop.
  The normal StopAsync path owns its own final Unknown notification.
- Lifecycle notifications are delivered to each subscriber separately. If one
  UI/logger observer throws, other subscribers still receive the Unknown
  transition; cleanup does not throw solely because of observer failures.
- FileSessionLogger.LogSnapshot now updates its deduplication signature
  **after** successful append. If disk access fails, an identical later
  snapshot is retried rather than silently discarded.

## Regression coverage

Added tests for:

- Stop invalidates a synthetic Visible snapshot and broadcasts Unknown;
- a new start waits for a slow cancellation-ignoring old probe to complete;
- disposing a monitor rejects further starts;
- cancellation from a caller-supplied token invalidates a previous Visible
  sample even without an explicit stop;
- a failed log-file append is retried with identical snapshot data, then
  deduplicated after success.

Full Release solution command:

    dotnet test DotaUtility.slnx -c Release --no-restore --nologo --tl:off --verbosity quiet --logger "console;verbosity=quiet"

Result: **218 tests passed, 0 failed, 0 skipped** (exit code 0).

## Live compatibility check

Read-only .scratch/CurrentContext diagnostic returned:

    snapshot player= heroTeam= hero=0x0 state=Unknown
    detail=Unsupported client.dll build e5166595605c92fadd5382c005b22758d00b6e26982ec57ce82a8c42c7cc619e.
    Layout-dependent memory reads are disabled until this build is audited.
    Current build is not audited for production layouts; skipping stale hard-coded diagnostics.

The compatibility gate remains unchanged: this client is **unsupported**,
no layout-dependent native reads were performed, and the monitor must not infer
Hidden/Visible or display an indicator based on missing evidence.


## Follow-up: final lifecycle and diagnostic hardening

This continuation keeps the previous 218/218 test checkpoint intact for
traceability. Additional changes were made afterward:

- MainViewModel checks callback snapshot reference identity against the
  monitor's current snapshot on the UI thread. Out-of-order queued callbacks
  cannot restore a stale Visible result after a newer Unknown.
- A recoverable IOException or UnauthorizedAccessException from snapshot
  logging no longer blocks dashboard updates.
- VisibilityMonitor dispatches each regular snapshot to subscribers
  individually, then broadcasts a fail-closed Unknown if any subscriber
  fails. A faulty logger cannot prevent the other observers from receiving
  the eventual Unknown transition.
- VisibilityMonitor establishes its stop barrier before cancellation and
  invokes cancellation callbacks outside its lifecycle lock. This prevents
  re-entrant cancellation callbacks on another thread from deadlocking
  while trying to acquire the monitor lock.
- DotaBuildCompatibilityGate rechecks the client.dll backing file length
  and modification time after hashing. If they changed during the check,
  the result is rejected rather than cached.
- FileSessionLogger replaces CR, LF, ANSI escape characters and other
  control characters in diagnostic strings to prevent log record injection.

New regression tests cover out-of-order UI callbacks, recoverable log write
errors, observer isolation, cancellation-callback reentrancy, backing-file
mutation during PE verification, and log control-character handling.

Final verification for this continuation:

    dotnet test DotaUtility.slnx -c Release --no-restore --nologo --tl:off --verbosity quiet --logger "console;verbosity=quiet"

**224/224 tests passed**, none failed or skipped, exit code 0. The
Application test suite passed **5/5 repeated runs (18/18 tests each)**.
An additional explicit Release build succeeded with **0 warnings and
0 errors**, exit code 0.

The read-only live diagnostic again returned:

    snapshot player= heroTeam= hero=0x0 state=Unknown
    detail=Unsupported client.dll build e5166595605c92fadd5382c005b22758d00b6e26982ec57ce82a8c42c7cc619e.
    Layout-dependent memory reads are disabled until this build is audited.
    Current build is not audited for production layouts; skipping stale hard-coded diagnostics.

## Final scope and limitations

Lifecycle, UI state consistency, diagnostic log handling and build-gate
file metadata checks have been hardened and the new regression coverage
passes. This does **not** verify the current client's memory layout,
native recipient/visibility semantics, or normal multiplayer suitability.
Comparing the mapped PE identity with the disk PE identity and checking
file metadata before/after hashing does not prove every byte in process
memory is identical to the file. The unknown client hash stays rejected.
No additional offsets, signatures, allowlist hashes, recipient filters
or enemy-visibility detection logic were added.
