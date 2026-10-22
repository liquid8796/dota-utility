# Initialized registered-field audit — 2026-09-29

## Scope and actual result

The research CLI tools/DotaUtility.RegistryAudit reads initialized field-registration objects, not only reflected schema names. It follows the corrected native local-controller implementation in 1.6.2. This audit does not provide or certify a universal non-host enemy-sight detector. Production detector semantics are unchanged by the research-tool release.

All process access is Read/Write. The server.dll investigation reads the installed module in the user's own process; it is not an inspection of the other person's host or its packet stream.

| Audited module | Classes including resolved parents | Direct registered fields |
| --- | ---: | ---: |
| client.dll | 14 | 328 |
| server.dll | 11 | 193 |

The client roots are C_DOTA_BaseNPC_Hero, C_DOTAPlayerController, DataTeamPlayer_t and C_DOTA_DataNonSpectator. The server roots are CDOTA_BaseNPC, CDOTA_BaseNPC_Hero and CDOTA_DataNonSpectator. Parent graphs are traversed explicitly. This does not exhaust every arbitrary subclass, nested state, specialized message or alternate serializer.

## Evidence files

The complete pointer-redacted metadata and summaries are in docs/evidence/registered-fields-client-2026-09-29.json, registered-fields-server-2026-09-29.json, registered-fields-summary-2026-09-29.json, and owner-context-sample-2026-09-29.json. Full module hashes are embedded and checked before reading pinned research layouts. Different builds are rejected.

## Positive controls and named-mask results

| Control field | Client offset | Server offset | Normalized type / encoder |
| --- | --- | --- | --- |
| m_iTeamNum | 0x3E7 | 0x344 | uint8 |
| m_nUnitState64 | 0x11C0 | 0xE20 | uint64 |
| Hero m_iPlayerID | 0x1A68 | 0x1A08 | PlayerID_t |
| m_bNPCVisibleState | 0xE58 | 0xD00 | uint64[256] / fixed64 |

Neither m_iTeamVisibilityBitmask nor m_iTaggedAsVisibleByTeam appears in the ordinary named-field lists of the captured class graphs, despite the known control fields being present and coherent. This is bounded registration evidence, not proof that every possible source of useful client information is absent.

Client registration at RVA 0x1A5993B names m_bNPCVisibleState, supplies in-object offset 0xE58 and array byte size 0x800, and registers against class descriptor 0x61CECC8. A later lookup/store at 0x1A5B5D1 / 0x1A5B5F2 selects fixed64. The analogous server path at 0x1E81876 uses offset 0xD00 and class descriptor 0x501C498; encoder assignment is at 0x1E8347B.

The client path at 0x1AE275C instead constructs a separate compatibility entry mapping m_iTaggedAsVisibleByTeam to OnReplayComap_TaggedAsVisibleByTeam. The spelling Comap is literal. This remains distinct from a verified live enemy-sight feed.

## Bounded registry traversal

Class descriptors hold the direct field count at +0x10, field-pointer array at +0x18, parent count at +0xB0 and parent-pointer array at +0xB8. Field descriptors hold names at +0x08, declaring class at +0x30, element size at +0x38, in-object offset at +0x3C and encoder name at +0x70. Native client code at 0x19F792A..0x19F7934 increments the count and appends the new descriptor to that array.

The CLI bounds pointer arithmetic, depth, class counts, field counts and string reads. It rejects duplicate classes/fields, cycles, required-read failures, declaring-class mismatch and changing array headers. This is not atomic game-frame snapshot isolation. Raw process pointers are omitted from exported evidence.

## Recipient path independently rechecked

The Radiant/Dire policy methods at server 0x1EB8E80 / 0x1EB8DC0 occupy vtable slot 94. At 0x1BBFF8A the caller invokes that slot, tests result bit 4 and takes a path calling slot 95 at 0x1BBFFA3. A pure result of 8 has neither bit 4 nor bit 0x10 and exits this examined branch without calling slot 95. The base membership code at 0x1B732E0..0x1B732E2 ORs an entity bit into the output bitset.

This links raw policy return values to inclusion on the inspected path. It is not a capture of the remote host's actual packet or every role-specific branch. No audience/role flags, subscriptions or teams were modified.

## Current local-context observation

An 80-sample bounded series observed the native local Sniper on Radiant: controller #7, Dota PlayerID 2, network slot 6, one continuous full-handle identity segment. The hero moved. Own Radiant data was present in 80/80 samples and opposing Dire data in 0/80. The legacy hero mask remained 0.

No independently time-aligned enemy-view labels were recorded. These are identity/availability observations, not an out/in/out sight test. Missing data remains Unknown, not Hidden. Own-team visibility must not substitute for enemy-team visibility.

## Reproduce and verify

Run the following from the repository root while Dota and the audited local modules are loaded:

    dotnet run --project tools/DotaUtility.RegistryAudit -c Release -- client.dll
    dotnet run --project tools/DotaUtility.RegistryAudit -c Release -- server.dll

Save stdout as UTF-8 JSON, then verify with:

    python scripts/research/verify_registered_fields.py client-registry.json server-registry.json
    python -m unittest discover -s tests/research -p "test_registered_field_audit.py" -v

The verifier requires complete parent graphs and positive controls before reporting named-field presence. Malformed/incomplete captures and unknown builds are rejected. Valid reports still explicitly state perPacketDeliveryVerified=false and enemySightVerified=false. The CLI does not produce a hero visibility result and is not invoked by the application.

## Acceptance gate still open

The requested green ring requires a fresh, available and correctly attributed signal for the opposing team seeing the actual local hero. These paths have not provided a verified universal signal in the tested normal-client context. Do not reactivate terrain FoW, the replay callback, own-team bitmaps or arbitrary item bytes merely because a read succeeds. New candidates need semantic and independently labelled live validation.

## Validation of this research release

The isolated full .NET regression run passed 90 tests (5 Memory, 9 Application, 70 Dota2, 6 App). The standalone capture CLI built and ran against both audited local modules. Eighteen offline verifier tests passed, including incomplete graphs, unknown hashes, changed controls and incorrect encoder metadata. A full Release build reported zero warnings and zero errors. These tests establish tooling/fixture behavior; they do not validate real enemy sight or the overlay.
