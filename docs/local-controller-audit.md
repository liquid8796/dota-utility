# Local-controller identity audit - 2026-09-29

## Result

The earlier probe confused two independent identifiers: the network-local slot/index at CNetworkGameClient + 0xF8 and the Dota PlayerID at C_DOTAPlayerController + 0x908. Equating them can select another player's valid controller and hero. This is not a stale-pointer issue and is not corrected by checking handle serials alone.

Version 1.6.2 now uses the engine's native primary local-controller pointer. It does not claim to have found a universal enemy-sight signal. With the correct local identity, the tested session had only its own-team data object; the opposite-team object needed by the current bitmap reader was unavailable.

## Native-code and schema evidence

Audited client.dll SHA-256:

`33f6c667b628c9a63106b6d9148f35eaa3a4ec665c43dfbc6b8440d320dfaccf`

The native accessor at RVA 0x1750840 contains:

```text
xor   eax, eax
cmp   ecx, -1
cmove ecx, eax
movsxd rax, ecx
lea   rcx, [localControllerArray]
mov   rax, [rcx + rax*8]
ret
```

Its primary-index client consumer at RVA 0x1B3FDB6 passes ECX=0 before the call. The local-controller array target in this build is RVA 0x619BEF8. The production resolver signature-scans the accessor and decodes its LEA rather than pinning that RVA.

The verifier independently matches the descriptor names and offsets:

| Name | Offset | Descriptor RVA |
| --- | --- | --- |
| m_bIsLocalPlayerController | 0x770 | 0x5A79610 |
| m_bIsHLTV | 0x6D0 | 0x5A79570 |
| m_nPlayerID | 0x908 | 0x5AA06D0 |
| m_hAssignedHero | 0x90C | 0x5AA06F0 |

A code reference alone is not enough. The Read/Write live check also compared the native array pointer with enumerated controller types and local flags. It did not invoke the accessor inside the game.

## Same-time comparison in the live session

Three samples at 17:44:44-17:44:45 UTC on September 28 (00:44 local time on September 29) gave the following consistent result. Both assigned-hero handles were valid; only one controller was actually local.

| Check | Legacy ID-equality choice | Engine native choice |
| --- | --- | --- |
| Raw network-local value | 4 | 4 |
| Controller entity index | 1 | 5 |
| Dota PlayerID | 4 | 9 |
| m_bIsLocalPlayerController | 0 | 1 |
| Equals native accessor result | No | Yes |
| Controller / hero team | Radiant / Radiant | Dire / Dire |
| Assigned hero runtime class | Earthshaker | Rubick |

The only available team-data object was C_DOTA_DataDire. Its full handle resolved and its team was Dire. The Radiant team-data handle was 0xFFFFFFFF. Once the local controller is identified correctly, this means **own-team data present, opposing-team data unavailable**, not enemy-team data magically available to a Radiant local player.

The position and all-controller records are diagnostic observations, not enemy-view ground truth. The committed JSON retains only the two relevant controller rows and team availability, omitting runtime process pointers and unrelated players.

## Why this affects both status and the ring

With the old identity choice, the tool could ask the Dire bitmap whether it sees an enemy Earthshaker, and label that result as whether the user's own hero is seen. That can generate plausible transitions while tracking the wrong entity. It could also project the ring at that other hero instead of at the user-controlled hero.

This is a concrete false-positive and wrong-position mechanism demonstrated by the two simultaneously valid controllers. It does not establish that every earlier missing ring or false status had this single cause.

The old FoW and team-bitmap trace labels must therefore remain untrusted unless they independently checked native local-controller identity. Historical evidence is preserved with an interpretation correction in docs/replication-audit.md.

## Production change

LocalPlayerContextReader now reads native array[0], verifies local flag 1 and HLTV flag 0, checks the full controller identity/generation, reads the actual Dota PlayerID, and resolves that controller's assigned hero with full-handle validation. The controller and hero must belong to the same playing team.

The probe rereads the network context and complete local context before publishing any result. Network-slot changes, native-controller swaps, local-flag changes, Dota PlayerID changes, controller-generation changes, assigned-hero changes and team changes invalidate the sample. No controller is chosen by numeric equality with the network value. There is no own-team bitmap substitution and no re-enabled terrain/item heuristic.

In the first post-patch production-probe smoke test at 17:53:10 UTC, the selected Dota PlayerID was 9, local team 3, opposing team 2, network slot 4 and controller entity #5. The state remained Unknown with an explicit missing-team-2-data message. That is the expected missing-data behavior, not a verified sight transition.

## Reproduction

```powershell
python scripts/research/audit_local_controller.py '<Dota installation>/game/dota/bin/win64'
dotnet test DotaUtility.slnx -c Release
```

The standard-library-only Python audit rejects unsupported DLL hashes, checks that the accessor signature has one match, verifies its RIP-relative target, validates the primary-index call site, and matches the four field descriptors. It reads files only. Unknown-build rejection is tested independently of the .NET regression fixtures.

The new .NET fixtures deliberately contain a remote controller whose Dota PlayerID equals the raw network slot, while the real native controller has another PlayerID and team. They cover this misselection, both signed RIP-relative directions, missing native signatures, HLTV/local-flag rejection, missing opposing-team data, in-flight identity changes and reacquisition after a dashboard/controller change. Test success proves those code paths against fixtures, not universal in-game enemy visibility.

## Remaining work

Correct identity is necessary before any further live serialization or sight test is meaningful. The outstanding requirement is still to find a delivered, fresh, entity-level signal for the actual local hero in a normal non-host session. Candidate validation needs independent time-aligned sight labels; none of the existing terrain, inventory or replay-callback observations replaces that requirement. The other person's host remains unmodified.
