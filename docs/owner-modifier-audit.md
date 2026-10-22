# Owner modifier and enemy-visibility audit — 2026-09-30

## Result and scope

Version 1.6.5 follows a specific ability mechanic instead of inferring sight from terrain or arbitrary inventory bytes. The installed Slark passive implementation really consults an opposing-team visibility predicate on the server. Its corresponding client update method is a no-op. The observable regeneration modifier is a conditioned, delayed effect, not a universal instantaneous visibility flag.

The requested all-hero, non-host green ring is **not completed by this release**. The shipped detector is unchanged. A modifier list, an invisibility effect, or a true-sight property does not by itself establish that an opposing team currently sees the local hero.

## Build and reproducibility

This audit applies only to the individually verified September 30 pair:

| Module | SHA-256 |
| --- | --- |
| client.dll | 88fa24c60a8c68f84d25cc2285019889c05db0c55b2bed1165d6f98fa7c18215 |
| server.dll | 02b90bbb5814bc1af39cada58b6b8311808e01e84aa8fab894bdaea896a8cd47 |

Run `python scripts/research/audit_owner_modifiers.py "<game/dota/bin/win64>"` for the offline proof. It verifies native instruction contracts, signed branch targets, primary RTTI, method slots, factory assignments and the regeneration modifier identifier. Unknown binaries and changed contracts are rejected. Existing audit tools retain their older default build gates.

The committed proof is `docs/evidence/owner-modifier-native-2026-09-30.json`. It contains no absolute process pointers or full game binaries.

## Actual causal path

| Operation | Current module/RVA |
| --- | --- |
| Server passive primary vtable / update slot | server +0x35C7F90 / slot 83 |
| Server passive update region | +0x137C1C0 through +0x137C437 |
| Call to opposing-team query | +0x137C26E → +0x2472530 |
| Query's call to per-team mask primitive | +0x2472580 → +0x19FFFD0 |
| Delayed regeneration transition | +0x137C335 → +0x147F030 |
| Client passive primary vtable / counterpart | client +0x438AE48 / slot 74 → +0x12EF190 |

The client counterpart is `ret 0`. On the server, the update looks up `modifier_slark_shadow_dance_passive_regen`, applies a neutral-hit timer gate, then combines the opposing-team query with additional unit-state predicates. When the desired effect differs from the existing effect, it uses `activation_delay` and `m_fPendingStateChangeTime` before applying the change. The transition helper has further linger logic.

The per-team primitive consults the server entity mask at +0x544 after handling its dirty state. The opposing-team wrapper filters candidate teams and excludes the entity's own team and an additional native team predicate. This is server code in the locally installed binary, **not** a read of another player's host or proof that this mask is sent to the local player.

The first runtime-function/unwind entry for the passive update ends just after the null-parent guard. Stopping disassembly at that one entry misses the substantive conditions. The audit therefore verifies the known complete contiguous region and individual branch targets. It does not assume every `.pdata` entry equals a complete C++ function.

## Neutral damage is an independent confounder

The server's neutral-hit callback at +0x1473C80 resolves full victim/attacker handles, requires the victim to match the modifier parent, tests attacker team 4, then starts the countdown at modifier +0x1A90 using `neutral_disable` at +0x1A7C.

Its client counterpart at +0x1370F10 returns an empty event result. Therefore the reflected presence of a neutral timer member on both sides does not establish that the client locally runs this server logic or receives that member's complete state.

`m_bPendingRefresh` is not an enemy-seen bit: the refresh methods set it independently of an observed sight transition. The server creation/refresh methods store the ability's special values, while the effect decision is made by the separate update path.

No active Slark out/in/out experiment with independent enemy-view labels was performed. This source must not be enabled as a production enemy-sight detector from this static audit.

## Modifier list actually observed on the user's client

The initial bounded samples resolved the native local Riki: Dota PlayerID 4, Radiant, network slot 6, full hero handle `010705EC`. Subsequent prototypes read 25 and 27 modifier entries while retaining that full local identity. These included native effects and Lua modifiers such as the Riki awakening modifiers. They are observations of this client, not assertions about every mode or every modifier's packet provenance.

Each observed modifier pointed back to the hero's embedded manager and carried a parent handle matching the actual local hero. Its display identifier was independently readable even for `CDOTA_Modifier_Lua`, so Lua effects need not be collapsed into one anonymous class.

One useful negative control was `modifier_item_gem_of_true_sight`: its caster and parent both matched the local hero. Its name is not evidence of an enemy revealing the player. Other observed modifiers included invisibility and Radiance debuff; neither is promoted to an enemy-sight assertion.

