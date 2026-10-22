# Combat-log visibility provenance audit — 2026-09-30

## Result and scope

Version 1.6.4 is a research release, not a completed enemy-sight detector. The goal remains fresh visibility of the actual local hero to the opposing team, without requiring software on another person's host.

The audit verifies the installed DLLs, embedded protobuf descriptors, three distinct client message routes, one server event producer and one normal recipient-filtering path. It never attaches a debugger, executes game code, changes subscriptions, alters teams or bypasses recipient selection.

**A combat-log visibility flag is not sufficient to report the local hero as Visible.** In one verified producer the flag is set if either of two participating entities is visible to the corresponding team. A true flag may therefore be caused by the other participant. The record is also event-scoped, not a continuous visibility heartbeat.

The app's conditional opposing-team bitmap probe and Unknown behavior remain unchanged. No terrain-FoW, item, replay or combat-event fallback was enabled.

## Build identity

The installed DLLs changed since the September 29 audit. The resumed script first rejected them rather than using old addresses. A separately verified profile now applies to:

| Module | SHA-256 |
| --- | --- |
| client.dll | 88fa24c60a8c68f84d25cc2285019889c05db0c55b2bed1165d6f98fa7c18215 |
| server.dll | 02b90bbb5814bc1af39cada58b6b8311808e01e84aa8fab894bdaea896a8cd47 |

This profile belongs only to `audit_combat_log_messages.py`. Older audit tools retain their original default hash gates; they were not silently enabled for this newer DLL pair.

## Schema and presence

All three embedded descriptor fingerprints match between client and server, but their locations moved:

| Descriptor | Size | Client RVA | Server RVA |
| --- | ---: | --- | --- |
| dota_shared_enums.proto | 22,746 | 0x3D7CE70 | 0x2E98910 |
| dota_usermessages.proto | 43,676 | 0x3D60940 | 0x2E87600 |
| dota_modifiers.proto | 1,572 | 0x3D73E80 | 0x2EBEFC0 |

Audited message field counts are 82 for CMsgDOTACombatLogEntry, 5 for CombatLogBulkData, 9 for UnitEvent and 44 for ModifierBuffTableEntry. Names, field numbers, labels, types and native routes are checked, not just counts.

CombatLogEntry tags 11/12 are optional booleans `is_visible_radiant` / `is_visible_dire`. Native serialization tests presence bits 12/13 at record+0x10 and reads values at +0x54/+0x55, emitting wire tags 0x58/0x60. Target-name index is at +0x30; timestamp is at +0x50 with presence bit 11.

The decoders preserve absence as null/unknown, distinct from explicit false. `target_name` is a string-table index, not an entity handle or PlayerID. The audit conservatively rejects duplicate singular fields, although ordinary protobuf merge semantics permit last-one-wins. Payload, field, string and varint sizes are bounded; non-finite timestamps are rejected.

## Distinct receive routes

| Message | ID | Client callback | Examined destination |
| --- | ---: | --- | --- |
| CombatLogData | 468 | 0x1CA59B0 | HUD ingestion 0x30E0A90 |
| CombatLogBulkData | 470 | 0x1CA5860 | archive insertion 0x1C54A80 |
| CombatLogDataHLTV | 554 | 0x1CA59F0 | archive insertion 0x1C54A80, subject to a native guard |

Wrapper/delegate RTTI, callback assignments, native message-ID loads and call/jump targets were checked. Static registration is not proof of live normal-client delivery. A caller-supplied message-ID label on a payload is not verified provenance.

## Server producer: two-participant OR

A producer constructs a CombatLogDataHLTV wrapper at server+0x1E6627A, with inline record base `[rbp-0x50]`. Its field writes match the offsets independently confirmed by client serialization.

At 0x1E66735..0x1E66799 it checks two entities, held in r12 and rsi, using per-team visibility primitive 0x19FFFD0. Neither register is assumed to be the user's local hero.

Radiant calls at 0x1E66742/0x1E66753 feed the branch setting presence bit 12 and record+0x54 at 0x1E6675C when either result is true. Dire calls at 0x1E66774/0x1E66785 analogously set presence bit 13 and record+0x55 at 0x1E6678E.

The primitive reads the server entity mask at +0x544 after special-entity/dirty-state handling and tests the requested team bit. This ties the rule to native visibility rather than a guessed field name.

The positive-setting rule for this producer is:

    eventFlag(team) is set true when Visible(A, team) OR Visible(B, team).

