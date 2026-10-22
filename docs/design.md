# Dota Utility Design

## Current version: 1.6.6

Version 1.6.6 adds generation-checked modifier-caster provenance to the existing bounded observation CLI. Cached modifier team, raw entity team and native effective team are deliberately distinct. See docs/modifier-source-provenance.md. Version 1.6.5 introduced the owner-modifier observation CLI. The shipped enemy-sight detector remains conditional and its semantics are unchanged from 1.6.2.

DotaUtility is a Windows C#/.NET 10 companion intended to report when the local hero is visible to the opposing team and to draw a click-through ring beneath that hero.

**Exact non-host enemy sight is not yet established for every normal client session.** The current opposing-team bitmap reader is conditional: missing data remains Unknown. It does not substitute terrain FoW, inventory changes, own-team data, or another player's hero.

Version 1.6.2 fixes a verified local-hero selection bug. The old probe equated the network-local slot/index with a Dota PlayerID. A live audit caught it selecting a Radiant Earthshaker while Dota's native local-controller accessor selected a Dire Rubick. Full entity-handle validation alone could not catch this: the wrong player's hero was a perfectly valid entity.

## Safety boundary

Production requests PROCESS_VM_READ, PROCESS_QUERY_INFORMATION, PROCESS_QUERY_LIMITED_INFORMATION and SYNCHRONIZE. It requests no write, remote-thread or debugging rights. No changes are made to another person's host.

The old VScript/relay compatibility mode is disabled by default. It remains opt-in via `DOTAUTILITY_ENABLE_COMPAT_RELAY=1`, not a requirement or a solution to missing non-host data.

## Architecture

- Domain: immutable visibility snapshots; `LocalPlayerId` now consistently means the Dota PlayerID read from the native local controller.
- Application: polling, optional override providers and snapshot delivery.
- Memory: external read-only process access and signature scanning.
- Dota2: build-compatibility gate, address resolution, `LocalPlayerContextReader`, full-handle entity resolution, conditional `ClientTeamVisibilityReader`, and world-to-screen projection.
- App: WPF status and click-through hero ring.

## 1. Native local-controller selection

The current native accessor at client RVA `0x171EFA0` (`2026-10-19` build `dfec84e4...`; previously `0x1750840`) selects an entry from a local-controller pointer array. An existing client consumer passes primary split-screen index 0. A unique code signature locates the array without hard-coding its RVA in production:

```text
33 C0 83 F9 FF 0F 44 C8 48 63 C1 48 8D 0D ?? ?? ?? ?? 48 8B 04 C1 C3
```

The RIP-relative displacement begins at pattern offset 14; the LEA ends at offset 18. On the `2026-10-19` audited build the target array RVA is `0x61E7D48` (previously `0x619BEF8`). The reader uses array[0]. It never indexes this array with a Dota PlayerID or network slot, and never calls the accessor inside Dota.

`LocalPlayerContextReader` validates the selected controller's local flag (`+0x778`), non-HLTV flag (`+0x6D8`), identity back-pointer (`+0x10`) and complete handle/generation (`+0x10`). It then reads that controller's Dota PlayerID (`+0x910`), playing team (`+0x3E7`) and assigned hero (`+0x914`). The assigned-hero handle passes full-generation validation and the hero must belong to the same playing team.

A null/unreadable native pointer, unconfirmed local flag, HLTV controller, invalid ID, missing assigned hero or identity mismatch returns Unknown. There is no scan for a different controller with a numerically matching ID.

The raw field `CNetworkGameClient::localPlayer` is retained only as a changing-network-context token. In the audited session it was 4, whereas the actual Dota PlayerID was 9. An observed slot-plus-one relationship is not used as a production fallback.

## 2. Conditional opposing-team bitmap lookup

A 15-entry handle array references available team-data objects. The current signature locates it through the replay-named callback `OnReplayComap_TaggedAsVisibleByTeam`; this code reference does not establish live callback delivery. See `docs/replication-audit.md`.

For standard Radiant/Dire play, the reader chooses team 3 for a team-2 local hero and team 2 for a team-3 local hero. It validates the complete team-data handle and entity team, then addresses `m_bNPCVisibleState` as 256 64-bit words:

```text
wordIndex = heroEntityIndex / 64
bitIndex  = heroEntityIndex % 64
word      = *(teamData + 0xE58 + wordIndex * 8)
bitSet    = (word & (1UL << bitIndex)) != 0
```

A readable, identity-consistent word is interpreted only for that requested team and hero. Missing opposing-team data returns Unknown, not Hidden. Own-team data is not a replacement. The current live audit, using the corrected local identity, found only Dire data for a local Dire player; Radiant data was unavailable.

## 3. Consistency and lifecycle

`Source2EntityResolver.ResolveByHandle` checks the complete handle at `CEntityIdentity + 0x10` before and after reading the entity pointer. `ResolveByIndex` enumerates current occupants and is not validation of an old handle.

After reading a visibility word, the team reader reconfirms the team handle, entity and team. Before publishing any result, the probe rereads the entity/network globals, network-local value and complete local-player context (native controller, controller generation, Dota PlayerID, assigned hero, hero generation and team). An observed change discards the sample.

