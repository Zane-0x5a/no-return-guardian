"""Read-only, build-pinned observation of native Rogue checkpoint registrations."""

import argparse
import ctypes
from ctypes import wintypes
import hashlib
import json
import os
from pathlib import Path
import struct
import sys
import time

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from InspectPeVa import read_layout, va_to_offset
from NativeCodeSignature import UNVERIFIED_BUILD, VERIFIED_SHA256, signature_mismatch


ROOT = Path(__file__).resolve().parents[2]
# Guardian's helper programs and the frida package. A development tree builds them into tools\, a release
# ships them in native-recovery\tools; neither shares a directory with the local evidence under artifacts\.
TOOLS = ROOT / 'tools'
FRIDA_DEPS = TOOLS / 'native-probe-deps'


REGISTRATIONS = (
    ("remember", 0x4166E90, 0x41674D8, 0x5E22848122583AF6, 0x104C570),
    ("update_run", 0x4167460, 0x41677C0, 0x006E58EC322C04A2, 0x104D4F0),
    ("store_hub", 0x4167BC0, 0x41670A8, 0x11A4E1D3CED13973, 0x104D060),
    ("backup", 0x4167A88, 0x4166F98, 0x05234BBFB7DBAB1D, 0x104D4C0),
    ("restore_backup", 0x4167C08, 0x41670D0, 0xA3FCBC8490406922, 0x104B690),
)


class ProbeError(RuntimeError):
    pass


def verify_build(image):
    """Refuse a build whose code at the native sites differs from the verified one (NativeCodeSignature).
    Returns the executable's SHA-256, which identifies the build in logs and records."""
    digest = hashlib.sha256(image).hexdigest().upper()
    mismatch = None if digest == VERIFIED_SHA256 else signature_mismatch(image)
    if mismatch is not None:
        raise ProbeError(f"{UNVERIFIED_BUILD} ({mismatch}); nothing was attached or changed")
    return digest


def inspect_current_task(read, base, max_nodes=64, include_simple_fallback=False):
    if type(max_nodes) is not int or not 1 <= max_nodes <= 64:
        raise ProbeError("task traversal limit must be 1..64")
    observations = []

    def scalar(address, encoding):
        size = struct.calcsize(encoding)
        if not 0x10000 <= address < 0x800000000000 - size:
            raise ProbeError("invalid task memory address")
        payload = read(address, size)
        if len(payload) != size:
            raise ProbeError("short task read")
        observations.append((address, size, payload))
        return struct.unpack(encoding, payload)[0]

    def pointer(address, nullable=False):
        value = scalar(address, '<Q')
        if nullable and value == 0:
            return value
        if not 0x10000 <= value < 0x800000000000:
            raise ProbeError("invalid task pointer")
        return value

    def finish(status, visited_nodes, **fields):
        for address, size, payload in reversed(observations):
            if read(address, size) != payload:
                raise ProbeError("task observation changed during revalidation")
        return {"status": status, "visited_nodes": visited_nodes,
                "usable_as_restore_gate": False,
                "basis": "bounded non-atomic read of supported native selector branches",
                **fields}

    def candidate(status, task, visited, flags):
        descriptor = pointer(task + 0x120)
        word = scalar(descriptor + 0x6C, '<I')
        return finish(status, len(visited), task=hex(task), descriptor=hex(descriptor),
                      descriptor_word=word, task_flags=hex(flags))

    def walk(sentinel):
        node = pointer(sentinel)
        visited = set()
        while node != sentinel:
            if node in visited:
                raise ProbeError("non-sentinel task list cycle")
            if len(visited) >= max_nodes:
                raise ProbeError("task traversal limit exceeded")
            visited.add(node)
            yield pointer(node + 0x10), visited
            node = pointer(node)

    context = base + 0x9334770
    use_direct = scalar(context, '<B') == 0
    if not use_direct and not include_simple_fallback:
        return finish("fallback_required", 0)
    sentinel = context + 0xB0
    visited = set()
    if use_direct:
        for task, visited in walk(sentinel):
            flags = scalar(task + 0xA0, '<I')
            if flags & (1 << 20):
                return candidate("direct_candidate_observed", task, visited, flags)
    if not include_simple_fallback:
        return finish("fallback_required" if visited else "no_current_task", len(visited))
    visited = set()
    for task, visited in walk(sentinel):
        if scalar(task + 0x90, '<Q') != 0xEBADA5168620C5FE:
            return finish("sid_lookup_required", len(visited))
        activity = pointer(task + 0xB8, nullable=True)
        if not activity or not scalar(activity + 0x58, '<B') & 0x0E:
            continue
        flags = scalar(task + 0xA0, '<I')
        if flags & 1:
            return finish("recursive_selector_required", len(visited))
        return candidate("fallback_self_observed", task, visited, flags)
    return finish("no_current_task", len(visited))