A logical counterexample, not a claimed captured match event:

    A is hidden; B is visible; eventFlag is true.
    Therefore eventFlag=true does not establish Visible(A).

This rule is verified for the inspected producer, not every combat-log type. It does not prove that normal clients receive every HLTV record.

## Normal recipient filtering

A separate normal sender constructs CombatLogData at 0x1EB73DB, record base `[rbp-0x30]`. At 0x1EB74DD it reads recipient team from +0x344. Team 2 is checked against record+0x54; team 3 against record+0x55. False branches at 0x1EB74F7 to 0x1EB7611, skipping the examined send call at 0x1EB7609.

Other type/name/recipient filters exist. This is an event-filtering gate, not a complete contract for all recipients or continuous enemy sight. The normal path is not relabelled as the separately verified HLTV producer.

## Live cache observation and reusable CLI

A prototype series at 06:50:28..06:50:32 UTC on September 30 validated Dota PID 15212, native local PlayerID 9, Dire Leshrac and full hero handle 0045054B. Eight samples had an initialized but empty normal HUD cache: root=-1, capacity=0, no records.

This was not a dashboard/controller-unavailable result. An empty short-window cache does not prove absence of all delivery or establish Hidden. No synchronized enemy-view labels were recorded; no populated record was verified.

The bounded prototype was promoted into `tools/DotaUtility.CombatLogAudit`. The CLI checks the DLL hash, loaded getter/serializer bytes, native local controller, full hero identity, entity/network context and HUD identity. It traverses at most 128 nodes and 512 records, checks allocation bounds and entry vtables, and rereads every block. An observed race or invalid read discards the sample. These checks are not atomic frame synchronization.

All reports retain `currentHeroVisibility=Unknown` and `canDriveCurrentOverlay=false`. Historical cache events cannot drive the overlay. The app neither references nor launches this CLI.

## Reproduce

From the repo root with the exact audited DLL pair:

    python scripts/research/audit_combat_log_messages.py --binaries "D:/Program Files (x86)/Steam/steamapps/common/dota 2 beta/game/dota/bin/win64" --output .scratch/combat-log-audit.json

The offline verifier uses Python's standard library. It never loads or executes DLL code. Unknown hashes or changed contracts return exit code 2, with no skip-build-check switch. An old output file is not a new successful audit when the command fails.

For a bounded live observation with a new output filename:

    dotnet run --project tools/DotaUtility.CombatLogAudit -c Release -- --samples 8 --output .scratch/combat-cache.jsonl

The CLI refuses to overwrite existing output. Samples are 500 ms apart and bounded to 1..120. No Dota/dashboard/no assigned hero produces inactive/unavailable output. It does not launch, restart or close Dota.

For an existing single wire payload:

    python scripts/research/audit_combat_log_messages.py --event existing-entry.bin --message-id 468

468/554 is a caller assertion; bulk 470 is intentionally unsupported by this single-entry decoder.

Tests:

    python -m unittest discover -s tests/research -v
    dotnet test DotaUtility.slnx -c Release

## Evidence and remaining gate

`docs/evidence/combat-log-audit-2026-09-30.json` contains descriptor summaries/fingerprints, client bindings, serializer checks, sender byte/call contracts and the hypothetical OR counterexample. `docs/evidence/combat-cache-prototype-2026-09-30.json` preserves the initial eight live observations and their limits.

A successful next candidate must identify the current local hero, distinguish entity visibility from event visibility, prove normal-client delivery, and cover entering/leaving sight without combat. It needs independently labelled enemy-view ground truth, ward-only transitions and match/hero changes. These findings exclude the direct combat-flag-to-ring shortcut, not every possible non-host approach.

## Release verification

The full .NET suite passed 113 tests (23 added cache-reader cases), and the Python research suite passed 61 tests (43 added cases). They exercise bounds, optional-field presence, cycles, data mutation, wrong entry type, unsupported builds and event-not-hero interpretation. These are tooling tests, not verified enemy-sight accuracy.

The packaged CLI 1.6.4.0 completed a separate eight-sample run at 07:15:18..07:15:22 UTC. All eight lacked a validated active controller/assigned hero and were emitted as inactive without reading stale cache data; see docs/evidence/combat-cache-cli-smoke-2026-09-30.json. This is distinct from the earlier active Leshrac prototype run. Malformed CLI arguments returned 2 without writing output, and an existing evidence file was protected from overwrite. No game input, team change, subscription change or debugger was used.