The prototype's true-sight and FoW-property validity masks were zero. The final CLI correctly records inactive states when no native local controller/assigned hero is available; dashboard/loading data is not re-labelled Hidden. Raw prototype pointers stay in ignored scratch files. The pointer-redacted observation summary and final CLI evidence are stored with this document's evidence set.

## True-sight cache: validity comes first

Client getter +0x14FFB50 first tests the requested team's validity bit at manager +0x37C. If missing, it iterates the manager's list at +0x10/+0x18, skips pending-removal entries, and calls a per-modifier property at vtable byte offset +0x300. It then stores the result at +0x37A and marks the cache valid.

Thus a zero validity mask does not mean false; it means that no cached answer is valid for those teams. The research model returns null for an invalid team cache. Even a valid true-sight property is not a complete opposing-team terrain/entity-sight result.

## Read/Write owner-modifier CLI

`tools/DotaUtility.ModifierAudit` is a separate research executable, not an `IVisibilityProbe` and not an application dependency. It exports JSON lines with native local player/team/handle identity, modifier identifiers and classes, parent/caster/ability handles, native instance tokens, pending-removal flags, raw property caches and invisibility-effect values.

Run from the repo root:

```powershell
dotnet run --project tools/DotaUtility.ModifierAudit -c Release -- --samples 20 --output .scratch/owner-modifiers.jsonl
```

The output path must be new; it is never overwritten. Sampling is bounded to 1–300 samples, 200 ms apart, and supports Ctrl+C. It never creates a background watcher or changes a game command.

Before reading it checks the installed client hash and loaded native instruction contracts. Every sample uses the native primary local controller, not equality between Dota PlayerID and network slot. It validates the manager's owner, counts and allocation bounds; each entry's primary RTTI, identifier, manager back-pointer and full parent handle; and duplicate/native instance tokens. It rereads observed blocks and rechecks the full controller/hero/network context before exporting.

The native token and reread guards reduce stale-entry errors but are not atomic frame isolation or a proof against every allocation-reuse race. Unavailable caster/ability handles are preserved as raw handles; they are not silently mapped to a current slot occupant. No raw process pointers are exported.

Every report retains `currentHeroVisibility=Unknown` and `canDriveCurrentOverlay=false`. Empty lists, pending-removal effects, own-team items, absent properties and inactive matches do not become Hidden or Visible.

## Remaining gate

A production source still needs both semantic identity and independent live verification: the actual local hero, the correct enemy team, available/fresh data, and labelled enter/exit tests including a ward-only transition with no combat event. A Slark-specific conditioned effect cannot replace that gate for Riki or other heroes.

## Validation

The full .NET suite passed 156 tests (5 Memory, 9 Application, 136 Dota2, 6 App), including 43 new owner-modifier reader cases. The Python research suite passed 76 tests, including 15 new byte-contract/branch/RTTI/evidence tests. Invalid/duplicate CLI arguments and output overwrite attempts were rejected. Fixture tests validate acquisition and guards, not actual enemy sight or the ring renderer.

The guarded final CLI capture is `docs/evidence/owner-modifier-cli-2026-09-30.json`; the earlier pointer-redacted prototype is `docs/evidence/owner-modifier-prototype-2026-09-30.json`. The final capture contains {'start': 1, 'modifier_sample': 20, 'end': 1}. No inactive or discarded sample contributes a visibility result.

The final guarded capture obtained 20 accepted samples and no discarded/inactive samples on a new native local identity: Dota PlayerID 6, Radiant, hero handle 019E05D4, 14 modifiers in the initial sample. Earlier compiled smoke samples recorded an unavailable local controller; the final capture did not reuse the earlier Riki handle. The evidence deduplicates identical snapshots by SHA-256 while retaining every observation timestamp/context.

The complete normal Release build finished with zero warnings and zero errors. Formatting verification and whitespace checks passed. Assembly/File version is 1.6.5.0. These results certify the new observation tooling and its tested guards, not universal enemy-sight detection.


## 1.6.6 source-provenance continuation

The existing CLI now exports clientCachedTeamHint on each modifier and separately generation-checks unique caster references. It compares the owner modifier snapshots before and after source acquisition. Raw caster team, owner handle and effective team are not conflated; a missing source remains unavailable. See [modifier-source-provenance.md](modifier-source-provenance.md) and the two modifier-source evidence files from 2026-09-30.

This does not reinterpret the 1.6.5 captures retrospectively and does not change the production visibility probe or enable its ring. Native initialization can populate cached team from the parent when the caster cannot be resolved, and the effective-team virtual method can follow an owner or a custom game branch. The new fields are observations, not authoritative enemy-sight assertions.