def inspect_registrations(read, base):
    records = []
    for name, node_rva, object_rva, sid, implementation_rva in REGISTRATIONS:
        node = read(base + node_rva, 16)
        function_object = read(base + object_rva, 16)
        if len(node) != 16 or len(function_object) != 16:
            raise ProbeError("short registration read")
        actual_object, actual_sid = struct.unpack("<QQ", node)
        vtable, implementation = struct.unpack("<QQ", function_object)
        matches = (
            actual_object == base + object_rva
            and actual_sid == sid
            and vtable == base + 0x2E56290
            and implementation == base + implementation_rva
        )
        records.append({"name": name, "matches": matches,
                        "object": hex(actual_object), "sid": hex(actual_sid),
                        "vtable": hex(vtable), "implementation": hex(implementation)})
    return records


class ReadOnlyProcess:
    def __init__(self, process_id):
        if os.name != "nt" or ctypes.sizeof(ctypes.c_void_p) != 8:
            raise ProbeError("64-bit Windows Python required")
        self.kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        self.psapi = ctypes.WinDLL("psapi", use_last_error=True)
        self.kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        self.kernel.OpenProcess.restype = wintypes.HANDLE
        self.kernel.CloseHandle.argtypes = [wintypes.HANDLE]
        self.kernel.CloseHandle.restype = wintypes.BOOL
        self.kernel.ReadProcessMemory.argtypes = [wintypes.HANDLE, ctypes.c_void_p,
                                                  ctypes.c_void_p, ctypes.c_size_t,
                                                  ctypes.POINTER(ctypes.c_size_t)]
        self.kernel.ReadProcessMemory.restype = wintypes.BOOL
        self.kernel.QueryFullProcessImageNameW.argtypes = [wintypes.HANDLE, wintypes.DWORD,
                                                          wintypes.LPWSTR,
                                                          ctypes.POINTER(wintypes.DWORD)]
        self.kernel.QueryFullProcessImageNameW.restype = wintypes.BOOL
        self.psapi.EnumProcessModulesEx.argtypes = [wintypes.HANDLE, ctypes.POINTER(ctypes.c_void_p),
                                                   wintypes.DWORD, ctypes.POINTER(wintypes.DWORD),
                                                   wintypes.DWORD]
        self.psapi.EnumProcessModulesEx.restype = wintypes.BOOL
        self.handle = self.kernel.OpenProcess(0x0410, False, process_id)
        if not self.handle:
            raise ProbeError(f"OpenProcess read-only failed: {ctypes.get_last_error()}")

    def close(self):
        if self.handle:
            self.kernel.CloseHandle(self.handle)
            self.handle = None

    def image_path(self):
        buffer = ctypes.create_unicode_buffer(32768)
        length = wintypes.DWORD(len(buffer))
        if not self.kernel.QueryFullProcessImageNameW(self.handle, 0, buffer, ctypes.byref(length)):
            raise ProbeError(f"process path query failed: {ctypes.get_last_error()}")
        path = Path(buffer.value)
        if path.name.lower() != "tlou-ii.exe":
            raise ProbeError("only the verified tlou-ii.exe build is supported")
        return path

    def image_base(self):
        modules = (ctypes.c_void_p * 2048)()
        needed = wintypes.DWORD()
        if not self.psapi.EnumProcessModulesEx(self.handle, modules, ctypes.sizeof(modules),
                                                ctypes.byref(needed), 2):
            raise ProbeError(f"module query failed: {ctypes.get_last_error()}")
        if needed.value < ctypes.sizeof(ctypes.c_void_p) or needed.value > ctypes.sizeof(modules):
            raise ProbeError("unexpected module list size")
        if not modules[0]:
            raise ProbeError("main module missing")
        return modules[0]

    def read(self, address, size):
        if not self.handle or not 0 < size <= 4096 or not 0x10000 <= address < 0x7FFFFFFFFFFF:
            raise ProbeError("invalid bounded read")
        buffer = ctypes.create_string_buffer(size)
        actual = ctypes.c_size_t()
        success = self.kernel.ReadProcessMemory(self.handle, ctypes.c_void_p(address), buffer,
                                                size, ctypes.byref(actual))
        if not success or actual.value != size:
            raise ProbeError(f"memory read failed at {address:#x}: {ctypes.get_last_error()}")
        return buffer.raw


