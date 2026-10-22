# Current Dota build checkpoint — 2026-10-08

## Scope

This checkpoint records the state observed after resuming the project against the
locally installed Dota 2 build on 2026-10-08. It is intentionally diagnostic.
It does not promote any new visibility source, offset, recipient route, or
enemy-sight claim.

The production memory accessor remains external and read-only. It requests
process query rights plus `PROCESS_VM_READ` and uses `ReadProcessMemory`; it
does not request process write rights.

## Installed build identity

The loaded Dota process was `dota2.exe` PID 21880, x64, medium integrity, from:

`D:\Program Files (x86)\Steam\steamapps\common\dota 2 beta\game\bin\win64\dota2.exe`

The loaded `client.dll` and `server.dll` came from the same installation's
`game\dota\bin\win64` directory.

| Module | SHA-256 | PE timestamp (UTC) | Last write (UTC) |
| --- | --- | --- | --- |
| client.dll | `9106614aeafaf952ce5021a0e66441a74fd2e4073942861c0036d4a5d39c3e9e` | 2026-10-06 22:54:28 | 2026-10-07 16:38:28 |
| server.dll | `ea1b9dc4eb759fc66d05b5cb247f045af9b4237d1a8b532049bee3f0a1f52b4d` | 2026-10-07 00:45:47 | 2026-10-07 16:38:28 |

These hashes differ from all September build-pinned research profiles currently
committed in the audit scripts and evidence.

## Audit behavior

The existing build-pinned local-controller, visibility-registration, and reveal-
publication audits correctly rejected the current binaries as unsupported. This
is expected fail-closed behavior; old RVAs must not be silently reinterpreted as
current evidence.

The full Release solution test run completed successfully after resuming the
project. After the fail-closed production hardening described below, the full
Release suite passes 213/213 tests.

## Live production-path observation

Before the strict build gate was added, the existing `.scratch/CurrentContext`
diagnostic was run against the current process. Its first step used the
production `DotaAddressResolver` and `DotaUtilityProbe`.

Observed result:

```text
snapshot player= heroTeam= hero=0x0 state=Unknown
detail=The native controller is not a confirmed local, non-HLTV controller.
```

The same diagnostic was still able to read a non-empty team-data handle for team
3, which shows that at least part of the production signature-resolution path
still resolves on this build. This does **not** establish that all signatures,
object layouts, local-controller semantics, or visibility fields remain valid.

The hard-coded vtable addresses later in `.scratch/CurrentContext/Program.cs`
belong to an older build and must not be treated as current evidence.

After adding the strict production build gate, the same live diagnostic now
returns earlier:

```text
snapshot player= heroTeam= hero=0x0 state=Unknown
detail=Unsupported client.dll build 9106614aeafaf952ce5021a0e66441a74fd2e4073942861c0036d4a5d39c3e9e.
Layout-dependent memory reads are disabled until this build is audited.
Current build is not audited for production layouts; skipping stale hard-coded diagnostics.
```

This was the intended behavior for the build observed during the first
checkpoint.

A second live diagnostic after attachment-generation hardening on 2026-10-08
returned the following, still fail-closed:

    snapshot player= heroTeam= hero=0x0 state=Unknown
    detail=Unsupported client.dll build e5166595605c92fadd5382c005b22758d00b6e26982ec57ce82a8c42c7cc619e.
    Layout-dependent memory reads are disabled until this build is audited.
    Current build is not audited for production layouts; skipping stale hard-coded diagnostics.

The later client backing-file SHA-256 differs from the earlier
9106614a... observation. The diagnostic does not establish why the binary
changed; it does establish that the unsupported-build gate still blocks
layout-dependent reads.


## Interpretation

Before strict build gating, the observed failure was upstream of enemy-team
visibility interpretation: local-controller acquisition failed its local/non-
HLTV validation. Plausible causes included an inactive/loading context, changed
controller-array semantics, or stale controller field offsets. That observation
alone did not distinguish those cases.

Production no longer reaches that ambiguity on this build. The current
`9106614a...` client is rejected by build identity before layout-dependent
address resolution or entity reads.

Until the current build's local-controller identity and relevant layouts are
independently revalidated, production should continue to return `Unknown`.
Missing or unvalidated data must not be converted to `Hidden`, and own-team
data, terrain FoW, legacy hero masks, replay callbacks, combat-log flags, or
modifier-name heuristics remain invalid substitutes.

## Source cleanup in this checkpoint

Current source/UI wording was corrected from misleading `Read/Write` labels to
`read-only` / `read/query`, matching the actual `Win32ProcessMemoryAccessor`
implementation. No detector semantics, offsets, signatures, network behavior,
or overlay decision rules were changed by that cleanup.

## Fail-closed production build gate

`DotaBuildCompatibilityGate` hashes the loaded `client.dll` backing file once
per process/module/file identity and blocks layout-dependent production reads
for unknown hashes. Before trusting that file hash, the gate parses the PE
identity from both the image currently mapped in the Dota process and the
backing file on disk. It requires AMD64 PE32+, equal PE timestamps, equal
`SizeOfImage`, and agreement with the enumerated module size. This prevents a
Steam update from replacing the on-disk DLL while an older image remains mapped
in a running process.

The cache key includes process ID, module base, module size, file path, file
length, and last-write time, so the 150 ms monitor loop does not rehash the
module on every sample. Malformed or oversized PE image sizes and short file
reads fail closed rather than throwing.

The production allowlist is deliberately narrower than the historical research
profiles. It currently contains only client hash
`33f6c667b628c9a63106b6d9148f35eaa3a4ec665c43dfbc6b8440d320dfaccf`,
the build on which the local-controller identity and replicated team-data layout
were audited together. A research script accepting a later build is not
automatically sufficient to promote that build into production.

