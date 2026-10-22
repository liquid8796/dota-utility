"""Hash-pinned modifier source provenance; not a current enemy-sight detector."""
from __future__ import annotations

import argparse
import hashlib
import json
import struct
from pathlib import Path
from typing import Any

from audit_owner_modifiers import BUILDS
from audit_visibility_registration import Image
from audit_combat_log_messages import extract_descriptor

# Each contract was located in this exact installed pair. No whole-module RVA shift.
CODE = [
    ("parent field decoder jump-table entry", 0xD076D0, "4667d000"),
    ("parent decoder checks protobuf tag 2/varint", 0xD06746, "4080fe10"),
    ("parent decoder stores parsed parent at +E0", 0xD06793, "418996e0000000"),
    ("consumer reads +E0 from message at stack+20", 0x15026B0, "8bbc2400010000"),
    ("initializer reads caster", 0x14D7BBC, "8b4e68"),
    ("initializer fallback reads parent", 0x14D7C58, "8b4e74"),
    ("initializer reads resolved entity raw team", 0x14D7CA5, "0fb687e7030000"),
    ("initializer stores local cached team", 0x14D7CB2, "898694000000"),
    ("TrueSight reads caster handle", 0x14E88E6, "448b4168"),
    ("TrueSight full handle check", 0x14E8937, "44394010"),
    ("TrueSight native NPC type check", 0x14E89AF, "80bad005000003"),
    ("TrueSight uses effective-team virtual method", 0x14E8A65, "ff90680b0000"),
    ("TrueSight compares requested team", 0x14E8A6B, "3bd80f94c0"),
    ("TrueSight fallback uses cached team", 0x14E8A8A, "413b9a940000000f94c0"),
    ("TrueSightFoW reads caster handle", 0x14E8C26, "448b4168"),
    ("TrueSightFoW full handle check", 0x14E8C77, "44394010"),
    ("TrueSightFoW effective-team virtual method", 0x14E8DA5, "ff90680b0000"),
    ("TrueSightFoW fallback cached team", 0x14E8DCA, "413b9a940000000f94c0"),
    ("TrueSightAll property constant true", 0x14E8F10, "b001c3"),
    ("gem aura item property default false", 0xF0AF80, "32c0c3"),
    ("NPC effective-team owner handle", 0x16418C6, "8b9114050000"),
    ("NPC owner full-handle check", 0x164190C, "395010"),
    ("NPC delegates effective team to owner", 0x1641937, "48ffa0680b0000"),
    ("NPC raw-team fallback", 0x1641990, "0fb683e7030000"),
    ("TrueSight common callback member", 0x14E88CB, "0f1181e0170000"),
    ("TrueSightFoW additional callback member", 0x14E8C08, "0f1181d0100000"),
    ("TrueSightFoW common callback member", 0x14E8C13, "0f1181e0170000"),
    ("FoW callback recipient/team context handle", 0x14FBFEF, "418b90c4000000"),
    ("FoW callback compares cached team", 0x14FC054, "413b8294000000"),
    ("FoW callback parent handle", 0x14FC07A, "418b4a74"),
    ("FoW callback zero property result", 0x14FC0ED, "897b04"),
    ("FoW callback one property result", 0x14FC0FE, "c743040000803f"),
    ("wire parent invalid sentinel", 0x15026B7, "81ffffffff00"),
    ("wire serial shift", 0x15026D2, "c1ef0e"),
    ("wire index mask", 0x15026D5, "81e3ff3f0000"),
    ("wire serial mask", 0x15026DB, "81e7ff030000"),
    ("runtime handle index mask", 0x150271B, "25ff7f0000"),
    ("identity flags read", 0x1502732, "8b441030"),
    ("identity flag adjustment", 0x1502736, "83e001c1e90f2bc8"),
    ("runtime serial reconstruction", 0x1502750, "c1e70f0bfb"),
]
# Prefix, displacement offset and complete instruction length are separate: do
# not parse a rel32 LEA as a CALL or confuse a slot address with its pointee.
REFERENCES = [
    ("missing caster to parent fallback", 0x14D7BC9, "0f84", 2, 6, 0x14D7C58),
    ("TrueSight absent caster fallback", 0x14E88F3, "0f84", 2, 6, 0x14E8A8A),
    ("TrueSightFoW absent caster fallback", 0x14E8C33, "0f84", 2, 6, 0x14E8DCA),
    ("TrueSight callback address", 0x14E88B4, "488d05", 3, 7, 0x1500D80),
    ("TrueSightFoW specific callback address", 0x14E8BE4, "488d05", 3, 7, 0x14FBFE0),
    ("TrueSightFoW common callback address", 0x14E8BF1, "488d05", 3, 7, 0x1500F40),
    ("FoW callback exclusion string", 0x14FC0CE, "488d15", 3, 7, 0x3F0DEC0),
    ("NPC effective-team alternate lookup", 0x1641985, "e8", 1, 5, 0x1FF3230),
]
METHODS = [
    ("client.dll", "CDOTA_Modifier_TrueSight", 0x4719DA0, 96, 0x14E88E0),
    ("client.dll", "CDOTA_Modifier_TrueSightFoW", 0x471A270, 96, 0x14E8C20),
    ("client.dll", "CDOTA_Modifier_TrueSightAll", 0x471A728, 96, 0x14E8F10),
    ("client.dll", "CDOTA_Modifier_Item_GemOfTrueSight", 0x44E14E0, 96, 0xF0AF80),
    ("client.dll", "C_DOTA_BaseNPC", 0x4872BE8, 365, 0x16418C0),
    ("client.dll", "C_DOTA_BaseNPC_Hero", 0x4873CE0, 365, 0x16418C0),
    ("client.dll", "CDOTAModifierBuffTableEntry", 0x3D5FEF8, 9, 0xD065D0),
    ("server.dll", "CDOTA_Modifier_TrueSight", 0x39EA678, 116, 0x1678430),
    ("server.dll", "CDOTA_Modifier_TrueSightFoW", 0x39EAD10, 116, 0x1678B80),
    ("server.dll", "CDOTA_Modifier_TrueSightAll", 0x39EB380, 116, 0x1679280),
]
REGIONS = [
    ("local cached-team initialization", 0x14D7A30, 0x2D1),
    ("TrueSight team property", 0x14E88E0, 0x1BA),
    ("TrueSightFoW team property", 0x14E8C20, 0x1BA),
    ("NPC effective-team getter", 0x16418C0, 0xDD),
    ("FoW-specific callback", 0x14FBFE0, 0x133),
    ("compressed parent-handle consumer", 0x15026B0, 0xD6),
]


