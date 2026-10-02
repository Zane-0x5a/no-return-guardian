#!/usr/bin/env python3
"""Read-only DSAR/PSARC inspector for the PC T2R archives."""

from __future__ import annotations

import argparse
import bisect
import hashlib
import os
import struct
import sys
import zlib
from dataclasses import dataclass
from pathlib import Path

MAX_CHUNKS = 5_000_000
MAX_FILES = 5_000_000
MAX_BLOCK_SIZE = 16 * 1024 * 1024


class ArchiveError(RuntimeError):
    pass


def decode_lz4_block(source: bytes, expected_size: int) -> bytes:
    """Decode the raw LZ4 block form used by DSAR chunks."""
    output = bytearray()
    position = 0

    def extended_length(initial: int) -> int:
        nonlocal position
        length = initial
        if initial != 15:
            return length
        while True:
            if position >= len(source):
                raise ArchiveError("Truncated LZ4 extended length")
            value = source[position]
            position += 1
            length += value
            if value != 255:
                return length

    while position < len(source):
        token = source[position]
        position += 1
        literal_length = extended_length(token >> 4)
        literal_end = position + literal_length
        if literal_end > len(source):
            raise ArchiveError("Truncated LZ4 literal run")
        output.extend(source[position:literal_end])
        position = literal_end
        if position == len(source):
            break
        if position + 2 > len(source):
            raise ArchiveError("Truncated LZ4 match offset")

        match_offset = source[position] | (source[position + 1] << 8)
        position += 2
        if match_offset == 0 or match_offset > len(output):
            raise ArchiveError("Invalid LZ4 match offset")
        match_length = extended_length(token & 0x0F) + 4
        if len(output) + match_length > expected_size:
            raise ArchiveError("LZ4 block expands beyond its declared size")
        for _ in range(match_length):
            output.append(output[-match_offset])

    if len(output) != expected_size:
        raise ArchiveError(
            f"LZ4 block decoded to {len(output)} bytes, expected {expected_size}"
        )
    return bytes(output)


@dataclass(frozen=True)
class DsarChunk:
    decoded_offset: int
    compressed_offset: int
    decoded_size: int
    compressed_size: int
    compression_type: int


class FileReader:
    def __init__(self, path: Path) -> None:
        self._file = path.open("rb")
        self._size = path.stat().st_size

    def close(self) -> None:
        self._file.close()

    def read_at(self, offset: int, size: int) -> bytes:
        if offset < 0 or size < 0 or offset + size > self._size:
            raise ArchiveError("Read extends outside the archive")
        self._file.seek(offset)
        data = self._file.read(size)
        if len(data) != size:
            raise ArchiveError("Short archive read")
        return data


class DsarReader:
    HEADER_SIZE = 32
    CHUNK_SIZE = 32

    def __init__(self, base: FileReader) -> None:
        self._base = base
        header = base.read_at(0, self.HEADER_SIZE)
        if header[:4] != b"DSAR":
            raise ArchiveError("Not a DSAR stream")

        chunk_count = struct.unpack_from("<I", header, 8)[0]
        if chunk_count == 0 or chunk_count > MAX_CHUNKS:
            raise ArchiveError(f"Invalid DSAR chunk count: {chunk_count}")

        raw_table = base.read_at(
            self.HEADER_SIZE, chunk_count * self.CHUNK_SIZE
        )
        chunks: list[DsarChunk] = []
        for index in range(chunk_count):
            fields = struct.unpack_from("<QQIIB7x", raw_table, index * self.CHUNK_SIZE)
            chunk = DsarChunk(*fields)
            if chunk.decoded_size == 0:
                raise ArchiveError(f"DSAR chunk {index} has zero decoded size")
            if chunks:
                expected = chunks[-1].decoded_offset + chunks[-1].decoded_size
                if chunk.decoded_offset != expected:
                    raise ArchiveError("DSAR decoded chunks are not contiguous")
            chunks.append(chunk)

        self._chunks = chunks
        self._starts = [chunk.decoded_offset for chunk in chunks]
        self._cached_index = -1
        self._cached_data = b""

    def _decode_chunk(self, index: int) -> bytes:
        if index == self._cached_index:
            return self._cached_data

        chunk = self._chunks[index]
        if chunk.compressed_size == 0:
            decoded = bytes(chunk.decoded_size)
        else:
            compressed = self._base.read_at(
                chunk.compressed_offset, chunk.compressed_size
            )
            if chunk.compression_type == 0:
                if chunk.compressed_size != chunk.decoded_size:
                    raise ArchiveError("Invalid uncompressed DSAR chunk size")
                decoded = compressed
            else:
                decoded = decode_lz4_block(compressed, chunk.decoded_size)

        if len(decoded) != chunk.decoded_size:
            raise ArchiveError(f"DSAR chunk {index} decoded to the wrong size")
        self._cached_index = index
        self._cached_data = decoded
        return decoded

    def read_at(self, offset: int, size: int) -> bytes:
        if offset < 0 or size < 0:
            raise ArchiveError("Negative DSAR read")
        if size == 0:
            return b""

        end = offset + size
        stream_end = self._chunks[-1].decoded_offset + self._chunks[-1].decoded_size
        if end > stream_end:
            raise ArchiveError("Read extends outside the decoded DSAR stream")

        result = bytearray()
        position = offset
        while position < end:
            index = bisect.bisect_right(self._starts, position) - 1
            if index < 0:
                raise ArchiveError("DSAR read does not map to a chunk")
            chunk = self._chunks[index]
            within = position - chunk.decoded_offset
            take = min(end - position, chunk.decoded_size - within)
            if take <= 0:
                raise ArchiveError("Invalid DSAR chunk mapping")
            decoded = self._decode_chunk(index)
            result.extend(decoded[within : within + take])
            position += take
        return bytes(result)


