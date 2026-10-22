"""Offline, hash-pinned owner-modifier causality audit. Never executes game code."""
from __future__ import annotations

import argparse
import hashlib
import json
import struct
from pathlib import Path
from typing import Any

from audit_visibility_registration import Image

BUILDS = {
    "client.dll": "88fa24c60a8c68f84d25cc2285019889c05db0c55b2bed1165d6f98fa7c18215",
    "server.dll": "02b90bbb5814bc1af39cada58b6b8311808e01e84aa8fab894bdaea896a8cd47",
}
# Instruction contracts were rederived from this exact pair, not shifted by a
# guessed whole-module delta. Runtime-function/unwind entries may split a method.
CODE = {
    "server.dll": [
        ("desired-state comparison", 0x137C2A6, "403af5"),
        ("true desired state", 0x137C29E, "40b601"),
        ("false desired state", 0x137C2A3, "4032f6"),
        ("activation delay", 0x137C2E8, "f30f1097781a0000"),
        ("deadline storage", 0x137C306, "f30f11878c1a0000"),
        ("neutral team check", 0x1473D92, "80b94403000004"),
        ("neutral disable duration", 0x1473D9B, "f30f108e7c1a0000"),
        ("neutral timer address", 0x1473DA3, "488d8e901a0000"),
        ("unit-state mask source", 0x18CB572, "8b83200e0000"),
        ("unit-state bit 30", 0x18CB578, "48c1e81e2401"),
        ("requested regeneration state", 0x147F089, "84db745a"),
        ("linger duration", 0x147F11B, "f3410f1086841a0000"),
        ("per-team visibility mask", 0x1A000A2, "8b8344050000"),
        ("per-team bit selection", 0x1A000AF, "d3e723c73bc7"),
    ],
    "client.dll": [
        ("passive update is no-op", 0x12EF190, "c20000"),
        ("neutral-hit callback is no-op", 0x1370F10, "33c048c702010000004889420848894210488bc2c3"),
        ("modifier manager getter", 0x14FB5D0, "488b4130c3"),
        ("ability handle", 0x14FA830, "8b5170"),
        ("ability full-handle guard", 0x14FA878, "395010"),
        ("parent handle", 0x14D9650, "8b5174"),
        ("parent full-handle guard", 0x14D9693, "395010"),
        ("native instance token", 0x14FF180, "8b4148c3"),
        ("true-sight cache validity first", 0x14FFB6E, "6685ae7c030000"),
        ("modifier array count", 0x14FFB8F, "48634610"),
        ("modifier array pointer", 0x14FFBB0, "488b4618"),
        ("skip pending removals", 0x14FFBBD, "f6819e00000004"),
        ("per-modifier property virtual dispatch", 0x14FFBD1, "ff9000030000"),
        ("cache becomes valid", 0x14FFBF8, "6609ae7c030000"),
        ("cache result", 0x14FFC04, "6685ae7a030000"),
    ],
}
BRANCHES = [
    ("passive to opposing-team query", 0x137C26E, 0xE8, 0x2472530),
    ("opposing-team query to mask primitive", 0x2472580, 0xE8, 0x19FFFD0),
    ("passive unit-state condition", 0x137C285, 0xE8, 0x18CB540),
    ("delayed regeneration state change", 0x137C335, 0xE8, 0x147F030),
    ("neutral suppression state change", 0x137C261, 0xE8, 0x147F030),
    ("neutral damage starts timer", 0x1473DAA, 0xE8, 0xD50EF0),
    ("passive starts periodic work", 0x137BF23, 0xE9, 0x16B0020),
]


def verify_code(image: Image) -> list[dict[str, Any]]:
    if image.name not in CODE:
        raise ValueError("Unsupported audit module.")
    rows = []
    for meaning, rva, expected in CODE[image.name]:
        if image.read(rva, len(bytes.fromhex(expected))) != bytes.fromhex(expected):
            raise ValueError(f"Native contract changed: {image.name}+{rva:X}: {meaning}")
        rows.append({"meaning": meaning, "rva": hex(rva), "bytes": expected})
    return rows


