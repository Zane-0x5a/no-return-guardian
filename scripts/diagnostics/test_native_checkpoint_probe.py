import struct
import unittest

from NativeCheckpointProbe import REGISTRATIONS, ProbeError, inspect_registrations, sample, verify_build


class FakeProcess:
    base = 0x7FF600000000

    def __init__(self):
        self.memory = {}
        for _, node, function_object, sid, implementation in REGISTRATIONS:
            self.memory[self.base + node] = struct.pack("<QQ", self.base + function_object, sid)
            self.memory[self.base + function_object] = struct.pack("<QQ", self.base + 0x2E56290,
                                                                  self.base + implementation)
        self.memory[self.base + 0x4161F58] = b"\1"
        self.memory[self.base + 0x415AD30] = struct.pack("<i", 7)
        self.memory[self.base + 0x35F9F40] = bytes(56)

    def read(self, address, size):
        return self.memory[address][:size]


class NativeCheckpointProbeTests(unittest.TestCase):
    def test_relocated_registrations_match(self):
        process = FakeProcess()
        self.assertTrue(all(item["matches"] for item in inspect_registrations(process.read, process.base)))

    def test_wrong_build_rejected(self):
        for payload in [b"", b"MZ", b"different game"]:
            with self.subTest(payload=payload), self.assertRaises(ProbeError):
                verify_build(payload)

    def test_each_registration_field_is_checked(self):
        for _, node, function_object, _, _ in REGISTRATIONS:
            for rva in [node, function_object]:
                for field in [0, 8]:
                    with self.subTest(rva=rva, field=field):
                        process = FakeProcess()
                        payload = bytearray(process.memory[process.base + rva])
                        payload[field] ^= 1
                        process.memory[process.base + rva] = bytes(payload)
                        with self.assertRaises(ProbeError):
                            sample(process, process.base)

    def test_uninitialized_registration_rejected(self):
        process = FakeProcess()
        process.memory[process.base + REGISTRATIONS[0][1]] = bytes(16)
        with self.assertRaises(ProbeError):
            sample(process, process.base)

    def test_short_registration_rejected(self):
        with self.assertRaises(ProbeError):
            inspect_registrations(lambda address, size: bytes(15), FakeProcess.base)

    def test_unreadable_memory_propagates(self):
        def denied(address, size):
            raise ProbeError("access denied")
        with self.assertRaisesRegex(ProbeError, "access denied"):
            inspect_registrations(denied, FakeProcess.base)

    def test_unknown_mode_rejected(self):
        process = FakeProcess()
        process.memory[process.base + 0x4161F58] = b"\2"
        with self.assertRaises(ProbeError):
            sample(process, process.base)

    def test_changing_registration_rejected(self):
        process = FakeProcess()
        original = process.read
        reads = 0
        def changing(address, size):
            nonlocal reads
            reads += 1
            if reads == 14:
                return bytes(size)
            return original(address, size)
        process.read = changing
        with self.assertRaisesRegex(ProbeError, "changed"):
            sample(process, process.base)

    def test_valid_read_does_not_claim_scene_or_restore(self):
        process = FakeProcess()
        result = sample(process, process.base)
        self.assertEqual(result["scene"], "not_classified")
        self.assertFalse(result["restore_accepted"])
        self.assertEqual(result["overall_mode_raw"], 7)


if __name__ == "__main__":
    unittest.main()
