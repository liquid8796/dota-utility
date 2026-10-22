# Visibility replication audit — 2026-09-28

## Correction added in 1.6.2: local-controller identity

The September 29 follow-up proved that equating network-local value with Dota PlayerID can select another player's hero. Native accessor/flag checks now replace that lookup. The older Venomancer and changing-bitmap observations below therefore do not independently establish the local hero or the own/enemy-team labels. Original evidence is retained; see docs/local-controller-audit.md for the corrected live observation and source patch.

## Outcome and scope

This audit found a replay-compatibility registration behind the callback previously treated as a general live visibility update. It also found and corrected a concrete entity-handle validation defect. Neither finding establishes a universal, exact enemy-sight detector for an unmodified non-host client.

Production version 1.6.1 adds sampling-consistency guards. It does not restore the rejected CFoW layer-0 heuristic, guess hidden wards, or call into another person's host.

## Build identity and reproducibility

The findings below apply to these installed binaries, not arbitrary later patches:

| Module | SHA-256 |
| --- | --- |
| client.dll | `33f6c667b628c9a63106b6d9148f35eaa3a4ec665c43dfbc6b8440d320dfaccf` |
| server.dll | `78e5533be686544508346ae69b80e812eaeff3f1cbc08c69ebdf02765739e189` |

Run the standard-library-only verifier against the directory containing those DLLs:

```powershell
python scripts/research/audit_visibility_registration.py '<Dota installation>/game/dota/bin/win64'
```

It reads files only, validates their complete hashes, resolves the relevant RIP-relative addresses, checks the callback name and instruction bytes, verifies the team-data classes through MSVC RTTI, and checks vtable slot 94. Unknown builds return `AUDIT_REJECTED` rather than silently applying old addresses. The captured result is `docs/evidence/replication-audit-2026-09-28.json`.

## 1. The callback's actual registration

The earlier search for an absolute pointer to `client.dll + 0x1AF4770` found none. A broader search including RIP-relative LEA references found an actual registration at `+0x1ABA707`.

The adjacent registration stores:

- name at RVA `0x488C470`: `OnReplayComap_TaggedAsVisibleByTeam`;
- callback address at RVA `0x1AF4770`;
- containing registered class name: `C_DOTA_BaseNPC`.

The spelling `Comap` is copied exactly from the binary. The name reference originates at `+0x1ABA6EF`; the function reference originates at `+0x1ABA707`. Both references are independently decoded and checked by the verifier.

This callback's body can distribute a supplied team mask into `C_DOTA_DataNonSpectator::m_bNPCVisibleState`. That observation explains the structure of the bitmap, but the replay-named registration does **not** establish that the callback receives enemy-sight transitions during normal live non-host play. It must not be described as a verified general live-network hook. Failure to hit this callback in a live trace is not proof that a breakpoint was installed incorrectly.

The installed server schema also names `m_iTaggedAsVisibleByTeam` at offset `0xED4`. The client binary contains its name string but the saved client schema does not expose the corresponding storage field. These observations are consistent with replay compatibility; they are not a full proof of every serialization path or of the absence of other usable client signals.

Local detailed evidence: `.scratch/ReplicationAudit/callback_xrefs.json`, `callback_registration.txt`, `registration_strings.json`, and `replay_metadata.json`.

## 2. Native handle resolution includes the generation

Inside the callback, the engine masks the handle to locate its entity index, calculates the identity entry, and then performs:

```text
client.dll + 0x1AF480A: cmp dword ptr [rax + 0x10], edx
```

`EDX` still contains the complete handle. On mismatch the native code rejects the identity. The prior utility instead masked the handle and returned the current occupant of that slot without checking its serial/generation.

Example: handles `0x00018021` and `0x00020021` have the same low index `0x21`, but identify different generations. A matching pointer, team, or vtable does not prove that an old handle remains valid after a slot has been recycled.

Version 1.6.1 now validates the complete handle before and after reading the entity pointer and confirms that pointer again. It rejects the `0xFFFFFFFE` sentinel as well. This behavior is covered by deterministic tests, including a simulated generation change between reads. `ResolveByIndex` remains useful for enumeration but explicitly does not validate a previously captured handle.

The team bitmap reader additionally rechecks its handle-table entry, resolved team-data entity, and team after reading a word. The probe rechecks its entity-system/network-client globals, local player ID, assigned hero, hero generation, and team before publishing a successful sample. Observed changes return `Unknown`; they never become a stale `Visible` or `Hidden` sample. These are best-effort race guards, not a claim of atomic snapshot isolation.

## 3. Team-data policy is recipient-dependent

The server classes and slot-94 addresses were verified using on-disk RTTI and vtable contents:

| Class | Vtable RVA | Slot-94 method RVA |
| --- | --- | --- |
| CDOTA_DataRadiant | `0x3C4F610` | `0x1EB8E80` |
| CDOTA_DataDire | `0x3C4FD70` | `0x1EB8DC0` |

Their methods branch on a flag at argument `RDX + 0x38`, a selector at `RDX + 0x30`, and helper predicates associated with the object at `RDX + 8`. Under the branch assumption that the special flag and helper predicates are false, the disassembled control flow gives:

| Selector at +0x30 | Radiant return | Dire return |
| --- | --- | --- |
| 1 | 4 | 4 |
| 2 | 4 | 8 |
| 3 | 8 | 4 |
| Other | 8 | 8 |

