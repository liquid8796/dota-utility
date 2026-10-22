"""Verify the installed server's visibility publication routes, offline only.

No debugger, DLL loading, game calls, packet manipulation or role changes.
A successful audit is NOT proof that the owner's normal client receives enemy sight.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import struct
from pathlib import Path
from typing import Any

from audit_visibility_registration import Image
# Keep this profile independent from other audit modules' future build updates.
BUILDS = {
    "client.dll": "88fa24c60a8c68f84d25cc2285019889c05db0c55b2bed1165d6f98fa7c18215",
    "server.dll": "02b90bbb5814bc1af39cada58b6b8311808e01e84aa8fab894bdaea896a8cd47",
}

CODE = {
    "server.dll": [
        ("new server visibility mask", 0x24FB3F2, "8b8344050000"),
        ("previous published mask", 0x24FB3F8, "8b93d40e0000"),
        ("newly visible teams only", 0x24FB3FE, "f7d223d0"),
        ("store new published mask", 0x24FB414, "4489b3d40e0000"),
        ("changed teams include gains and losses", 0x24FB41B, "4433f0"),
        ("team bitmap word source", 0x24FB559, "4a8b8ce1000d0000"),
        ("new team's current bit", 0x24FB575, "85bbd40e0000"),
        ("team-data bitmap field address", 0x24FB582, "498d8a000d0000"),
        ("bitmap setter skips unchanged word", 0x24E8F63, "493b00"),
        ("notify bitmap field D00", 0x24E9020, "c700000d0000"),
        ("network change virtual dispatch", 0x24E9029, "ff90e0000000"),
        ("store changed bitmap word", 0x24E9090, "488b07488906"),
        ("modifier gained-team mask must be nonzero", 0x16AF790, "85d2"),
        ("skip pending modifier removals", 0x16AF7E6, "f6879e00000004"),
        ("per-team modifier recipient predicate", 0x16AF808, "ff90b8000000"),
        ("remove rejected recipient bit", 0x16AF812, "0fb3de"),
        ("force modifier publication", 0x16AF81C, "41b0018bd6488bcf"),
        ("modifier publication has nonzero-mask guard", 0x16AEDC8, "85ff"),
        ("dirty flag cleared after publication", 0x24FCB4C, "c6864905000000"),
    ],
    "client.dll": [
        ("game manager vtable assigned", 0x15BEF24, "488901"),
        ("forced FoW result tests blocking modifier", 0x14FC0E8, "4885c07411"),
        ("blocked forced FoW result is zero", 0x14FC0ED, "897b04"),
        ("unblocked forced FoW result is one", 0x14FC0FE, "c743040000803f"),
    ],
}
CALLS = [
    ("mask update publishes changes", "server.dll", 0x24FCB47, 0x24FB3E0),
    ("gained-team modifier path", "server.dll", 0x24FB402, 0x16AF790),
    ("filtered modifier publication", "server.dll", 0x16AF824, 0x16AED80),
    ("modifier payload construction", "server.dll", 0x16AEE2A, 0x16A8010),
    ("bitmap field setter", "server.dll", 0x24FB58E, 0x24E8F40),
    ("forced FoW fallback query", "server.dll", 0x24FCAB1, 0x16AC770),
    ("forced FoW blocking-modifier lookup", "client.dll", 0x14FC0D5, 0x1AD0E60),
]
TYPES = [
    ("server.dll", 0x3FA0E80, ".?AVCDOTA_BaseNPC@@", 268, 0x24FC840),
    ("server.dll", 0x3FA2A80, ".?AVCDOTA_BaseNPC_Hero@@", 268, 0x24FC840),
    ("server.dll", 0x2FDBDE8, ".?AVCDOTA_Unit_Hero_Mirana@@", 268, 0x24FC840),
    ("client.dll", 0x4741C08, ".?AVC_DOTAGameManager@@", None, None),
    ("client.dll", 0x4744D80, ".?AVC_DOTAGameManagerProxy@@", None, None),
]
REGIONS = [
    ("server.dll", "full entity mask updater", 0x24FC840, 0x24FCB59),
    ("server.dll", "publication and team-data update", 0x24FB3E0, 0x24FB640),
    ("server.dll", "gained-team modifier filter", 0x16AF790, 0x16AF858),
    ("server.dll", "networked bitmap word setter", 0x24E8F40, 0x24E90A7),
    ("client.dll", "forced FoW property callback", 0x14FBFE0, 0x14FC113),
]


def transition_masks(previous: int, current: int) -> dict[str, int]:
    """Model of the verified 15-team bit operations, not a live sight reader."""
    if type(previous) is not int or type(current) is not int:
        raise ValueError("Masks must be integers.")
    if not 0 <= previous < (1 << 15) or not 0 <= current < (1 << 15):
        raise ValueError("Masks must fit the audited 15-team domain.")
    return {"gained": (~previous) & current, "lost": previous & (~current),
            "changed": previous ^ current}


def verify_code(image: Image) -> list[dict[str, Any]]:
    if image.name not in CODE:
        raise ValueError("Unsupported module.")
    rows = []
    for meaning, rva, raw in CODE[image.name]:
        expected = bytes.fromhex(raw)
        if image.read(rva, len(expected)) != expected:
            raise ValueError(f"Changed code contract: {image.name}+{rva:X} ({meaning}).")
        rows.append({"meaning": meaning, "rva": hex(rva), "bytes": raw})
    return rows


def verify_calls(image: Image) -> list[dict[str, Any]]:
    if image.name not in CODE:
        raise ValueError("Unsupported module.")
    rows = []
    for meaning, module, rva, target in CALLS:
        if module != image.name:
            continue
        raw = image.read(rva, 5)
        if len(raw) != 5 or raw[0] != 0xE8:
            raise ValueError(f"Expected direct call at {rva:X}.")
        actual = rva + 5 + struct.unpack_from("<i", raw, 1)[0]
        if actual != target:
            raise ValueError(f"Call target changed at {rva:X}.")
        rows.append({"meaning": meaning, "rva": hex(rva), "targetRva": hex(target), "bytes": raw.hex()})
    return rows


def verify_types(image: Image) -> list[dict[str, Any]]:
    if image.name not in CODE:
        raise ValueError("Unsupported module.")
    rows = []
    for module, vtable, name, slot, target in TYPES:
        if module != image.name:
            continue
        if image.type_name(vtable) != name:
            raise ValueError(f"Unexpected type at vtable {vtable:X}.")
        if slot is not None and image.u64(vtable + slot * 8) - image.base != target:
            raise ValueError(f"Changed visibility method for {name}.")
        rows.append({"name": name, "vtableRva": hex(vtable), "slot": slot,
                     "methodRva": hex(target) if target is not None else None})
    return rows


def audit(directory: Path) -> dict[str, Any]:
    images = {name: Image(directory / name, supported=BUILDS) for name in CODE}
    modules = [{"name": i.name, "sha256": i.digest, "code": verify_code(i),
                "calls": verify_calls(i), "types": verify_types(i)} for i in images.values()]
    client = images["client.dll"]
    if client.lea_rax_target(0x15BEF1D) != 0x4741C08:
        raise ValueError("Game manager constructor changed.")
    if client.read(0x14FC0CE, 7) != bytes.fromhex("488d15eb1da102"):
        raise ValueError("Blocking-modifier reference changed.")
    blocker = client.text(0x3F0DEC0)
    if blocker != "modifier_slark_shadow_dance":
        raise ValueError("Blocking-modifier identity changed.")
    regions = [{"module": name, "meaning": meaning, "startRva": hex(start),
                "endRvaExclusive": hex(end),
                "sha256": hashlib.sha256(images[name].read(start, end-start)).hexdigest()}
               for name, meaning, start, end in REGIONS]
    return {
        "schemaVersion": 1,
        "scope": "Installed DLLs only. Static publication route, not packet delivery or enemy-view ground truth.",
        "modules": modules, "regions": regions, "forcedFowBlocker": blocker,
        "hypotheticalTransitions": {
            "enterEnemySight": transition_masks(4, 12),
            "leaveEnemySight": transition_masks(12, 4),
            "unchanged": transition_masks(12, 12),
        },
        "interpretation": {
            "teamBitmapCarriesBothDirections": True,
            "modifierPathInputIsOnlyNewlyVisibleTeams": True,
            "modifierRecipientsAreFilteredPerTeam": True,
            "modifierPublicationPulseIsCurrentOwnerSight": False,
            "normalOwnerClientDeliveryVerified": False,
            "allPossibleClientChannelsExcluded": False,
            "currentHeroVisibility": "Unknown", "canDriveCurrentOverlay": False,
        },
        "limitations": [
            "A server recipient bit is not evidence the hero owner receives that team's bitmap.",
            "Modifier serialization has additional gates; its input mask is not claimed to be a wire field.",
            "Forced-FoW and true-sight properties have separate semantics and conditional blockers.",
            "No addon was loaded or executed by this audit; Arcade script behavior must be separately scoped.",
        ],
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("binary_directory", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args(argv)
    try:
        result = audit(args.binary_directory)
        payload = json.dumps(result, indent=2, allow_nan=False) + "\n"
        if args.output:
            with args.output.open("x", encoding="utf-8") as stream:
                stream.write(payload)
        else:
            print(payload, end="")
        return 0
    except (OSError, ValueError, struct.error) as error:
        parser.exit(2, f"AUDIT_REJECTED: {error}\n")


if __name__ == "__main__":
    raise SystemExit(main())

