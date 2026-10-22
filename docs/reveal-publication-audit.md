# Visibility publication and active Arcade context audit

## Release and scope

Version 1.6.7 adds an offline verifier for the installed server's visibility
publication routes. It does not add a new production visibility provider and it
does not enable the green ring from unverified signals. Normal non-host enemy
sight, including ward-only transitions, is still unverified.

The preceding modifier-source changes were committed separately as 1.6.6
(commit 2f3cf03). This work preserves those changes and uses separate audit files.

The exact DLL hashes, instruction bytes, branch targets and RTTI checks are in
[the native evidence](evidence/reveal-publication-native-2026-09-30.json).
All addresses below are RVAs in that exact installed build, not stable offsets
for an arbitrary update. The verifier refuses an unsupported hash.

## 1. Actual entity visibility publication

The server's CDOTA_BaseNPC, CDOTA_BaseNPC_Hero and Mirana vtables resolve method
slot 268 to server.dll + 0x24FC840. The per-team query invokes this updater when
its visibility cache is dirty. The updater evaluates several gates, including
terrain sight and forced-FoW properties; it finishes by calling 0x24FB3E0.

In that publisher, the mask at entity + 0x544 is compared with the previously
published mask at +0xED4. Two different differences are computed:

    gained = current & ~previous
    changed = current ^ previous

The first goes to the modifier publication path. The second controls updates to
the appropriate team-data bitmap, including both sight gains and losses.

The verified team-data path computes the hero's bit, calls the bitmap setter at
0x24E8F40, marks field 0xD00 as changed through the entity's network-change
interface, and stores the new 64-bit word. A changed server field does not prove
that the local hero's owner receives the opposing team's data entity.

This confirms the server-side producer for the team-data approach while leaving
the recipient constraint intact. A missing enemy-team data object remains
unavailable; it must not be reinterpreted as Hidden.

## 2. Why a modifier update is not a complete sight state

The call at 0x24FB402 passes only gained teams to 0x16AF790. That function visits
the current modifier list, skips pending removals, and filters each team through
the modifier's virtual recipient predicate at vtable + 0xB8. It then calls
0x16AED80 with the remaining mask and a force-publication flag.

The latter function has further eligibility and duration gates before building
a modifier payload through 0x16A8010. This audit does not claim that the input
mask is itself serialized as a wire field, nor that an owner's client receives
another recipient's publication.

A concrete, hypothetical bit example is:

| Previous mask | Current mask | Gained teams | Changed teams |
| --- | --- | --- | --- |
| 0x04 | 0x0C | 0x08 | 0x08 |
| 0x0C | 0x04 | 0x00 | 0x08 |
| 0x0C | 0x0C | 0x00 | 0x00 |

The second row has a sight loss but no gained-team publication input. Therefore
an arrival pulse, an unchanged modifier list, or silence on this path cannot be
treated as a continuously valid owner-visible/owner-hidden answer. These rows
are a model of verified instructions, not labelled live game observations.

## 3. Forced FoW is conditional too

The separate client forced-FoW property callback at 0x14FBFE0 checks context and
a blocking modifier before returning its result. The verified blocking name is
modifier_slark_shadow_dance. The result branches at 0x14FC0E8 return zero when
that blocker exists and one otherwise, within the already-selected property
path. The server entity updater's forced-FoW fallback also has additional gates.

This is not a license to infer enemy sight from the name of a true-sight modifier.
Source team, parent, effective-team rules, active/removal state and the engine's
remaining predicates all matter. Nothing in this audit invokes those game
functions or turns modifier presence into a production visibility snapshot.

## 4. The actual Arcade addon was identified natively

A bounded read of the current C_DOTAGameManagerProxy resolved the game manager,
validated both RTTI types and the full proxy handle, reread the mode strings,
and checked the native local-controller/hero identity around the read.

The recorded game manager reports CustomGame = 1, map dota, and an addon path
ending in workshop/content/570/3320645051/3320645051.vpk. The literal addon field
is a full local package path, not just the numeric Workshop ID.

[The active-addon evidence](evidence/active-addon-reveal-audit-2026-09-30.json)
retains the observed timestamp, player/team/full hero handle, package hash and
selected member fingerprints. The identity is the one in that observation;
it is not borrowed from the older Riki or Mirana modifier traces.

The local package contains encrypted module wrappers and its own corresponding
loader. Its 353 wrapped Lua modules were decoded strictly as offline data into
ignored scratch storage; neither the loader nor the decoded Lua was executed.
No game package, active script VM, host installation or network subscription was
changed. Full shipped sources, content keys and service payloads are not included
in this repository evidence.

The package's declared custom tables are common, service, player_data and
treasure. Direct sight-query tokens and selected synchronization call sites were
reviewed, but no owner enemy-sight heartbeat was established in that surface.
This is not whole-program alias resolution and is not a proof that every possible
addon or native data path is absent. The host's loaded runtime can also differ
from the local package.

One concrete correction is supported by the reviewed source:
modifier_zhenshibaoshi_xianshi is a Gem display modifier, with UI/lifetime methods
and item_gem as its texture. That class does not itself query opposing-team sight.
This conclusion does not describe every behavior of the native Gem item.

The helper mz_modifier_provides_vision sets a unit-state flag; finding its
definition is not evidence of a client heartbeat reporting who sees the hero.

## Reproduce the native verifier

Run from the repository root against the exact audited DLL pair:

    python scripts/research/audit_reveal_publication.py "D:/Program Files (x86)/Steam/steamapps/common/dota 2 beta/game/dota/bin/win64" --output .scratch/reveal-publication-new.json

The output path must not already exist. The script uses Python's standard
library and existing Read/Write PE helpers. Unknown hashes, altered contracts,
wrong RTTI/method slots and changed calls fail before output publication. There
is no skip-build-check option. Code after split unwind entries is included in
the saved region fingerprints instead of stopping at the first short fragment.

Run the focused tests:

    python -m unittest discover -s tests/research -p test_reveal_publication.py

The package findings are an inspected local-source record, not a new runtime
addon integration. Their fingerprints distinguish this package from another
Workshop version; they do not certify an unobserved host package.

## Remaining acceptance gate

A useful normal-client provider still needs independently labelled enemy-view
truth for the actual local hero, matching full handles, and both out/in/out
transitions without combat. Ward-only sight, invisibility/true-sight interaction,
team/hero changes and returning to the dashboard must be tested separately.
The current release does not claim those gates have passed.