Dashboard/loading/no-assigned-hero states are normal inactive contexts. The reader reacquires current identities on the next sample and does not cache an old hero. These guards reduce race windows; they do not create an atomic game-frame snapshot.

## 4. Corrected evidence interpretation

The engine-selected controller in the September 29 audit was entity #5, Dota PlayerID 9, local flag 1, Dire Rubick. The legacy ID-equality lookup selected entity #1, Dota PlayerID 4, local flag 0, Radiant Earthshaker. Both hero handles resolved successfully.

This explains a concrete false-positive path: the old probe could query the actual local team's bitmap about an enemy hero while labelling that enemy hero as the user. It could also position an overlay at the wrong hero. This is not a claim that every prior symptom had the same cause.

Older CFoW, inventory and team-bitmap traces lacking native-controller validation cannot certify the user's local-hero visibility. Historical recordings are preserved with corrected interpretation, not silently relabelled.

## 5. Rejected sources remain rejected

`C_BaseModelEntity::m_iTeamVisibilityBitmask = 0x780` is diagnostic only. `CFoW` terrain bit `0x4000` is not used for enemy entity reveal. The earlier layer-0-to-enemy mapping was not established, and many old samples also had unverified local identity. Inventory byte changes are not used for vision detection.

Fixing hero selection does not justify re-enabling any of those sources without new, independently labelled validation on the correct hero.

## Validated layout

| Field | Offset |
| --- | --- |
| CNetworkGameClient local context value | 0xF8 |
| CEntityInstance identity pointer | 0x10 |
| CEntityIdentity complete handle | 0x10 |
| C_BasePlayerController local flag | 0x770 |
| C_BasePlayerController HLTV flag | 0x6D0 |
| C_DOTAPlayerController Dota PlayerID | 0x908 |
| C_DOTAPlayerController assigned hero | 0x90C |
| C_BaseEntity team | 0x3E7 |
| C_BaseEntity scene node | 0x330 |
| CGameSceneNode absolute origin | 0xD8 |
| C_DOTA_DataNonSpectator NPC bitmap | 0xE58 |
| CEntityIdentity stride | 0x70 |

These values apply to the audited September 2026 client; the reproducible verifier checks its full binary hash before using pinned audit addresses. Production still scans for its address signatures.

## Indicator and UI

Visible enables the existing light-green WPF ring at the **native local hero's** projected position. Hidden and Unknown hide it. The existing enlarged 350 x 190 DIP overlay is unchanged by 1.6.2. It remains mouse-transparent, non-activating and hidden when Dota is not foreground or projection is invalid.

Snapshot detail now distinguishes Dota PlayerID, raw network slot and controller entity index. A missing enemy object is explicitly described as unavailable, not hidden.

## Evidence and next validation gate

See `docs/local-controller-audit.md`, `docs/evidence/local-controller-audit-2026-09-29.json` and `scripts/research/audit_local_controller.py`. The previous registration audit remains in `docs/replication-audit.md` with its local-hero interpretation corrected.

Next, trace actual live serialization and recipient selection using the corrected native local controller. Candidates require independent, time-aligned enemy-view labels, identity continuity and freshness checks. A changing bit at an arbitrary valid entity is not sufficient to promote a new detector source.

## Initialized-field research tooling

The separate CLI tools/DotaUtility.RegistryAudit is Read/Write and build-gated; it is not a visibility source used by the app. It exports bounded field-registration/parent graphs without raw process pointers. The script scripts/research/verify_registered_fields.py checks structural completeness and positive-control offsets/types. See docs/registered-fields-audit.md. The evidence does not assert live delivery or exact enemy sight, and no overlay is enabled from unvalidated data.

## Combat-log provenance audit (1.6.4)

The September 30 binary pair has separately verified hashes and receive/sender contracts. In one server producer, is_visible_radiant/dire is set when either of two participants is visible to that team; it is not an entity-specific local-hero heartbeat. Normal delivery has its own recipient filtering, distinct from bulk/HLTV archive routes. Absent optional fields remain unknown, and an empty cache is not Hidden.

The standalone tools/DotaUtility.CombatLogAudit executable is not a probe used by the app. Its cache samples validate native local identity and full handles, enforce read/allocation budgets, and discard observed concurrent changes. Every report retains currentHeroVisibility=Unknown and canDriveCurrentOverlay=false. See docs/combat-log-audit.md and the September 30 evidence files for exact scope, contracts and reproduction commands. Older audit tools keep their old default hash gates; they were not silently enabled for the new DLLs.

## Owner modifiers and delayed ability effects (1.6.5)

The Slark passive update was traced from a concrete server method through the opposing-team visibility predicate to addition/removal of the regeneration modifier. The client counterpart is a no-op. Neutral-hit suppression, unit-state predicates, activation delay and linger logic prevent treating modifier presence/absence as an instantaneous enemy-sight boolean. This does not implement the requested universal detector.

The Read/Write tools/DotaUtility.ModifierAudit CLI captures the actual local hero modifier list with manager/parent checks, native instance tokens, bounded RTTI and names, full local-context validation and reread guards. Raw true-sight caches retain their validity masks; an invalid cache is unknown, not false. Modifier identifiers (including the player's own Gem of True Sight) do not drive the overlay. The application does not reference this research CLI. See docs/owner-modifier-audit.md.
