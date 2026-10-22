"""Audit embedded combat-log schemas, native receive routes and event-flag provenance.

This is a research tool, NOT an enemy-visibility detector. Optional event flags,
archived records, and terrain visibility must not become current hero state.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
import struct
from dataclasses import dataclass
from pathlib import Path
from typing import Any

from audit_visibility_registration import Image

# These hashes govern THIS audit only. Older audits retain their original gate.
BUILD_HASHES = {
    "client.dll": "88fa24c60a8c68f84d25cc2285019889c05db0c55b2bed1165d6f98fa7c18215",
    "server.dll": "02b90bbb5814bc1af39cada58b6b8311808e01e84aa8fab894bdaea896a8cd47",
}

MAX_BYTES = 1 << 20
MAX_FIELDS = 8192
DESCRIPTORS = {
    "dota_shared_enums.proto": (22746, "ab58abd71dfd0f5e46a9ffe35ddf3fb73dba50381691c0ab084b88304541fd71", {"client.dll": 0x3D7CE70, "server.dll": 0x2E98910}),
    "dota_usermessages.proto": (43676, "ac3cd37fc0b7ed9753d6eb45ce70bcc6922facaaf63d14b57da5470b980dfc4b", {"client.dll": 0x3D60940, "server.dll": 0x2E87600}),
    "dota_modifiers.proto": (1572, "1475f636ab20113ec84ec7878b4dbed056e4e104b4386efb18a6548e33b3eae2", {"client.dll": 0x3D73E80, "server.dll": 0x2EBEFC0}),
}
MESSAGE_NAMES = {
    "CMsgDOTACombatLogEntry", "CDOTAUserMsg_CombatLogBulkData",
    "CDOTAUserMsg_UnitEvent", "CDOTAModifierBuffTableEntry",
}
MESSAGE_IDS = {
    "DOTA_UM_CombatLogData": 468,
    "DOTA_UM_CombatLogBulkData": 470,
    "DOTA_UM_CombatLogDataHLTV": 554,
}


@dataclass(frozen=True)
class WireField:
    number: int
    wire: int
    value: int | bytes


def read_varint(data: bytes, position: int) -> tuple[int, int]:
    """Read a bounded uint64 varint. No recursive or speculative memory reads."""
    if position < 0 or position >= len(data):
        raise ValueError("Varint position is outside the input.")
    result = 0
    for index in range(10):
        if position >= len(data):
            raise ValueError("Truncated varint.")
        value = data[position]
        position += 1
        if index == 9 and value > 1:
            raise ValueError("Varint exceeds uint64.")
        result |= (value & 127) << (index * 7)
        if value < 128:
            return result, position
    raise ValueError("Varint exceeds ten bytes.")


def parse_wire(data: bytes, *, maximum_fields: int = MAX_FIELDS) -> list[WireField]:
    """Parse the scalar/fixed/length-delimited wire types used by these schemas.

    Legacy groups are deliberately unsupported. Bounds and unknown-field skipping
    are independent of the names later assigned to fields.
    """
    if len(data) > MAX_BYTES or not 0 < maximum_fields <= MAX_FIELDS:
        raise ValueError("Wire-input budget exceeded.")
    result: list[WireField] = []
    position = 0
    while position < len(data):
        if len(result) >= maximum_fields:
            raise ValueError("Wire-field budget exceeded.")
        tag, position = read_varint(data, position)
        number, wire = tag >> 3, tag & 7
        if not 0 < number <= 0x1FFFFFFF:
            raise ValueError("Invalid protobuf field number.")
        if wire == 0:
            value, position = read_varint(data, position)
        elif wire in (1, 2, 5):
            if wire == 2:
                size, position = read_varint(data, position)
            else:
                size = 8 if wire == 1 else 4
            if size > MAX_BYTES or size > len(data) - position:
                raise ValueError("Truncated or over-budget wire field.")
            value = data[position:position + size]
            position += size
        else:
            raise ValueError(f"Unsupported wire type {wire}.")
        result.append(WireField(number, wire, value))
    return result


def optional(fields: list[WireField], number: int, wire: int) -> int | bytes | None:
    selected = [field for field in fields if field.number == number]
    # A protobuf merge can legally contain duplicates. This audit conservatively
    # rejects ambiguous samples instead of choosing an undocumented merge policy.
    if len(selected) > 1:
        raise ValueError(f"Ambiguous duplicate singular field {number}.")
    if not selected:
        return None
    if selected[0].wire != wire:
        raise ValueError(f"Unexpected wire type for field {number}.")
    return selected[0].value


def text(fields: list[WireField], number: int) -> str | None:
    value = optional(fields, number, 2)
    return value.decode("utf-8") if isinstance(value, bytes) else None


def signed32(value: int) -> int:
    value &= 0xFFFFFFFF
    return value - 0x100000000 if value & 0x80000000 else value


def summarize_descriptor(data: bytes, expected_name: str) -> dict[str, Any]:
    root = parse_wire(data)
    if text(root, 1) != expected_name:
        raise ValueError("FileDescriptorProto name does not match the audited input.")
    messages, enums = [], []
    for item in root:
        if item.wire != 2 or item.number not in (4, 5):
            continue
        definition = parse_wire(item.value)
        name = text(definition, 1)
        if item.number == 4 and name in MESSAGE_NAMES:
            rows = []
            for field in definition:
                if field.number != 2 or field.wire != 2:
                    continue
                part = parse_wire(field.value)
                rows.append({"name": text(part, 1), "number": optional(part, 3, 0),
                             "label": optional(part, 4, 0), "type": optional(part, 5, 0),
                             "typeName": text(part, 6), "default": text(part, 7)})
            if len({row["number"] for row in rows}) != len(rows):
                raise ValueError("Duplicate field numbers in a message descriptor.")
            messages.append({"name": name, "fields": rows})
        elif item.number == 5 and name in {"DOTA_COMBATLOG_TYPES", "EDotaUserMessages"}:
            values = []
            for value in definition:
                if value.number == 2 and value.wire == 2:
                    part = parse_wire(value.value)
                    value_name = text(part, 1)
                    number = optional(part, 2, 0)
                    if value_name is None or not isinstance(number, int):
                        raise ValueError("Incomplete enum value.")
                    if name == "DOTA_COMBATLOG_TYPES" or value_name in MESSAGE_IDS:
                        values.append({"name": value_name, "number": signed32(number)})
            enums.append({"name": name, "values": values})
    return {"messages": messages, "enums": enums}


def extract_descriptor(image: Image, name: str) -> dict[str, Any]:
    size, expected_hash, addresses = DESCRIPTORS[name]
    data = image.read(addresses[image.name], size)
    digest = hashlib.sha256(data).hexdigest()
    if digest != expected_hash:
        raise ValueError(f"Embedded descriptor fingerprint changed: {name}.")
    return {"name": name, "rva": hex(addresses[image.name]), "size": size,
            "sha256": digest, **summarize_descriptor(data, name)}


def decode_event(data: bytes, message_id: int | None = None) -> dict[str, Any]:
    """Decode an existing single event, never infer a continuous visibility state.

    A target_name is a string-table index, NOT an entity handle or PlayerID.
    The message ID is caller-supplied provenance, not a verified live subscription.
    """
    if message_id not in (None, 468, 554):
        raise ValueError("Only single-entry normal/HLTV messages are supported; not bulk data.")
    fields = parse_wire(data)
    event_type = optional(fields, 1, 0)
    target = optional(fields, 2, 0)
    if target is not None and target > 0xFFFFFFFF:
        raise ValueError("Target name index exceeds uint32.")
    timestamp_bytes = optional(fields, 15, 5)
    timestamp = struct.unpack("<f", timestamp_bytes)[0] if timestamp_bytes is not None else None
    if timestamp is not None and not math.isfinite(timestamp):
        raise ValueError("Non-finite event timestamp.")
    radiant = optional(fields, 11, 0)
    dire = optional(fields, 12, 0)
    return {
        "kind": "combat-log-event", "messageId": message_id,
        "channelAssertion": "caller supplied; not proof of live delivery",
        "eventType": signed32(event_type) if event_type is not None else None,
        "targetNameIndex": target, "timestamp": timestamp,
        "visibleRadiant": bool(radiant) if radiant is not None else None,
        "visibleDire": bool(dire) if dire is not None else None,
        "currentHeroVisibility": "Unknown", "canDriveCurrentOverlay": False,
        "limitations": ["event-scoped flags", "a verified producer ORs visibility of two participants",
                        "no verified target entity generation",
                        "no verified match identity or freshness", "missing fields are unknown, not false"],
    }


def expect_bytes(image: Image, rva: int, expected: str) -> None:
    data = bytes.fromhex(expected)
    if image.read(rva, len(data)) != data:
        raise ValueError(f"Native code contract changed at {image.name}+0x{rva:X}.")


def branch_target(image: Image, rva: int, opcode: int) -> int:
    code = image.read(rva, 5)
    if code[0] != opcode:
        raise ValueError("Expected a direct native branch.")
    return rva + 5 + struct.unpack_from("<i", code, 1)[0]


def verify_presence_mapping(image: Image) -> dict[str, Any]:
    checks = {
        0xCD2353: "418b4f30",        # target string-table index at object+0x30
        0xCD235B: "c60310",          # tag 2
        0xCD26E6: "0fbae60b",        # timestamp presence bit 11
        0xCD26FF: "f3410f104750",    # event timestamp at object+0x50
        0xCD2705: "c6037d",          # tag 15, fixed32
        0xCD2336: "418b7710",        # load has-bits word, object+0x10
        0xCD25C7: "0fbae60c",        # test presence bit 12
        0xCD25E0: "410fb64754",      # read boolean at object+0x54
        0xCD25E7: "c60358",          # wire tag 11, varint
        0xCD25F4: "0fbae60d",        # test presence bit 13
        0xCD260D: "410fb64755",      # read boolean at object+0x55
        0xCD2614: "c60360",          # wire tag 12, varint
    }
    for rva, code in checks.items():
        expect_bytes(image, rva, code)
    return {"hasBitsOffset": "0x10", "radiant": {"field": 11, "presenceBit": 12, "valueOffset": "0x54"},
            "dire": {"field": 12, "presenceBit": 13, "valueOffset": "0x55"},
            "checks": [{"rva": hex(rva), "bytes": code} for rva, code in checks.items()]}


def audit_receive_paths(image: Image) -> dict[str, Any]:
    bindings = [
        ("CombatLogData", 468, 0x4927338, 0x492AA50, 0x1C3D217, 0x1C3D287, 0x1CA59B0),
        ("CombatLogBulkData", 470, 0x49273F8, 0x492AA88, 0x1C3D374, 0x1C3D3DA, 0x1CA5860),
        ("CombatLogDataHLTV", 554, 0x483F198, 0x492AB30, 0x1C3D76D, 0x1C3D7D3, 0x1CA59F0),
    ]
    result = []
    for name, message_id, wrapper, listener, vt_lea, callback_lea, callback in bindings:
        wrapper_type = f".?AVCDOTAUserMsg_{name}_t@@"
        listener_type = f".?AV?$CGameMessageDelegateHook@VCDOTAUserMsg_{name}_t@@@@"
        if image.type_name(wrapper) != wrapper_type or image.type_name(listener) != listener_type:
            raise ValueError("Native receiver RTTI changed.")
        if image.lea_rax_target(vt_lea) != listener or image.lea_rax_target(callback_lea) != callback:
            raise ValueError("Native callback binding changed.")
        expect_bytes(image, callback_lea + 7, "49894638")
        getter = image.u64(wrapper + 3 * 8) - image.base
        getter_body = image.read(getter, 0x4E)
        id_load = b"\xba" + struct.pack("<I", message_id)
        if getter_body.count(id_load) != 1:
            raise ValueError("Native message-ID registration does not match the schema.")
        result.append({"message": name, "messageId": message_id,
                       "wrapperVtableRva": hex(wrapper), "listenerVtableRva": hex(listener),
                       "listenerType": listener_type, "callbackRva": hex(callback),
                       "bindingRva": hex(callback_lea), "idLoadRva": hex(getter + getter_body.index(id_load))})
    expected_branches = [
        (0x1CA59B9, 0xE8, 0x30F1380),  # normal -> HUD getter
        (0x1CA59D7, 0xE9, 0x30E0A90),  # normal -> HUD event ingestion
        (0x1CA58F6, 0xE8, 0x1C54A80),  # bulk -> archive insertion
        (0x1CA5A1B, 0xE8, 0x1C54A80),  # HLTV -> archive insertion, subject to native guard
    ]
    for branch, opcode, target in expected_branches:
        if branch_target(image, branch, opcode) != target:
            raise ValueError("Native combat-log route changed.")
    expect_bytes(image, 0x30F1380, "488b0511b13303c3")
    global_slot = 0x30F1387 + struct.unpack("<i", image.read(0x30F1383, 4))[0]
    if image.lea_rax_target(0x30E0DBA) != 0x3D4F110 or image.type_name(0x3D4F110) != ".?AVCMsgDOTACombatLogEntry@@":
        raise ValueError("HUD cached-message type changed.")
    return {"bindings": result, "normalHudPointerSlotRva": hex(global_slot),
            "normalHudIngestRva": "0x30e0a90", "archiveInsertRva": "0x1c54a80",
            "branches": [{"instructionRva": hex(a), "targetRva": hex(t)} for a, _, t in expected_branches],
            "interpretation": "Distinct native normal/bulk/HLTV routes; static code, not observed delivery.",
            "presenceMapping": verify_presence_mapping(image)}



# Byte contracts are for the BUILD_HASHES pair above, never discovered speculatively
# during a production sample. Comments describe the instructions checked below.
SENDER_CODE_CHECKS = {
    0x1E66253: "488d4db0",                # inline record = rbp - 0x50
    0x1E66281: "48894580",                # wrapper vtable at rbp - 0x80
    0x1E6629B: "488945b0",                # inline base vtable at rbp - 0x50
    0x1E66291: "4489b5c0000000",          # event type at record + 0x110
    0x1E66735: "4d85e47411",              # first participant optional
    0x1E6673A: "ba02000000498bcc",        # team Radiant, participant A (r12)
    0x1E66747: "84c07511",                # A visible => set event Radiant flag
    0x1E6674B: "ba02000000488bce",        # otherwise team Radiant, B (rsi)
    0x1E66758: "84c0740b",                # B not visible => skip true flag
    0x1E6675C: "814dc000100000c6450401",  # presence bit 12; record+0x54 true
    0x1E66767: "4d85e47411",              # first participant optional
    0x1E6676C: "ba03000000498bcc",        # team Dire, A
    0x1E66779: "84c07511",                # A visible => set event Dire flag
    0x1E6677D: "ba03000000488bce",        # otherwise team Dire, B
    0x1E6678A: "84c0740b",                # B not visible => skip true flag
    0x1E6678E: "814dc000200000c6450501",  # presence bit 13; record+0x55 true
    0x19FFFD0: "48895c2418564883ec20488b018bf2488bd9",
    0x19FFFE2: "ff9058080000",           # special-entity predicate
    0x19FFFF9: "80bb4905000000",         # visibility dirty flag
    0x1A00065: "ff9060080000",           # recompute through virtual method
    0x1A000A2: "8b83440500008bce488b5c2440d3e723c73bc7",
    0x1A000BA: "0f94c0",                 # return tested team bit
    0x1EB73E2: "488945a0",               # normal wrapper at rbp-0x60
    0x1EB73F0: "488945d0",               # inline record at rbp-0x30
    0x1EB74DD: "410fb685440300003c027506807d2400eb083c03750a807d25000f8414010000",
    0x1EB75E5: "488d45a048c7442430480100004889442428",
    0x1EB7609: "41ffd6",                 # recipient-filtered send call
}
SENDER_CALL_CHECKS = {
    0x1E6626E: 0xAC4BC0,
    0x1E66742: 0x19FFFD0,
    0x1E66753: 0x19FFFD0,
    0x1E66774: 0x19FFFD0,
    0x1E66785: 0x19FFFD0,
}


def verify_message_wrapper(image: Image, vtable: int, name: str, message_id: int) -> int:
    if image.type_name(vtable) != f".?AVCDOTAUserMsg_{name}_t@@":
        raise ValueError("Server message wrapper RTTI changed.")
    getter = image.u64(vtable + 3 * 8) - image.base
    code = image.read(getter, 0x4E)
    load = b"\xba" + struct.pack("<I", message_id)
    if code.count(load) != 1:
        raise ValueError("Server message wrapper ID does not match the descriptor.")
    return getter + code.index(load)


def audit_sender_paths(image: Image) -> dict[str, Any]:
    if image.name != "server.dll":
        raise ValueError("Sender contracts apply only to server.dll.")
    for rva, code in SENDER_CODE_CHECKS.items():
        expect_bytes(image, rva, code)
    for rva, target in SENDER_CALL_CHECKS.items():
        if branch_target(image, rva, 0xE8) != target:
            raise ValueError("Per-team visibility call target changed.")
    hltv_vtable = image.lea_rax_target(0x1E6627A)
    normal_vtable = image.lea_rax_target(0x1EB73DB)
    if hltv_vtable != 0x3C51CD8 or normal_vtable != 0x3C51798:
        raise ValueError("Sender message construction target changed.")
    hltv_id_load = verify_message_wrapper(image, hltv_vtable, "CombatLogDataHLTV", 554)
    normal_id_load = verify_message_wrapper(image, normal_vtable, "CombatLogData", 468)
    for lea, expected in ((0x1E6628A, 0x3C51D08), (0x1EB73E9, 0x3C517C8)):
        if image.lea_rax_target(lea) != expected:
            raise ValueError("Inline combat-log record vtable changed.")
        # This is the CMsgDOTACombatLogEntry base subobject of the named wrapper.
        if image.type_name(expected) not in {
            ".?AVCDOTAUserMsg_CombatLogDataHLTV_t@@",
            ".?AVCDOTAUserMsg_CombatLogData_t@@",
            ".?AVCMsgDOTACombatLogEntry@@",
        }:
            raise ValueError("Inline record RTTI does not match the wrapper/base.")
    return {
        "method": "Hash-pinned instructions, RTTI, message IDs and direct call targets",
        "producer": {
            "constructionRva": "0x1e6627a", "wrapperMessageId": 554,
            "idLoadRva": hex(hltv_id_load), "recordFrameBase": "rbp-0x50",
            "predicateCalls": [hex(p) for p in SENDER_CALL_CHECKS if p != 0x1E6626E],
            "perTeamPredicateRva": "0x19fffd0", "serverEntityMaskOffset": "0x544",
            "radiantSetRva": "0x1e6675c", "direSetRva": "0x1e6678e",
            "flagRule": "Set true when participant A OR participant B is visible to that team.",
            "participantIdentity": "Registers r12/rsi; neither is asserted to be the current local hero.",
            "scope": "One verified producer, not all combat-log types or all server execution paths.",
        },
        "normalRecipientFilter": {
            "constructionRva": "0x1eb73db", "messageId": 468,
            "idLoadRva": hex(normal_id_load), "recordFrameBase": "rbp-0x30",
            "recipientTeamOffset": "0x344", "selectorRva": "0x1eb74dd",
            "radiantEventFlagOffset": "0x54", "direEventFlagOffset": "0x55",
            "falseBranchRva": "0x1eb74f7", "skipTargetRva": "0x1eb7611",
            "sendCallRva": "0x1eb7609",
            "scope": "Verified gate in one normal send path; other event/recipient filters also exist.",
        },
        "counterexample": {
            "participantAVisible": False, "participantBVisible": True,
            "eventFlag": True, "participantAVisibilityCannotBeInferredFromEventFlag": True,
            "evidenceKind": "Logical counterexample to the verified OR rule, not a captured match event.",
        },
        "codeChecks": [{"rva": hex(rva), "bytes": code} for rva, code in SENDER_CODE_CHECKS.items()],
        "callChecks": [{"rva": hex(rva), "target": hex(target)} for rva, target in SENDER_CALL_CHECKS.items()],
        "currentHeroVisibility": "Unknown", "canDriveCurrentOverlay": False,
    }



def validate_definitions(descriptors: list[dict[str, Any]]) -> None:
    messages = [message for entry in descriptors for message in entry["messages"]]
    if len({message["name"] for message in messages}) != len(messages):
        raise ValueError("Duplicate audited message definitions.")
    by_name = {message["name"]: message for message in messages}
    if set(by_name) != MESSAGE_NAMES:
        raise ValueError("Audited message set is incomplete.")
    expected_counts = {"CMsgDOTACombatLogEntry": 82, "CDOTAUserMsg_CombatLogBulkData": 5,
                       "CDOTAUserMsg_UnitEvent": 9, "CDOTAModifierBuffTableEntry": 44}
    for name, count in expected_counts.items():
        if len(by_name[name]["fields"]) != count:
            raise ValueError("Audited field counts changed.")
    combat = {field["name"]: field for field in by_name["CMsgDOTACombatLogEntry"]["fields"]}
    for name, number in (("is_visible_radiant", 11), ("is_visible_dire", 12)):
        field = combat.get(name, {})
        if (field.get("number"), field.get("label"), field.get("type")) != (number, 1, 8):
            raise ValueError("Visibility fields no longer match optional bool tags 11/12.")
    values = {value["name"]: value["number"] for entry in descriptors
              for enum in entry["enums"] for value in enum["values"]}
    if any(values.get(name) != number for name, number in MESSAGE_IDS.items()):
        raise ValueError("Combat-log message IDs changed.")
    if values.get("DOTA_COMBATLOG_INVALID") != -1:
        raise ValueError("Signed combat-log enum decoding failed.")


def audit_binaries(directory: Path) -> dict[str, Any]:
    modules = []
    receiver = None
    sender = None
    for name in ("client.dll", "server.dll"):
        image = Image(directory / name, supported=BUILD_HASHES)  # Fixed profile; no bypass switch.
        descriptors = [extract_descriptor(image, name) for name in DESCRIPTORS]
        validate_definitions(descriptors)
        modules.append({"module": image.name, "sha256": image.digest, "descriptors": descriptors})
        if image.name == "client.dll":
            receiver = audit_receive_paths(image)
        else:
            sender = audit_sender_paths(image)
    for left, right in zip(modules[0]["descriptors"], modules[1]["descriptors"], strict=True):
        if (left["name"], left["sha256"]) != (right["name"], right["sha256"]):
            raise ValueError("Client/server message descriptors differ.")
    return {
        "auditVersion": 2, "scope": "offline embedded schemas, native receivers and selected server producers",
        "readOnlyGameFiles": True, "attachesDebugger": False, "capturesNetworkPackets": False,
        "modules": modules, "clientReceivePaths": receiver, "serverSenderPaths": sender,
        "canDriveCurrentOverlay": False,
        "unverified": ["delivery to a normal player in an active match", "flag semantics for all other event producers",
                       "mapping event target name to the current local hero generation",
                       "continuous visibility when no combat event arrives", "ward-only transitions"],
        "rejectedInference": "Event flag true does not establish visibility of the current local hero.",
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument("--binaries", type=Path, help="Installed game/dota/bin/win64 directory.")
    source.add_argument("--event", type=Path, help="Existing single CombatLogEntry wire payload; never a bulk envelope.")
    parser.add_argument("--message-id", type=int, choices=(468, 554), help="Caller assertion only, with --event.")
    parser.add_argument("--output", type=Path, help="Write explicit JSON evidence path; otherwise use stdout.")
    args = parser.parse_args()
    if args.binaries and args.message_id is not None:
        parser.error("--message-id applies only to --event.")
    try:
        if args.binaries:
            result = audit_binaries(args.binaries)
        else:
            with args.event.open("rb") as handle:
                data = handle.read(MAX_BYTES + 1)
            result = decode_event(data, args.message_id)
        payload = json.dumps(result, ensure_ascii=False, indent=2, allow_nan=False) + "\n"
        if args.output:
            args.output.parent.mkdir(parents=True, exist_ok=True)
            args.output.write_text(payload, encoding="utf-8")
        else:
            print(payload, end="")
        return 0
    except (OSError, ValueError, KeyError, struct.error) as error:
        parser.exit(2, f"Audit rejected input: {error}\n")


if __name__ == "__main__":
    raise SystemExit(main())