def verify_code(process, image, base, include_task=False):
    preferred, headers, sections = read_layout(image)
    for rva in [0x105F830, 0x10434C0, 0x1042AA0, 0x1042B80,
                0x1046FC0, 0x1352580, 0x160FD00]:
        offset, _ = va_to_offset(preferred + rva, preferred, headers, sections)
        if process.read(base + rva, 16) != image[offset:offset + 16]:
            raise ProbeError(f"live code fingerprint mismatch at RVA {rva:#x}")
    if include_task:
        for rva, size in [(0x1B6EA60, 0xBF), (0x1046F80, 0x32), (0x1046FC0, 0x32),
                          (0x1BDE410, 0x4F), (0x1BDDB60, 0xD0)]:
            offset, _ = va_to_offset(preferred + rva, preferred, headers, sections)
            if process.read(base + rva, size) != image[offset:offset + size]:
                raise ProbeError(f"live task selector fingerprint mismatch at RVA {rva:#x}")


def sample(process, base):
    first = inspect_registrations(process.read, base)
    mode = process.read(base + 0x4161F58, 1)[0]
    overall = struct.unpack("<i", process.read(base + 0x415AD30, 4))[0]
    checkpoint_pointers = struct.unpack("<7Q", process.read(base + 0x35F9F40, 56))
    second = inspect_registrations(process.read, base)
    if first != second:
        raise ProbeError("registrations changed during observation")
    if not all(record["matches"] for record in first):
        raise ProbeError("registrations uninitialized or different from verified build")
    if mode not in (0, 1):
        raise ProbeError("unexpected Rogue boolean encoding")
    return {"unix_time": time.time(), "registrations": first,
            "rogue_mode": bool(mode), "overall_mode_raw": overall,
            "checkpoint_pointers": [hex(value) for value in checkpoint_pointers],
            "scene": "not_classified", "restore_accepted": False,
            "note": "Non-atomic read-only observation; not a snapshot or a restore gate."}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("pid", type=int)
    parser.add_argument("--samples", type=int, default=1)
    parser.add_argument("--interval", type=float, default=1)
    parser.add_argument("--task", action="store_true",
                        help="observe supported bounded task selector branches; never a restore gate")
    args = parser.parse_args()
    if args.pid <= 0 or not 1 <= args.samples <= 600 or not 0.1 <= args.interval <= 10:
        parser.error("positive PID, 1..600 samples and 0.1..10 second interval required")
    process = ReadOnlyProcess(args.pid)
    try:
        path = process.image_path()
        image = path.read_bytes()
        digest = verify_build(image)
        base = process.image_base()
        verify_code(process, image, base, args.task)
        print(json.dumps({"event": "read_only_attached", "pid": args.pid,
                          "image": str(path), "sha256": digest, "base": hex(base)}), flush=True)
        for index in range(args.samples):
            verify_code(process, image, base, args.task)
            observation = sample(process, base)
            if args.task:
                observation["native_task"] = inspect_current_task(
                    process.read, base, include_simple_fallback=True)
            print(json.dumps(observation), flush=True)
            if index + 1 < args.samples:
                time.sleep(args.interval)
        return 0
    finally:
        process.close()


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (ProbeError, OSError, ValueError) as error:
        print(json.dumps({"event": "probe_failed", "error": str(error)}), file=sys.stderr)
        raise SystemExit(1)
