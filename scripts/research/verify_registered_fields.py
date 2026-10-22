"""Validate a bounded registry capture and report named-field presence, not enemy sight.

Usage: python scripts/research/verify_registered_fields.py <capture.json> [capture.json ...]
The input is produced by tools/DotaUtility.RegistryAudit. This verifier never attaches
 to a process. A successful result does not certify per-packet delivery or hero reveal.
"""
from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

from audit_visibility_registration import SUPPORTED

ROOTS = {
    "client.dll": ("C_DOTA_BaseNPC_Hero", "C_DOTAPlayerController", "DataTeamPlayer_t", "C_DOTA_DataNonSpectator"),
    "server.dll": ("CDOTA_BaseNPC", "CDOTA_BaseNPC_Hero", "CDOTA_DataNonSpectator"),
}
MASK_NAMES = ("m_iTeamVisibilityBitmask", "m_iTaggedAsVisibleByTeam")


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)


def load_capture(path: Path) -> dict:
    require(path.stat().st_size <= 2_000_000, "Capture exceeds the size limit")
    data = json.loads(path.read_text(encoding="utf-8-sig"))
    require(isinstance(data, dict), "Expected an object capture")
    return data


def validate_capture(data: dict) -> dict:
    require(data.get("schemaVersion") == 1, "Unsupported capture schema")
    module = data.get("module")
    require(module in ROOTS, "Unsupported module")
    require(data.get("sha256") == SUPPORTED[module], "Unknown binary hash")
    nodes = data.get("nodes")
    require(isinstance(nodes, list) and 1 <= len(nodes) <= 64, "Invalid node count")
    classes: dict[str, dict] = {}
    for node in nodes:
        require(isinstance(node, dict), "Invalid class node")
        name = node.get("name")
        require(isinstance(name, str) and 1 <= len(name) <= 511, "Invalid class name")
        require(name not in classes, "Duplicate class name")
        classes[name] = node
        fields = node.get("fields")
        count = node.get("fieldCount")
        require(isinstance(fields, list) and type(count) is int and 0 <= count <= 512,
                "Invalid field list or count")
        require(len(fields) == count, "Incomplete field list")
        seen = set()
        for index, field in enumerate(fields):
            require(isinstance(field, dict), "Invalid field descriptor")
            field_name = field.get("name")
            require(isinstance(field_name, str) and 1 <= len(field_name) <= 511, "Invalid field name")
            require(field_name not in seen, "Duplicate direct field name")
            seen.add(field_name)
            require(field.get("index") == index, "Field index mismatch")
            require(field.get("declaringClass") == name, "Declaring class mismatch")
            offset = field.get("offset")
            require(isinstance(offset, str) and re.fullmatch(r"[0-9A-Fa-f]{1,8}", offset) is not None,
                    "Invalid offset")
            require(int(offset, 16) <= 0x1000000, "Offset exceeds audit bound")
            require(type(field.get("size")) is int and 0 <= field["size"] <= 0x1000000, "Invalid field size")
            for key in ("cppType", "normalizedType", "baseType", "encoder"):
                require(isinstance(field.get(key), str) and len(field[key]) <= 511, "Invalid type/encoder string")
        parents = node.get("parents")
        require(isinstance(parents, list) and len(parents) <= 16
                and all(isinstance(p, str) for p in parents), "Invalid parent list")
        require(len(set(parents)) == len(parents), "Duplicate parent")
    require(all(name in classes for name in ROOTS[module]), "Missing audited root class")
    for node in nodes:
        require(all(p in classes for p in node["parents"]), "Missing parent class")

    active: set[str] = set()
    visited: set[str] = set()

    def visit(name: str, depth: int = 0) -> None:
        require(depth <= 16, "Parent depth exceeds audit bound")
        require(name not in active, "Class graph cycle")
        if name in visited:
            return
        active.add(name)
        for parent in classes[name]["parents"]:
            visit(parent, depth + 1)
        active.remove(name)
        visited.add(name)

    for root in ROOTS[module]:
        visit(root)
    require(len(visited) == len(classes), "Unreachable class in capture")

    client = module == "client.dll"
    base = "C_BaseEntity" if client else "CBaseEntity"
    npc = "C_DOTA_BaseNPC" if client else "CDOTA_BaseNPC"
    hero = "C_DOTA_BaseNPC_Hero" if client else "CDOTA_BaseNPC_Hero"
    team = "C_DOTA_DataNonSpectator" if client else "CDOTA_DataNonSpectator"
    controls = [
        (base, "m_iTeamNum", 0x3E7 if client else 0x344, "uint8", ""),
        (npc, "m_nUnitState64", 0x11C0 if client else 0xE20, "uint64", ""),
        (hero, "m_iPlayerID", 0x1A68 if client else 0x1A08, "PlayerID_t", ""),
        (team, "m_bNPCVisibleState", 0xE58 if client else 0xD00, "uint64[256]", "fixed64"),
    ]
    checked = []
    for owner, name, offset, normalized, encoder in controls:
        require(owner in classes, "Missing control class")
        matches = [f for f in classes[owner]["fields"] if f["name"] == name]
        require(len(matches) == 1, f"Missing control field {owner}.{name}")
        field = matches[0]
        require(int(field["offset"], 16) == offset and field["normalizedType"] == normalized
                and field["encoder"] == encoder, f"Control metadata mismatch: {owner}.{name}")
        checked.append({"class": owner, "field": name, "offset": hex(offset),
                        "normalizedType": normalized, "encoder": encoder})

    presence = {name: [n["name"] for n in nodes if any(f["name"] == name for f in n["fields"])]
                for name in MASK_NAMES}
    return {
        "module": module,
        "sha256": data["sha256"],
        "classCount": len(nodes),
        "registeredFieldCount": sum(n["fieldCount"] for n in nodes),
        "positiveControls": checked,
        "namedMaskClasses": presence,
        "interpretation": "Named-field registration evidence only; no live enemy-sight result is inferred",
        "perPacketDeliveryVerified": False,
        "enemySightVerified": False,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("captures", type=Path, nargs="+")
    args = parser.parse_args()
    try:
        reports = [validate_capture(load_capture(path)) for path in args.captures]
        print(json.dumps({"schemaVersion": 1, "reports": reports}, indent=2))
        return 0
    except (ValueError, OSError, TypeError, KeyError) as error:
        print(f"AUDIT_REJECTED: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