@dataclass(frozen=True)
class PsarcEntry:
    name_hash: bytes
    block_index: int
    uncompressed_size: int
    offset: int


class PsarcArchive:
    HEADER_SIZE = 32

    def __init__(self, reader: FileReader | DsarReader) -> None:
        self._reader = reader
        header = reader.read_at(0, self.HEADER_SIZE)
        if header[:4] != b"PSAR":
            raise ArchiveError("Decoded stream is not PSARC")

        (
            _magic,
            self.version,
            compression,
            toc_length,
            entry_size,
            file_count,
            self.block_size,
            self.flags,
        ) = struct.unpack(">4sI4sIIIII", header)
        self.compression = compression.decode("ascii", errors="strict")

        if entry_size != 30:
            raise ArchiveError(f"Unsupported PSARC entry size: {entry_size}")
        if file_count == 0 or file_count > MAX_FILES:
            raise ArchiveError(f"Invalid PSARC file count: {file_count}")
        if self.block_size == 0 or self.block_size > MAX_BLOCK_SIZE:
            raise ArchiveError(f"Invalid PSARC block size: {self.block_size}")

        entries_size = entry_size * file_count
        block_width = max(1, ((self.block_size - 1).bit_length() + 7) // 8)
        zsize_bytes = toc_length - self.HEADER_SIZE - entries_size
        if zsize_bytes < 0 or zsize_bytes % block_width != 0:
            raise ArchiveError("Invalid PSARC table length")

        raw_entries = reader.read_at(self.HEADER_SIZE, entries_size)
        entries: list[PsarcEntry] = []
        for index in range(file_count):
            raw = raw_entries[index * entry_size : (index + 1) * entry_size]
            entries.append(
                PsarcEntry(
                    name_hash=raw[:16],
                    block_index=int.from_bytes(raw[16:20], "big"),
                    uncompressed_size=int.from_bytes(raw[20:25], "big"),
                    offset=int.from_bytes(raw[25:30], "big"),
                )
            )

        self.entries = entries
        self._block_width = block_width
        self._zsizes = reader.read_at(
            self.HEADER_SIZE + entries_size, zsize_bytes
        )
        self._names = self._read_names()
        self._named_entries = self._match_names_to_entries()

    def _block_size_at(self, index: int) -> int:
        start = index * self._block_width
        end = start + self._block_width
        if end > len(self._zsizes):
            raise ArchiveError("PSARC block index is outside the size table")
        encoded = int.from_bytes(self._zsizes[start:end], "big")
        return self.block_size if encoded == 0 else encoded

    def read_entry(self, entry: PsarcEntry) -> bytes:
        remaining = entry.uncompressed_size
        offset = entry.offset
        block_index = entry.block_index
        result = bytearray()

        while remaining:
            output_size = min(remaining, self.block_size)
            compressed_size = self._block_size_at(block_index)
            block = self._reader.read_at(offset, compressed_size)
            if compressed_size == output_size:
                decoded = block
            elif self.compression == "zlib":
                try:
                    decoded = zlib.decompress(block)
                except zlib.error as exc:
                    raise ArchiveError(
                        f"PSARC zlib decode failed at block {block_index}"
                    ) from exc
            else:
                raise ArchiveError(
                    f"Unsupported PSARC compression: {self.compression}"
                )

            if len(decoded) != output_size:
                raise ArchiveError(
                    f"PSARC block {block_index} decoded to the wrong size"
                )
            result.extend(decoded)
            remaining -= output_size
            offset += compressed_size
            block_index += 1

        return bytes(result)

    def _read_names(self) -> list[str]:
        manifest = self.read_entry(self.entries[0])
        text = manifest.decode("utf-8", errors="strict")
        names: list[str] = []
        for line in text.replace("\x00", "\n").splitlines():
            name = line.split(",", 1)[0].split("\t", 1)[0].strip()
            if name:
                names.append(name.replace("\\", "/"))

        if len(names) != len(self.entries) - 1:
            raise ArchiveError(
                "PSARC manifest count does not match the file table: "
                f"{len(names)} names for {len(self.entries) - 1} entries"
            )
        return names

    def _match_names_to_entries(self) -> list[tuple[str, PsarcEntry]]:
        entries_by_hash: dict[bytes, PsarcEntry] = {}
        for entry in self.entries[1:]:
            if entry.name_hash in entries_by_hash:
                raise ArchiveError("PSARC contains duplicate path hashes")
            entries_by_hash[entry.name_hash] = entry

        named: list[tuple[str, PsarcEntry]] = []
        for name in self._names:
            digest = hashlib.md5(
                name.encode("utf-8"), usedforsecurity=False
            ).digest()
            entry = entries_by_hash.pop(digest, None)
            if entry is None:
                raise ArchiveError(f"PSARC manifest path hash is missing: {name}")
            named.append((name, entry))

        if entries_by_hash:
            raise ArchiveError(
                f"PSARC has {len(entries_by_hash)} unnamed file-table entries"
            )
        return named

    def named_entries(self) -> list[tuple[str, PsarcEntry]]:
        return list(self._named_entries)


def open_archive(path: Path) -> tuple[FileReader, PsarcArchive]:
    base = FileReader(path)
    try:
        magic = base.read_at(0, 4)
        if magic == b"DSAR":
            reader: FileReader | DsarReader = DsarReader(base)
        elif magic == b"PSAR":
            reader = base
        else:
            raise ArchiveError(f"Unsupported archive magic: {magic!r}")
        return base, PsarcArchive(reader)
    except Exception:
        base.close()
        raise


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("archive", type=Path)
    operation = parser.add_mutually_exclusive_group(required=True)
    operation.add_argument(
        "--list", nargs="?", const="", metavar="FILTER", help="list matching paths"
    )
    operation.add_argument("--extract", metavar="ARCHIVE_PATH")
    parser.add_argument("--output", type=Path, help="required with --extract")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    if args.extract and args.output is None:
        raise SystemExit("--output is required with --extract")
    if args.output is not None and not args.extract:
        raise SystemExit("--output is only valid with --extract")

    archive_path = args.archive.resolve(strict=True)
    base, archive = open_archive(archive_path)
    try:
        named = archive.named_entries()
        if args.list is not None:
            needle = args.list.casefold()
            matches = [item for item in named if needle in item[0].casefold()]
            for name, entry in matches:
                print(f"{entry.uncompressed_size:>10}  {name}")
            print(
                f"MATCHES={len(matches)} FILES={len(named)} "
                f"PSARC_VERSION=0x{archive.version:08X} "
                f"COMPRESSION={archive.compression}"
            )
            return 0

        target = args.extract.replace("\\", "/").casefold()
        matches = [item for item in named if item[0].casefold() == target]
        if len(matches) != 1:
            raise ArchiveError(
                f"Expected one exact archive path match, found {len(matches)}"
            )

        name, entry = matches[0]
        payload = archive.read_entry(entry)
        output = args.output.resolve()
        output.parent.mkdir(parents=True, exist_ok=True)
        with output.open("xb") as handle:
            handle.write(payload)
        digest = hashlib.sha256(payload).hexdigest().upper()
        print(
            f"EXTRACTED={name} OUTPUT={output} SIZE={len(payload)} SHA256={digest}"
        )
        return 0
    finally:
        base.close()


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (ArchiveError, OSError, UnicodeError) as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        raise SystemExit(1) from exc
