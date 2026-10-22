# Dota Utility Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a production-quality .NET 10 WPF desktop tool that reads Dota 2 memory Read/Write and reports whether the local hero is visible to the enemy team.

**Architecture:** Use five focused projects: Domain models, Application polling, Win32 Memory infrastructure, Dota 2 Source 2 adapter, and WPF App. The Dota adapter resolves global addresses from signatures and keeps schema offsets in one immutable profile; application/UI consume only `IVisibilityProbe` snapshots.

**Tech Stack:** C# 14, .NET 10, WPF, Win32 `ReadProcessMemory`, xUnit, Visual Studio 2026 solution format.

**Spec:** `docs/design.md`

## Global Constraints

- Workspace root is `D:\Project\lobby\DotaUtility`.
- Windows 10/11 only; target `net10.0-windows` for WPF and `net10.0`/`net10.0-windows` where appropriate.
- Process access is Read/Write (`PROCESS_VM_READ | PROCESS_QUERY_LIMITED_INFORMATION`).
- Source must remain readable, testable, maintainable, and extensible.
- Every completed patch updates Markdown documentation and assembly/file version.
- Final commit message format is `type(scope): message` and target branch is `master`.

---

### Task 1: Repository and solution foundation

**Files:**
- Create: `DotaUtility.slnx`
- Create: `Directory.Build.props`
- Create: `.gitignore`
- Create: five `src/*` projects and three `tests/*` projects
- Modify: `README.md`

**Interfaces:**
- Produces common nullable/analyzer/version settings and project-reference graph used by all later tasks.

- [ ] **Step 1:** Scaffold projects with `dotnet new` and add them to the solution.
- [ ] **Step 2:** Add project references according to `docs/design.md`.
- [ ] **Step 3:** Add centralized version properties and repository metadata.
- [ ] **Step 4:** Run `dotnet restore` and `dotnet build` to verify the empty graph compiles.

### Task 2: Domain and application monitoring via TDD

**Files:**
- Create: `src/DotaUtility.Domain/VisibilityState.cs`
- Create: `src/DotaUtility.Domain/VisibilitySnapshot.cs`
- Create: `src/DotaUtility.Application/IVisibilityProbe.cs`
- Create: `src/DotaUtility.Application/VisibilityMonitor.cs`
- Test: `tests/DotaUtility.Application.Tests/VisibilityMonitorTests.cs`

**Interfaces:**
- Produces `Task<VisibilitySnapshot> IVisibilityProbe.ProbeAsync(CancellationToken)`.
- Produces `VisibilityMonitor.SnapshotChanged` and start/stop lifetime.

- [ ] **Step 1:** Write failing tests for snapshot publication, cancellation, and probe exceptions becoming `Unknown` snapshots.
- [ ] **Step 2:** Run the focused tests and confirm they fail for missing production types.
- [ ] **Step 3:** Implement the smallest domain/application code to pass.
- [ ] **Step 4:** Run focused tests and refactor while green.

### Task 3: Win32 Read/Write memory infrastructure via TDD

**Files:**
- Create: `src/DotaUtility.Memory/IProcessMemoryAccessor.cs`
- Create: `src/DotaUtility.Memory/ProcessModuleSnapshot.cs`
- Create: `src/DotaUtility.Memory/Win32ProcessMemoryAccessor.cs`
- Create: `src/DotaUtility.Memory/BytePattern.cs`
- Create: `src/DotaUtility.Memory/ModulePatternScanner.cs`
- Test: `tests/DotaUtility.Memory.Tests/BytePatternTests.cs`
- Test: `tests/DotaUtility.Memory.Tests/Win32ProcessMemoryAccessorTests.cs`

**Interfaces:**
- Produces Read/Write attach/detach, module lookup, primitive/byte reads, and pattern matching with RIP-relative displacement decoding.

- [ ] **Step 1:** Write failing tests for wildcard pattern matching and RIP-relative target calculation.
- [ ] **Step 2:** Verify RED.
- [ ] **Step 3:** Implement `BytePattern`/scanner and verify GREEN.
- [ ] **Step 4:** Write a failing Windows self-process read test using unmanaged allocated memory.
- [ ] **Step 5:** Implement Read/Write Win32 accessor and verify GREEN.