The App passes the same strict gate to both `DotaUtilityProbe` and
`DotaHeroScreenProjector`. Therefore an unsupported build cannot proceed to
the address resolver, controller/entity layout reads, hero-position reads, or
view-matrix projection. The optional compatibility relay cannot bypass this to
draw the ring because an unsupported inner probe has no validated hero address.

Regression coverage now includes:

- unknown client hashes are rejected;
- a mapped image/backing-file mismatch is rejected;
- malformed oversized PE image metadata is rejected without throwing;
- a rejected build never invokes the address resolver;
- a rejected build prevents the screen projector from performing any memory
  reads.

## Production compatibility boundary

Production now constructs `DotaAddressResolver` in client-only mode. The App
does not require or signature-scan `engine2.dll`, and `DotaUtilityProbe` no
longer consumes `CNetworkGameClient` or its local network slot. That slot was
only a secondary consistency guard and was not the source of Dota PlayerID or
local-controller selection.

The stronger identity checks remain: the entity-system pointer and complete
native local controller/assigned-hero identity are reread after the visibility
sample, and any change invalidates the sample. This narrows the production
compatibility boundary to the audited client-side layouts instead of silently
depending on an unpinned `engine2.dll` build.

The resolver's default mode still resolves `engine2.dll` for historical
research/audit callers. This preserves those tools' prior evidence behavior
without making that dependency part of the production App path.

## Process lifecycle hardening

`Win32ProcessMemoryAccessor` now opens the target with `SYNCHRONIZE` in
addition to read/query rights and checks the original process handle with a
zero-timeout wait. When the attached process exits, the accessor disposes the
stale handle and clears the PID. A later PID reuse therefore cannot inherit the
old process/build cache merely because the numeric process ID is reused.

`TryGetModule` now validates the original attached process handle before and
after module enumeration. It only publishes a module snapshot when the same
original process object is still alive and the attached PID is unchanged. If
the process exits during enumeration, the accessor clears its state and rejects
the candidate snapshot.

## Resolver and scanner cache hardening

`DotaAddressResolver` no longer caches absolute addresses by PID alone. Its
cache identity includes the client module base and size, plus the engine module
base and size when the historical full resolver mode is enabled. A Dota restart,
ASLR change, or same-PID module remap therefore forces signatures to be resolved
again instead of reusing absolute addresses from the previous image.

`ModulePatternScanner.TryResolveRipRelativeAddress` now rejects impossible
module metadata such as a non-positive image size or a base address outside the
supported signed user-address domain before reading memory. RIP-relative
arithmetic overflow also fails closed with a false result rather than escaping
as an exception.

Regression coverage includes same-PID/module-base changes, process exit during
module access, impossible module-base metadata, build/file mismatch, malformed
PE metadata, and unsupported-build short-circuiting.

## Attachment-generation consistency (2026-10-08 continuation)

The Win32 memory accessor implements IProcessAttachmentGeneration, a
monotonic counter that changes on every successful attach or handle clearing.
It distinguishes different process objects even when Windows reuses the same
PID and the modules have the same base addresses and sizes.

TryGetModule now captures this generation before enumerating modules and
checks it again while holding the accessor lock before publishing a snapshot.
A concurrent reattach invalidates the old result without closing the new
handle.

DotaBuildCompatibilityGate, DotaAddressResolver, and
DotaHeroScreenProjector now include this generation in their process-bound
caches. All three reject results when the attachment changes during the
current operation. A custom accessor without generation support is processed
without reuse of a process-bound cache.

Regression tests cover same-PID reattachment with unchanged module metadata
and mid-read reattachment during PE validation, signature resolution, and
local-hero projection. No offsets, signatures, visibility-source semantics,
enemy-sight rules, or unsupported-build allowlist entries were changed.

## Whole-snapshot lifecycle and UI fail-closed behavior

The production DotaUtilityProbe captures process ID and attachment generation
at the start of each sample and checks them after build gating, after address
resolution, and after all identity rereads. A detach/reattach during the sample,
including a same-PID reattach with identical controller and hero data, yields
Unknown without publishing the previous hero address. Tests simulate both a
same-PID process-generation change and a switch to a different PID while the
sample is being read.

The overlay controller already hides its indicator when the snapshot is not
Visible. No drawing or visibility-source code was changed.

VisibilityMonitor now drops a completed probe result when its polling token
has already been canceled, including probes that ignore cancellation. Failures
thrown by SnapshotChanged observers are caught; the monitor publishes Unknown
rather than terminating its worker with a stale Visible/Hidden value. In the
UI, MainViewModel ignores synchronization-context callbacks dispatched after
DisposeAsync, avoiding stale post-disposal state and logging.

New regression coverage checks the mid-sample generation transitions, late
probe completion after cancellation, event-subscriber exceptions, and UI
callbacks queued before disposal.

VisibilityMonitor.StartAsync also now captures the CancellationTokenSource
for each worker in a local variable, rather than dereferencing the mutable
_cancellation field inside Task.Run. This prevents a fast StopAsync from
clearing the source before a newly scheduled worker has started. A repeated
immediate start/stop regression covers the lifecycle boundary.

## Safe next engineering gates

1. Re-establish current-build compatibility before trusting any layout-dependent
   result.
2. Preserve strict rejection in historical audit tools instead of weakening old
   evidence gates.
3. Keep live acquisition fail-closed and retain full controller/hero identity
   consistency checks.
4. Only promote a visibility source after freshness, entity attribution, normal
   client availability, and independently labelled semantics are all validated.
