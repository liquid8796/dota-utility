"""Read/Write, build-pinned visibility-registration audit. Python standard library only.

Usage: python scripts/research/audit_visibility_registration.py <game/dota/bin/win64>
This reads DLL files on disk. It never attaches to Dota or executes game code.
Unknown builds are rejected rather than interpreted using obsolete addresses.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import struct
import sys
from pathlib import Path

SUPPORTED = {
    "client.dll": "33f6c667b628c9a63106b6d9148f35eaa3a4ec665c43dfbc6b8440d320dfaccf",
    "server.dll": "78e5533be686544508346ae69b80e812eaeff3f1cbc08c69ebdf02765739e189",
}


class Image:
    def __init__(self, path: Path, *, supported: dict[str, str] | None = None) -> None:
        self.data = path.read_bytes()
        self.name = path.name
        self.digest = hashlib.sha256(self.data).hexdigest()
        # Callers with separate, independently audited address contracts may
        # supply their own fixed hashes. The default gate is deliberately unchanged.
        hashes = SUPPORTED if supported is None else supported
        if self.digest != hashes.get(self.name):
            raise ValueError(f"Unsupported build: {self.name} sha256={self.digest}; re-audit addresses first")
        if self.data[:2] != b"MZ":
            raise ValueError("Not a PE image")
        nt = struct.unpack_from("<I", self.data, 0x3C)[0]
        if self.data[nt:nt + 4] != b"PE\x00\x00":
            raise ValueError("Invalid PE signature")
        machine, count = struct.unpack_from("<HH", self.data, nt + 4)
        opt_size = struct.unpack_from("<H", self.data, nt + 20)[0]
        opt = nt + 24
        if machine != 0x8664 or struct.unpack_from("<H", self.data, opt)[0] != 0x20B:
            raise ValueError("Expected AMD64 PE32+")
        self.base = struct.unpack_from("<Q", self.data, opt + 24)[0]
        self.timestamp = struct.unpack_from("<I", self.data, nt + 8)[0]
        self.sections: list[tuple[int, int, int]] = []
        for index in range(count):
            section = opt + opt_size + index * 40
            virtual_address, raw_size, raw_pointer = struct.unpack_from("<III", self.data, section + 12)
            self.sections.append((virtual_address, raw_size, raw_pointer))

    def read(self, rva: int, length: int) -> bytes:
        if rva < 0 or length < 0:
            raise ValueError("Negative image range")
        for start, size, raw in self.sections:
            if start <= rva and rva + length <= start + size:
                offset = raw + rva - start
                result = self.data[offset:offset + length]
                if len(result) == length:
                    return result
        raise ValueError(f"RVA 0x{rva:X} is not in file-backed image data")

    def u32(self, rva: int) -> int:
        return struct.unpack("<I", self.read(rva, 4))[0]

    def u64(self, rva: int) -> int:
        return struct.unpack("<Q", self.read(rva, 8))[0]

    def text(self, rva: int) -> str:
        data = self.read(rva, 160)
        end = data.find(b"\x00")
        if end < 0:
            raise ValueError("Unterminated string")
        return data[:end].decode("ascii")

    def lea_rax_target(self, rva: int) -> int:
        data = self.read(rva, 7)
        if data[:3] != b"\x48\x8d\x05":
            raise ValueError(f"Expected RIP-relative LEA RAX at 0x{rva:X}")
        return rva + 7 + struct.unpack_from("<i", data, 3)[0]

    def type_name(self, vtable: int) -> str:
        locator = self.u64(vtable - 8) - self.base
        if self.u32(locator) != 1:
            raise ValueError("Unexpected MSVC RTTI locator signature")
        return self.text(self.u32(locator + 12) + 16)


def verify(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)


def audit(directory: Path) -> dict:
    client = Image(directory / "client.dll")
    server = Image(directory / "server.dll")
    name_rva = client.lea_rax_target(0x1ABA6EF)
    callback_rva = client.lea_rax_target(0x1ABA707)
    callback_name = client.text(name_rva)
    verify(callback_name == "OnReplayComap_TaggedAsVisibleByTeam", "Callback registration name mismatch")
    verify(callback_rva == 0x1AF4770, "Callback target mismatch")
    verify(client.read(callback_rva, 6) == bytes.fromhex("41564883ec40"), "Callback prologue mismatch")
    verify(client.read(0x1AF480A, 3) == bytes.fromhex("395010"), "Native full-handle comparison mismatch")
    policies = []
    for name, vtable, target, comparison in [
        ("CDOTA_DataRadiant", 0x3C4F610, 0x1EB8E80, "837f3002"),
        ("CDOTA_DataDire", 0x3C4FD70, 0x1EB8DC0, "837f3003"),
    ]:
        decorated_name = server.type_name(vtable)
        verify(name in decorated_name, "Team data RTTI mismatch")
        actual = server.u64(vtable + 94 * 8) - server.base
        verify(actual == target, "Team data vtable slot mismatch")
        body = server.read(target, 0xBA)
        verify(bytes.fromhex(comparison) in body, "Recipient-selector comparison mismatch")
        policies.append({"class": name, "rtti": decorated_name, "slot": 94,
                         "methodRva": hex(target), "bodySha256": hashlib.sha256(body).hexdigest(),
                         "selectorComparisonBytes": comparison})
    descriptors = []
    for image, fields in [(client, [("m_iTeamVisibilityBitmask", 0x58BE110, 0x780),
                                   ("m_bNPCVisibleState", 0x5A97EB0, 0xE58),
                                   ("m_iTeamNum", 0x58BF820, 0x3E7)]),
                          (server, [("m_iTeamVisibilityBitmask", 0x4776430, 0x544),
                                    ("m_bNPCVisibleState", 0x499ED20, 0xD00),
                                    ("m_iTeamNum", 0x4776D60, 0x344)])]:
        for name, descriptor, expected in fields:
            verify(image.text(image.u64(descriptor) - image.base) == name, "Field name mismatch")
            verify(image.u32(descriptor + 16) == expected, "Field offset mismatch")
            descriptors.append({"module": image.name, "name": name, "offset": hex(expected),
                                "descriptorRva": hex(descriptor),
                                "staticMetadataCount": image.u32(descriptor + 20)})
    return {"schemaVersion": 1, "method": "Offline build-pinned byte and RTTI verification; no game execution",
            "builds": [{"name": image.name, "sha256": image.digest, "peTimestamp": image.timestamp}
                       for image in (client, server)],
            "callback": {"name": callback_name, "nameRva": hex(name_rva),
                         "registrationLeaRva": "0x1aba707", "targetRva": hex(callback_rva)},
            "nativeIdentityGuard": {"instructionRva": "0x1af480a", "bytes": "395010",
                                    "instruction": "cmp dword ptr [rax + 0x10], edx"},
            "teamDataPolicies": policies, "staticFieldDescriptors": descriptors,
            "limitations": ["Replay-named callback registration does not establish live callback delivery.",
                            "No full-handle matching implies stale slot occupants may be misidentified.",
                            "Static field metadata absence is not proof that a field is unreplicated.",
                            "No enemy-view ground truth or universal non-host support is established by this audit."]}


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