The special flag gives return 4 in both methods. Other role-specific branches also exist. This table records raw return values from static control-flow analysis; it does not assume that undocumented argument fields or numeric return values have been fully mapped to current engine API names.

The result supports a recipient-dependent policy and contradicts treating class existence or a single trace as proof that every client receives both teams' data. The full path from these return values to an actual live recipient packet remains a validation gate.

Local detailed evidence: `.scratch/ReplicationAudit/static_regions.txt`. Earlier unlabelled traces are retained as observations, not independent enemy-sight ground truth.

## 4. Static schema metadata needs a control experiment

The on-disk descriptors for both `m_iTeamVisibilityBitmask` and `m_bNPCVisibleState` have zero static metadata entries. It would be incorrect to infer "not replicated" from that fact alone: the control field `m_iTeamNum` also has no network annotation in the parsed static descriptor (server count 0; client count 1, `MNotSaved`).

Therefore a missing annotation in this extracted table is inconclusive about live serialization. Future work must inspect the relevant live serializer/recipient path rather than equate a reflected layout with a wire contract.

Local detailed evidence: `.scratch/ReplicationAudit/field_metadata_controls.json`; descriptor addresses and counts are also preserved in the committed evidence JSON.

## 5. Inactive-match samples are excluded

At 2026-09-28 16:55:32 UTC, Read/Write inspection of Dota PID 11924 found a local player-6 controller but `m_hAssignedHero = 0xFFFFFFFF`. All 15 inspected team handles were also `0xFFFFFFFF` across three samples. This is not a valid hero-vision sample; dashboard/loading/selection states must be excluded. No old hero address was used as a replacement.

A later audit at 2026-09-28 17:14:08 UTC did have a controllable hero: local player 6, a Radiant Venomancer. Its complete assigned-hero handle matched the current identity entry. The Radiant team-data object likewise passed full-handle, RTTI, team-number and identity-back-pointer checks. The Dire team-data handle remained `0xFFFFFFFF` in all three samples while the hero position changed. This is separate active-context evidence that the current handle-array path has no opposing-team object to read in that session. It is not a dashboard artifact, and it is not proof about every possible alternative serializer path. A pointer-redacted extract is in `docs/evidence/live-context-audit-2026-09-28.json`.

Likewise, past traces that retained raw hero/item/team pointers through a match change cannot certify visibility merely because memory reads continued to succeed. A same-class allocation may reuse an address.

## Validation status

The .NET regression suite after the identity patch passed 64 tests. Twenty new tests exercise stale handles, in-flight pointer/generation changes, team-data rebinding, player/team/global-context changes, dashboard-to-new-generation reacquisition, and unavailable/unreadable data. The additional invalid-handle theory case covers the second sentinel. Existing stable visible/hidden fixture cases still pass.

These tests validate the implementation's reading/consistency behavior against fixtures. They are **not** evidence that an enemy-team bitmap is available and authoritative in an arbitrary live non-host match, and they do not validate the visual overlay in such a match.

## Next gate before changing detector semantics

The next live investigation should identify the serializer and audience for an actual non-host session, keep full handles and context identities with every sample, and distinguish live state from replay/spectator compatibility. Candidate transitions must have independent time-aligned labels; client-local FoW tiles and changing item bytes are not substitutes for that evidence.

A new candidate is not promoted to production `Visible/Hidden` until availability, freshness, entity identity and semantics are validated together. The other person's host remains unmodified throughout.

## 2026-09-29 initialized field-registration continuation

The follow-up docs/registered-fields-audit.md inspects initialized registration objects and parent graphs: 14 client classes / 328 fields, and 11 server classes / 193 fields. Positive controls validate team, unit-state, hero ID and the fixed64 team bitmap. The two previously targeted named masks are absent from these ordinary lists; their absence is not asserted to exhaust all possible signals. A separate compatibility mapping handles the tagged mask.

The native-controller-validated Sniper series contains 80 samples, with own Radiant data present and opposing Dire data unavailable. It contains no enemy-view ground-truth labels. The new research CLI and verifier do not change detector semantics, access the other person's host, or certify live packet delivery or exact enemy sight.


## 1.6.7: publication route and Arcade context

The installed server mask updater now has a reproducible offline publication
audit, separate from the owner-modifier source work in 1.6.6. The current/previous
mask difference feeds two paths: newly visible teams are filtered for modifier
publication, while all changed teams update the corresponding team-data bitmap.
The latter marks server field 0xD00 as network-changed before storing its word.
This does not prove normal owner-client receipt of the enemy team data.

A Read/Write native game-manager observation also identified the live Arcade
package as Workshop 3320645051, map dota. Offline review of its local scripts
confirmed that modifier_zhenshibaoshi_xianshi is a Gem UI display modifier, not
a direct enemy-sight query. No owner-sight heartbeat was established in the
reviewed synchronization surface; this is a scoped finding, not a whole-program
proof of absence or a guarantee about the host's loaded script version.

See [the publication audit](reveal-publication-audit.md),
[the native contracts](evidence/reveal-publication-native-2026-09-30.json), and
[the local-addon evidence](evidence/active-addon-reveal-audit-2026-09-30.json).
No terrain, modifier-name, publication-pulse or missing-data shortcut is enabled
for Visible/Hidden or the ring. Unknown remains the result without a verified
source. Full package sources and content keys remain outside the tracked tree.