### Task 4: Dota 2 Source 2 adapter via TDD

**Files:**
- Create: `src/DotaUtility.Dota2/DotaOffsets.cs`
- Create: `src/DotaUtility.Dota2/IDotaAddressResolver.cs`
- Create: `src/DotaUtility.Dota2/DotaAddressResolver.cs`
- Create: `src/DotaUtility.Dota2/Source2EntityResolver.cs`
- Create: `src/DotaUtility.Dota2/EnemyVisibilityInterpreter.cs`
- Create: `src/DotaUtility.Dota2/DotaUtilityProbe.cs`
- Test: `tests/DotaUtility.Dota2.Tests/EnemyVisibilityInterpreterTests.cs`
- Test: `tests/DotaUtility.Dota2.Tests/Source2EntityResolverTests.cs`
- Test: `tests/DotaUtility.Dota2.Tests/DotaUtilityProbeTests.cs`

**Interfaces:**
- `DotaUtilityProbe` implements `IVisibilityProbe`.
- `Source2EntityResolver.ResolveByHandle(uint handle)` resolves chunked Source 2 entity handles.
- `EnemyVisibilityInterpreter.Evaluate(int localTeam, int teamVisibilityMask)` returns domain visibility state.

- [ ] **Step 1:** Write failing visibility-mask tests covering Radiant, Dire, spectator/invalid teams, and zero masks.
- [ ] **Step 2:** Implement interpreter and verify GREEN.
- [ ] **Step 3:** Write failing entity-index/handle resolution tests against fake memory.
- [ ] **Step 4:** Implement resolver and verify GREEN.
- [ ] **Step 5:** Write failing end-to-end probe tests using a deterministic fake memory map/address resolver.
- [ ] **Step 6:** Implement the Dota probe, pointer validation, player-controller scan, and diagnostics; verify GREEN.

### Task 5: WPF MVVM dashboard

**Files:**
- Create/Modify: `src/DotaUtility.App/App.xaml`
- Create/Modify: `src/DotaUtility.App/App.xaml.cs`
- Create/Modify: `src/DotaUtility.App/MainWindow.xaml`
- Create/Modify: `src/DotaUtility.App/MainWindow.xaml.cs`
- Create: `src/DotaUtility.App/ViewModels/ViewModelBase.cs`
- Create: `src/DotaUtility.App/ViewModels/MainViewModel.cs`
- Create: `src/DotaUtility.App/Converters/VisibilityStateToBrushConverter.cs`
- Create: `src/DotaUtility.App/Services/FileSessionLogger.cs`

**Interfaces:**
- `MainViewModel` consumes `VisibilityMonitor`, exposes bindable status text/state/diagnostics, and owns monitor lifetime.

- [ ] **Step 1:** Implement composition root and view model over already-tested application contracts.
- [ ] **Step 2:** Build the borderless responsive dashboard with rounded cards, status orb, connection badge, pulse animation, and diagnostics.
- [ ] **Step 3:** Add session logging for attach/probe failures and state changes.
- [ ] **Step 4:** Run build and launch smoke test.

### Task 6: Documentation, version, verification, and delivery

**Files:**
- Modify: `README.md`
- Modify: `Directory.Build.props`
- Modify: `docs/design.md`

**Interfaces:**
- Produces reproducible run/build/test instructions and explicit current schema/profile notes.

- [ ] **Step 1:** Document architecture, live behavior, troubleshooting, schema offsets, and update process.
- [ ] **Step 2:** Ensure assembly/file/package version is set for this release.
- [ ] **Step 3:** Run `dotnet test` and Release build from a clean tree.
- [ ] **Step 4:** Launch against the running Dota process and inspect the generated session log/UI state.
- [ ] **Step 5:** Review `git diff`, commit `feat(visibility): add Read/Write Dota enemy visibility monitor`, and push `master` if a remote is configured.
