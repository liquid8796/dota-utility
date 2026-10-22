# Modifier source provenance - 2026-09-30

## Scope of 1.6.6

The existing ModifierAudit CLI records the modifier's client-cached team hint separately from generation-checked caster references. No new reader feeds the production visibility probe or ring. Every research result retains currentHeroVisibility=Unknown and canDriveCurrentOverlay=false.

Native evidence: `docs/evidence/modifier-source-native-2026-09-30.json`.
Live evidence: `docs/evidence/modifier-source-live-2026-09-30.json`.

## What the installed client actually does

The exact client/server hashes are stored in the proof. These are module-relative RVAs for that pair only, not reusable offsets for an arbitrary update.

### Locally reconstructed cached team

A client initializer at `0x14D7A30` reads the caster handle at modifier+0x68. Its absent/unresolvable-caster branch uses the parent handle at +0x74. It reads the resolved entity's raw team (+0x3E7) and stores it into modifier+0x94 at `0x14D7CB2`.

Consequently +0x94 is not inherently a server-provided enemy team or source-team fact. A lost caster cannot be attributed to the parent team just because this fallback exists. The CLI calls it **clientCachedTeamHint**, does not treat it as a resolved source and does not claim that every subclass initialization path is identical.

### Actual target true-sight property vs aura emitter

The primary method at client vtable slot 96 is `0x14E88E0` for CDOTA_Modifier_TrueSight, `0x14E8C20` for TrueSightFoW and `0x14E8F10` for TrueSightAll. The corresponding server property slots are 116, not 96. The proof checks primary RTTI, vtable entries and critical instruction bytes.

TrueSight/TrueSightFoW resolve the complete caster handle, check its native NPC type, call the caster's effective-team virtual method (+0xB68), then compare the requested team. Missing-caster paths compare the locally cached +0x94 value. TrueSightAll returns true for this property. None of these alone constitutes a verified final entity-visibility decision.

The own GemOfTrueSight item's same client property slot returns false. This is about that one property, not a claim that gems, wards, Track or Dust do not reveal units: their emitters and target effects need to be distinguished.

### Raw entity team is not the native effective team

The NPC effective-team method at `0x16418C0` can follow an owner handle at entity+0x514, recurse through the owner's virtual method and consult a game-mode-specific branch. Its raw entity-team read at `0x1641990` is a fallback.

The new caster reader therefore exports `rawEntityTeam`, `nativeEntityKind` and `ownerHandle`, but leaves `effectiveTeam=null` and `isEnemySightEvidence=false`. It does not execute or approximate the game's virtual method.

### FoW-specific callback is conditional

TrueSightFoW registers an additional callback at `0x14FBFE0` into member +0x10D0; plain TrueSight does not register that additional callback in the inspected method. The callback includes a team-context comparison and a lookup for **modifier_slark_shadow_dance**. That exclusion must not be silently renamed into a generic invisibility/smoke check. Its zero/one property result is not proof of instantaneous final enemy visibility.

The generic Lua wrapper named GetModifierProvidesFOWVision was found independently. This audit does not assume its name alone proves every registration/dispatch edge for the callback above.

## Buff payload handle encoding

The exact embedded dota_modifiers.proto descriptor defines 44 ordinary fields. It includes parent, caster, ability, aura_owner and arbitrary custom_int_values/custom_float_values. It has no ordinary field explicitly named enemy-visible; this is not an exhaustive negative claim about custom payloads or other channels.

The generated decoder for field 2 verifies tag 0x10 at `0xD06746` and stores its uint32 value at message+0xE0 (`0xD06793`). The consumer at `0x15026B0` reads that member from its stack-allocated message and handles it as a compressed network handle, not a runtime full handle:

- Invalid compressed handle: 0xFFFFFF.
- Index: 14 bits (mask 0x3FFF).
- Wire serial: 10 bits, after shifting by 14.
- Runtime handle uses a 15-bit index; the native consumer consults the live identity and flags while expanding the serial.

The byte contracts preserve this distinction for subsequent raw-payload research. This release does not ship a raw string-table reader: a bounded scratch attempt rejected its storage bounds before decoding and produced no authoritative payload observation.

## Runtime reader and consistency checks

`ModifierCasterReader` is part of the existing research CLI. It processes at most 256 unique sources, rejects invalid ranges, checks the full caster generation and identity back-pointer, validates the vtable/method module range, and rereads its recorded metadata. Appearing/disappearing/recycled sources and observed owner-context changes discard the sample. Invalid/missing handles remain explicitly unavailable and never fall back to cached or parent team.

The owner list is read before and after source acquisition. Observable list/token/parent/caster/ability/removal/cache/intensity changes discard the joined sample. This is best-effort external consistency checking, not atomic frame isolation or proof against all possible ABA allocation races.

Only local Read/Write process access and installed-file analysis were used.

## Live observations and their limits

An eight-sample baseline and a separate 150-sample window used native local identity player 6, Radiant, network slot 7, hero handle 019E05D4. Relevant observed names included the owner's own gem and Mirana effects. These samples had no independent enemy-view labels; absence of a target reveal modifier is not Hidden.

The new CLI later accepted 20 samples on a fresh context: player 9, Radiant, network slot 4, hero handle 004F8328. The first sample distinguished the owner's caster reference (raw team 2) from the caster of modifier_lycan_howl (raw team 3). Both generations resolved. This verifies source attribution plumbing for those observations, not that Howl or any other observed effect proves sight. The earlier hero pointer was not reused.

## Reproduce

From the repository root, with the exact audited pair:

    python scripts/research/audit_modifier_source_provenance.py "D:/Program Files (x86)/Steam/steamapps/common/dota 2 beta/game/dota/bin/win64" --output .scratch/new-source-proof.json

For a new bounded owner/source observation:

    dotnet run --project tools/DotaUtility.ModifierAudit -c Release -- --samples 20 --output .scratch/new-source-capture.jsonl

Both commands protect existing output. Hash changes require fresh analysis, not a bypass flag. Dashboard/loading emits inactive; it never supplies a Hidden observation.

## Remaining acceptance gate

A usable enemy-sight source must prove the right local hero and opposing-team semantics, ordinary-client availability, freshness, and a repeatable entering/leaving transition against independently labelled enemy-view truth. Observer-ward-only and no-combat cases remain unverified. Source attribution and byte-contract tests do not satisfy that gate by themselves.


## Release validation

The full .NET suite passed 190 tests (34 added source/snapshot cases); the Python research suite passed 93 tests (17 added provenance audit cases, with per-contract mutation subtests). The targeted modifier/source suite passed 77 cases. Formatting verification and whitespace checks passed. Invalid CLI counts return exit code 2 without creating output, and existing output is protected from overwrite.

These tests validate the tooling contracts, bounds, source generation, observed races and interpretation limits. They are not measurements of enemy-sight detection accuracy. Version/assembly/file version is 1.6.6 / 1.6.6.0 / 1.6.6.0.