def verify_branches(image: Image) -> list[dict[str, Any]]:
    if image.name != "server.dll":
        raise ValueError("Branch contracts belong to server.dll only.")
    rows = []
    for meaning, rva, opcode, target in BRANCHES:
        raw = image.read(rva, 5)
        actual = rva + 5 + struct.unpack_from("<i", raw, 1)[0]
        if raw[0] != opcode or actual != target:
            raise ValueError(f"Native branch changed at {rva:X}.")
        rows.append({"meaning": meaning, "instructionRva": hex(rva), "targetRva": hex(target), "bytes": raw.hex()})
    return rows


def verify_type_method(image: Image, vtable: int, slot: int, target: int) -> dict[str, Any]:
    expected = ".?AVCDOTA_Modifier_Slark_ShadowDance_Passive@@"
    if image.type_name(vtable) != expected or image.u64(vtable + slot * 8) - image.base != target:
        raise ValueError("Passive modifier RTTI or method slot changed.")
    return {"nativeType": expected, "vtableRva": hex(vtable), "slot": slot, "methodRva": hex(target)}


def audit(directory: Path) -> dict[str, Any]:
    client = Image(directory / "client.dll", supported=BUILDS)
    server = Image(directory / "server.dll", supported=BUILDS)
    client_checks = verify_code(client)
    server_checks = verify_code(server)
    branches = verify_branches(server)
    methods = [verify_type_method(server, 0x35C7F90, 83, 0x137C1C0),
               verify_type_method(client, 0x438AE48, 74, 0x12EF190)]
    if server.text(0x35CA098) != "modifier_slark_shadow_dance_passive_regen":
        raise ValueError("Regeneration modifier identifier changed.")
    for image, instruction, expected in [(server, 0x137DAA0, 0x35C7F90), (client, 0x12EF5F0, 0x438AE48)]:
        if image.lea_rax_target(instruction) != expected:
            raise ValueError("Passive factory assignment changed.")
    return {
        "schemaVersion": 1, "scope": "Installed binaries only; no remote host or live packet capture.",
        "modules": [{"name": i.name, "sha256": i.digest} for i in [client, server]],
        "clientContracts": client_checks, "serverContracts": server_checks,
        "branches": branches, "passiveMethods": methods,
        "regenerationModifier": "modifier_slark_shadow_dance_passive_regen",
        "passiveUpdateRegion": {"startRva": "0x137c1c0", "endRvaExclusive": "0x137c438",
                                "sha256": hashlib.sha256(server.read(0x137C1C0, 0x278)).hexdigest(),
                                "note": "Includes code beyond the first short runtime-function/unwind entry."},
        "interpretation": {
            "serverConsultsOpposingTeamVisibility": True,
            "clientCounterpartComputesSamePredicate": False,
            "effectIsDelayedAndConditioned": True,
            "modifierPresenceIsInstantaneousVisibility": False,
            "cacheValidityRequired": True,
            "allHeroesSupportedByThisAbility": False,
            "liveSlarkGroundTruthVerified": False,
            "currentHeroVisibility": "Unknown", "canDriveCurrentOverlay": False,
        },
        "limitations": ["No blanket assertion about every serializer or client signal.",
                        "Missing/expired modifiers and invalid caches are not Hidden.",
                        "Neutral effects, unit state, delay and linger must not be ignored.",
                        "Static causal code does not prove normal-client delivery or a measured transition."],
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
            with args.output.open("x", encoding="utf-8") as handle:
                handle.write(payload)
        else:
            print(payload, end="")
        return 0
    except (OSError, ValueError, struct.error) as error:
        parser.exit(2, f"AUDIT_REJECTED: {error}\n")


if __name__ == "__main__":
    raise SystemExit(main())