def verify_code(image: Image) -> list[dict[str, Any]]:
    if image.name != "client.dll":
        raise ValueError("These instruction contracts belong to client.dll.")
    rows = []
    for meaning, rva, raw in CODE:
        expected = bytes.fromhex(raw)
        if image.read(rva, len(expected)) != expected:
            raise ValueError(f"Native source contract changed at {rva:X}: {meaning}.")
        rows.append({"meaning": meaning, "rva": hex(rva), "bytes": raw})
    return rows


def verify_references(image: Image) -> list[dict[str, Any]]:
    if image.name != "client.dll":
        raise ValueError("These reference contracts belong to client.dll.")
    rows = []
    for meaning, rva, prefix, displacement, size, target in REFERENCES:
        raw = image.read(rva, size)
        actual = rva + size + struct.unpack_from("<i", raw, displacement)[0]
        if not raw.startswith(bytes.fromhex(prefix)) or actual != target:
            raise ValueError(f"Source reference changed at {rva:X}.")
        rows.append({"meaning": meaning, "rva": hex(rva), "target": hex(target), "bytes": raw.hex()})
    return rows


def verify_methods(image: Image) -> list[dict[str, Any]]:
    if image.name not in BUILDS:
        raise ValueError("Unsupported source audit module.")
    rows = []
    for module, name, vtable, slot, method in METHODS:
        if module != image.name:
            continue
        expected = f".?AV{name}@@"
        if image.type_name(vtable) != expected or image.u64(vtable + slot * 8) != image.base + method:
            raise ValueError(f"RTTI/method changed for {name}.")
        rows.append({"module": module, "type": expected, "vtable": hex(vtable),
                     "slot": slot, "method": hex(method)})
    return rows


def audit(directory: Path) -> dict[str, Any]:
    client = Image(directory / "client.dll", supported=BUILDS)
    server = Image(directory / "server.dll", supported=BUILDS)
    code = verify_code(client)
    references = verify_references(client)
    methods = verify_methods(client) + verify_methods(server)
    exclusion = client.text(0x3F0DEC0)
    if exclusion != "modifier_slark_shadow_dance":
        raise ValueError("FoW callback exclusion identifier changed.")
    descriptor = extract_descriptor(client, "dota_modifiers.proto")
    fields = next(m["fields"] for m in descriptor["messages"] if m["name"] == "CDOTAModifierBuffTableEntry")
    return {
        "schemaVersion": 1,
        "scope": "Installed file contracts; no remote host access, game-code calls or packet capture.",
        "modules": [{"name": i.name, "sha256": i.digest} for i in (client, server)],
        "codeContracts": code, "references": references, "methods": methods,
        "regions": [{"meaning": name, "rva": hex(rva), "size": size,
                     "sha256": hashlib.sha256(client.read(rva, size)).hexdigest()} for name, rva, size in REGIONS],
        "fowCallbackExcludedModifier": exclusion,
        "buffDescriptor": descriptor,
        "namedBuffFields": [f["name"] for f in fields],
        "interpretation": {
            "cachedTeamCanComeFromParent": True,
            "rawCasterTeamEqualsNativeEffectiveTeam": False,
            "auraEmitterEqualsTargetTrueSightProperty": False,
            "fowSpecificCallbackIsConditional": True,
            "wireParentEqualsRuntimeFullHandle": False,
            "ordinaryBuffFieldCount": len(fields),
            "customValuesAndOtherMessagesExhausted": False,
            "liveEnemyViewGroundTruthVerified": False,
            "currentHeroVisibility": "Unknown", "canDriveCurrentOverlay": False,
        },
        "limitations": [
            "TrueSight and FoW properties do not by themselves certify final entity visibility.",
            "A missing caster is not authoritatively assigned the parent's or cached team.",
            "Effective team can depend on an owner and custom game behavior.",
            "The ordinary descriptor does not exhaust custom_int_values/custom_float_values or other channels.",
            "Static receive consumers do not prove a specific remote packet was delivered.",
        ],
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("binary_directory", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args(argv)
    try:
        payload = json.dumps(audit(args.binary_directory), indent=2, allow_nan=False) + "\n"
        if args.output:
            with args.output.open("x", encoding="utf-8") as output:
                output.write(payload)
        else:
            print(payload, end="")
        return 0
    except (OSError, ValueError, struct.error) as error:
        parser.exit(2, f"AUDIT_REJECTED: {error}\n")


if __name__ == "__main__":
    raise SystemExit(main())
