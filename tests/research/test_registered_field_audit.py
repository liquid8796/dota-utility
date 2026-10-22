from copy import deepcopy
from pathlib import Path
import sys
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts" / "research"))
from verify_registered_fields import load_capture, validate_capture


class RegisteredFieldAuditTests(unittest.TestCase):
    def setUp(self):
        self.data = load_capture(ROOT / "docs/evidence/registered-fields-client-2026-09-29.json")

    def node(self, name="C_DOTA_BaseNPC"):
        return next(n for n in self.data["nodes"] if n["name"] == name)

    def rejects(self):
        with self.assertRaises(ValueError):
            validate_capture(self.data)

    def test_client_controls_pass_without_claiming_enemy_sight(self):
        result = validate_capture(self.data)
        self.assertEqual(14, result["classCount"])
        self.assertEqual([], result["namedMaskClasses"]["m_iTeamVisibilityBitmask"])
        self.assertFalse(result["enemySightVerified"])
        self.assertFalse(result["perPacketDeliveryVerified"])

    def test_server_controls_pass(self):
        data = load_capture(ROOT / "docs/evidence/registered-fields-server-2026-09-29.json")
        result = validate_capture(data)
        self.assertEqual(11, result["classCount"])
        self.assertEqual("fixed64", result["positiveControls"][-1]["encoder"])

    def test_unknown_hash_rejected(self):
        self.data["sha256"] = "0" * 64
        self.rejects()

    def test_unknown_module_rejected(self):
        self.data["module"] = "other.dll"
        self.rejects()

    def test_incomplete_field_list_rejected(self):
        self.node()["fields"].pop()
        self.rejects()

    def test_duplicate_class_rejected(self):
        self.data["nodes"].append(deepcopy(self.node()))
        self.rejects()

    def test_duplicate_field_rejected(self):
        self.node()["fields"][1]["name"] = self.node()["fields"][0]["name"]
        self.rejects()

    def test_missing_parent_rejected(self):
        self.node()["parents"] = ["NotCaptured"]
        self.rejects()

    def test_parent_cycle_rejected(self):
        self.node()["parents"] = ["C_DOTA_BaseNPC_Hero"]
        self.rejects()

    def test_wrong_declaring_class_rejected(self):
        self.node()["fields"][0]["declaringClass"] = "Other"
        self.rejects()

    def test_invalid_offset_rejected(self):
        self.node()["fields"][0]["offset"] = "not-hex"
        self.rejects()

    def test_missing_positive_control_rejected(self):
        for f in self.node("C_BaseEntity")["fields"]:
            if f["name"] == "m_iTeamNum":
                f["name"] = "renamed"
        self.rejects()

    def test_wrong_bitmap_encoder_rejected(self):
        for f in self.node("C_DOTA_DataNonSpectator")["fields"]:
            if f["name"] == "m_bNPCVisibleState":
                f["encoder"] = "varint"
        self.rejects()

    def test_wrong_control_offset_rejected(self):
        for f in self.node("C_DOTA_BaseNPC_Hero")["fields"]:
            if f["name"] == "m_iPlayerID":
                f["offset"] = "908"
        self.rejects()

    def test_missing_root_rejected(self):
        self.data["nodes"] = [n for n in self.data["nodes"] if n["name"] != "DataTeamPlayer_t"]
        self.rejects()

    def test_out_of_order_indices_rejected(self):
        self.node()["fields"][0]["index"] = 8
        self.rejects()

    def test_size_bound_rejected(self):
        self.node()["fields"][0]["size"] = 0x1000001
        self.rejects()

    def test_field_presence_is_reported_not_suppressed(self):
        self.node()["fields"][0]["name"] = "m_iTeamVisibilityBitmask"
        result = validate_capture(self.data)
        self.assertEqual(["C_DOTA_BaseNPC"], result["namedMaskClasses"]["m_iTeamVisibilityBitmask"])
        self.assertFalse(result["enemySightVerified"])


if __name__ == "__main__":
    unittest.main()
