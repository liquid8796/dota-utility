"""Read/Write verification of the native local-controller accessor on an audited build.

Usage: python scripts/research/audit_local_controller.py <game/dota/bin/win64>
Uses only the Python standard library. Never attaches to or executes Dota.
"""
from __future__ import annotations

import argparse
import json
import struct
import sys
from pathlib import Path

from audit_visibility_registration import Image, verify

PREFIX = bytes.fromhex("33c083f9ff0f44c84863c1488d0d")
SUFFIX = bytes.fromhex("488b04c1c3")


def raw_rva(image: Image, offset: int) -> int:
    for rva, size, raw in image.sections:
        if raw <= offset < raw + size:
            return rva + offset - raw
    raise ValueError("Offset is outside file-backed sections")


def occurrences(data: bytes, needle: bytes):
    start = 0
    while (start := data.find(needle, start)) >= 0:
        yield start
        start += 1


def audit(directory: Path) -> dict:
    image = Image(directory / "client.dll")
    matches = []
    for offset in occurrences(image.data, PREFIX):
        if image.data[offset + 18:offset + 23] == SUFFIX:
            matches.append(raw_rva(image, offset))
    verify(len(matches) == 1, f"Expected one local-controller accessor, found {len(matches)}")
    accessor = matches[0]
    body = image.read(accessor, 23)
    array = accessor + 18 + struct.unpack_from("<i", body, 14)[0]
    verify(accessor == 0x1750840 and array == 0x619BEF8, "Accessor target differs from audited build")
    # An existing native client consumer explicitly passes primary split-screen index 0.
    caller = image.read(0x1B3FDB6, 7)
    verify(caller[:3] == bytes.fromhex("33c9e8"), "Primary-index call site changed")
    target = 0x1B3FDB8 + 5 + struct.unpack_from("<i", caller, 3)[0]
    verify(target == accessor, "Primary-index consumer calls a different function")

    fields = []
    for name, expected in [("m_bIsLocalPlayerController", 0x770), ("m_bIsHLTV", 0x6D0),
                           ("m_nPlayerID", 0x908), ("m_hAssignedHero", 0x90C)]:
        descriptors = set()
        for position in occurrences(image.data, name.encode("ascii") + b"\0"):
            pointer = struct.pack("<Q", image.base + raw_rva(image, position))
            for ref in occurrences(image.data, pointer):
                if ref % 8 == 0 and ref + 32 <= len(image.data):
                    if struct.unpack_from("<I", image.data, ref + 16)[0] == expected:
                        descriptors.add(raw_rva(image, ref))
        verify(len(descriptors) == 1, f"Expected one {name} descriptor at offset {expected:#x}")
        fields.append({"name": name, "offset": hex(expected), "descriptorRva": hex(next(iter(descriptors)))})

    return {"schemaVersion": 1, "method": "Offline hash, signature, call-site and field-descriptor checks",
            "module": image.name, "sha256": image.digest, "peTimestamp": image.timestamp,
            "accessorRva": hex(accessor), "accessorBytes": body.hex(), "patternMatches": len(matches),
            "displacementOffset": 14, "instructionEndOffset": 18, "localControllerArrayRva": hex(array),
            "primaryIndexConsumerRva": "0x1b3fdb6", "primaryIndex": 0, "fields": fields,
            "limitations": ["The array index is a split-screen index, not a Dota PlayerID or network player slot.",
                            "Runtime controller and full-handle validation is still required.",
                            "Correct hero identity does not establish availability of enemy sight data."]}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("game_bin_directory", type=Path)
    args = parser.parse_args()
    try:
        print(json.dumps(audit(args.game_bin_directory), indent=2))
        return 0
    except (OSError, ValueError, struct.error) as error:
        print(f"AUDIT_REJECTED: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
