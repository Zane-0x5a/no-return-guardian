using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace NoReturnGuardian.Core
{
    /// <summary>
    /// The trading station's stock as a working R0A records it, and which hideout visit it belongs to. A
    /// continued run rebuilds the stock list natively; at some later hideout entries the game destroys that
    /// rebuilt list while the station keeps its own reference, and every save from then on records an empty
    /// stock (2026-10-17 probe, artifacts/vendor-stock-20261017). The station draws a new stock when an
    /// encounter completes and buying never removes an item from the list, so within one run, one completed
    /// count and one PRNG seed (re-rolled by every continue) the stock does not change by itself.
    /// </summary>
    public sealed class VendorStockReading
    {
        public string RunId { get; set; }
        public long CompletedEncounters { get; set; }
        public int Count { get; set; }
        public long[] Items { get; set; }
        public string Seed { get; set; }
        internal int CountOffset { get; set; }
        internal int ItemsOffset { get; set; }
    }

    /// <summary>
    /// The newest save of the current run, seen by the read-only monitor, whose stock was not empty. Kept in
    /// Guardian storage so a capture can return the stock the game lost from a later save of the same draw.
    /// </summary>
    public sealed class VendorStockWitness
    {
        public string ProfilePath { get; set; }
        public string RunId { get; set; }
        public long CompletedEncounters { get; set; }
        public int Count { get; set; }
        public List<string> Items { get; set; }
        public string Seed { get; set; }
        public string SourceRunSha256 { get; set; }
        public string SavedUtc { get; set; }
    }

    /// <summary>The manifest's record of a stock returned from a witness into the captured R0A.</summary>
    public sealed class VendorStockRepair
    {
        public string OriginalRunSha256 { get; set; }
        public string WitnessRunSha256 { get; set; }
        public int Count { get; set; }
        public List<string> Items { get; set; }
    }

    public static class VendorStock
    {
        public const string WitnessFileName = "vendor-witness.json";
        public const int Slots = 16;
        private const int BodyOffset = 0x590;
        private const int ArenaOffset = 0x418;
        private const uint ObservedVersion = 27;
        private const ulong RunKey = 0x92AD3946449C662D;
        private const ulong RunIdKey = 0x0B55C175A3A096A0;
        private const ulong CompletedKey = 0xA3CFA1D0C8AA075A;
        private const ulong CountKey = 0x7A42A069A2B91748;
        private const ulong ItemsKey = 0x6E133B0E37FD8006;
        private const ulong SeedKey = 0xC0447FC28ACA3578;
        private const byte IntegerKind = 1;
        private const byte StringKind = 4;

        public static Result<VendorStockReading> Read(byte[] raw)
        {
            try
            {
                Arena arena = Arena.FromWorkingRaw(raw);
                Dictionary<ulong, Entry> run = arena.ObjectEntries(arena.Container(arena.Root, RunKey));
                int itemsNode = arena.Container(run, ItemsKey);
                if (arena.U64(itemsNode + 0x18) != 0 || arena.U32(itemsNode + 4) != Slots)
                {
                    throw new InvalidDataException("The stock is not a 16-slot list.");
                }

                int itemsOffset = arena.IntegerArray(itemsNode, Slots);
                long[] items = new long[Slots];
                for (int index = 0; index < Slots; index++)
                {
                    items[index] = arena.I64(itemsOffset + index * 8);
                }

                Entry count = run.ContainsKey(CountKey) ? run[CountKey] : null;
                if (count == null || count.Kind != IntegerKind)
                {
                    throw new InvalidDataException("The stock count is missing.");
                }

                long countValue = arena.I64(count.Slot);
                if (countValue < 0 || countValue > Slots)
                {
                    throw new InvalidDataException("The stock count is out of range.");
                }

                for (int index = 0; index < Slots; index++)
                {
                    if ((index < countValue) != (items[index] != 0))
                    {
                        throw new InvalidDataException("The stock slots disagree with its count.");
                    }
                }

                return Result<VendorStockReading>.Ok(
                    new VendorStockReading
                    {
                        RunId = arena.Text(run, RunIdKey),
                        CompletedEncounters = arena.Integer(run, CompletedKey),
                        Count = (int)countValue,
                        Items = items,
                        Seed = arena.Text(run, SeedKey),
                        CountOffset = arena.FileOffset(count.Slot, 8),
                        ItemsOffset = arena.FileOffset(itemsOffset, Slots * 8)
                    },
                    "vendor_stock_read",
                    "The run's trading-station stock was read.");
            }
            catch (Exception exception) when (
                exception is InvalidDataException
                || exception is ArgumentException
                || exception is OverflowException)
            {
                return Result<VendorStockReading>.Fail("vendor_stock_unreadable", exception.Message);
            }
        }

        public static VendorStockWitness ToWitness(
            VendorStockReading reading,
            string profilePath,
            string sourceRunSha256,
            DateTime savedUtc)
        {
            return new VendorStockWitness
            {
                ProfilePath = Path.GetFullPath(profilePath),
                RunId = reading.RunId,
                CompletedEncounters = reading.CompletedEncounters,
                Count = reading.Count,
                Items = reading.Items.Select(FormatItem).ToList(),
                Seed = reading.Seed,
                SourceRunSha256 = sourceRunSha256.ToLowerInvariant(),
                SavedUtc = savedUtc.ToUniversalTime().ToString("O")
            };
        }

        /// <summary>
        /// Whether the witness holds the stock this empty save lost: the same run, the same completed encounters
        /// and the same seed, so neither a new draw nor a continue lies between them, and written before it.
        /// </summary>
        public static bool WitnessRestores(
            VendorStockWitness witness,
            VendorStockReading target,
            string profilePath,
            DateTime targetSavedUtc)
        {
            DateTime witnessSaved;
            return witness != null
                && target != null
                && target.Count == 0
                && witness.Count > 0
                && witness.Count <= Slots
                && witness.Items != null
                && witness.Items.Count == Slots
                && witness.Items.Select((item, index) => (ParseItem(item) != 0) == (index < witness.Count))
                    .All(valid => valid)
                && !string.IsNullOrWhiteSpace(witness.SourceRunSha256)
                && witness.SourceRunSha256.Length == 64
                && string.Equals(witness.ProfilePath, Path.GetFullPath(profilePath), StringComparison.OrdinalIgnoreCase)
                && string.Equals(witness.RunId, target.RunId, StringComparison.Ordinal)
                && witness.CompletedEncounters == target.CompletedEncounters
                && !string.IsNullOrEmpty(target.Seed)
                && string.Equals(witness.Seed, target.Seed, StringComparison.Ordinal)
                && DateTime.TryParse(
                    witness.SavedUtc,
                    null,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out witnessSaved)
                && witnessSaved.ToUniversalTime() < targetSavedUtc.ToUniversalTime();
        }

        /// <summary>The empty save with the witness's stock written back; nothing else in it changes.</summary>
        public static byte[] Repair(byte[] raw, VendorStockReading target, VendorStockWitness witness)
        {
            byte[] repaired = (byte[])raw.Clone();
            Buffer.BlockCopy(BitConverter.GetBytes((long)witness.Count), 0, repaired, target.CountOffset, 8);
            for (int index = 0; index < Slots; index++)
            {
                Buffer.BlockCopy(
                    BitConverter.GetBytes(ParseItem(witness.Items[index])),
                    0,
                    repaired,
                    target.ItemsOffset + index * 8,
                    8);
            }

            return repaired;
        }

        public static bool Holds(VendorStockReading reading, int count, IList<string> items)
        {
            return reading != null
                && items != null
                && items.Count == Slots
                && reading.Count == count
                && Enumerable.Range(0, Slots).All(index => reading.Items[index] == ParseItem(items[index]));
        }

        public static string FormatItem(long item)
        {
            return unchecked((ulong)item).ToString("x16");
        }

        public static long ParseItem(string item)
        {
            ulong value;
            if (item == null
                || item.Length != 16
                || !ulong.TryParse(item, System.Globalization.NumberStyles.AllowHexSpecifier, null, out value))
            {
                return 0;
            }

            return unchecked((long)value);
        }

        private sealed class Entry
        {
            public byte Kind;
            public int Slot;
        }

        /// <summary>
        /// Bounds-checked reader of a working save's serialized arena: the root container, keyed objects and
        /// integer lists, as NativeSaveBuffer and NativeSerializedArena model them.
        /// </summary>
        private sealed class Arena
        {
            private readonly int _start;
            private readonly int _length;
            public byte[] Data { get; private set; }
            public Dictionary<ulong, Entry> Root { get; private set; }

            private Arena(byte[] data, int start, int length)
            {
                Data = data;
                _start = start;
                _length = length;
            }

            public static Arena FromWorkingRaw(byte[] raw)
            {
                if (raw == null || raw.Length < BodyOffset + ArenaOffset + 0x40 || raw[0] != 0x93 || raw[1] != 0xEA
                    || raw.Skip(2).Take(14).Any(value => value != 0))
                {
                    throw new InvalidDataException("The working save header is not the observed format.");
                }

                uint declared = BitConverter.ToUInt32(raw, BodyOffset + 4);
                uint version = BitConverter.ToUInt32(raw, BodyOffset + 8);
                if (declared != raw.Length - BodyOffset || version != ObservedVersion)
                {
                    throw new InvalidDataException("The working save body is not the observed format.");
                }

                Arena arena = new Arena(raw, BodyOffset + ArenaOffset, (int)declared - ArenaOffset);
                if (arena.U32(0x38) != arena._length || arena.U64(0x30) != 0)
                {
                    throw new InvalidDataException("The serialized arena descriptor does not cover the body.");
                }

                byte rootKind = arena.Byte(0);
                if (rootKind != 5 && rootKind != 6)
                {
                    throw new InvalidDataException("The serialized root is not a container.");
                }

                arena.Root = arena.ObjectEntries(arena.Pointer(arena.Pointer(8, 8), 0x20));
                return arena;
            }

            public Dictionary<ulong, Entry> ObjectEntries(int node)
            {
                int count = (int)U32(node + 4);
                Dictionary<ulong, Entry> entries = new Dictionary<ulong, Entry>();
                if (count == 0)
                {
                    return entries;
                }

                if (count > 1000000)
                {
                    throw new InvalidDataException("A serialized container count is invalid.");
                }

                int values = Pointer(node + 8, count * 8);
                int kinds = Pointer(node + 0x10, count);
                int keys = Pointer(node + 0x18, count * 8);
                for (int index = 0; index < count; index++)
                {
                    ulong key = U64(keys + index * 8);
                    if (entries.ContainsKey(key))
                    {
                        throw new InvalidDataException("A serialized object repeats a key.");
                    }

                    entries[key] = new Entry { Kind = Byte(kinds + index), Slot = values + index * 8 };
                }

                return entries;
            }

            public int Container(Dictionary<ulong, Entry> entries, ulong key)
            {
                Entry entry;
                if (!entries.TryGetValue(key, out entry) || (entry.Kind != 5 && entry.Kind != 6))
                {
                    throw new InvalidDataException("A required save container is missing.");
                }

                return Pointer(entry.Slot, 0x20);
            }

            public int IntegerArray(int node, int count)
            {
                int values = Pointer(node + 8, count * 8);
                int kinds = Pointer(node + 0x10, count);
                for (int index = 0; index < count; index++)
                {
                    if (Byte(kinds + index) != IntegerKind)
                    {
                        throw new InvalidDataException("A save list holds a non-integer value.");
                    }
                }

                return values;
            }

            public long Integer(Dictionary<ulong, Entry> entries, ulong key)
            {
                Entry entry;
                if (!entries.TryGetValue(key, out entry) || entry.Kind != IntegerKind)
                {
                    throw new InvalidDataException("A required save integer is missing.");
                }

                return I64(entry.Slot);
            }

            public string Text(Dictionary<ulong, Entry> entries, ulong key)
            {
                Entry entry;
                if (!entries.TryGetValue(key, out entry) || entry.Kind != StringKind)
                {
                    throw new InvalidDataException("A required save string is missing.");
                }

                int start = Pointer(entry.Slot, 1);
                int end = Array.IndexOf(Data, (byte)0, _start + start, _length - start);
                if (end <= _start + start)
                {
                    throw new InvalidDataException("A required save string is empty or unterminated.");
                }

                return new UTF8Encoding(false, true).GetString(Data, _start + start, end - _start - start);
            }

            public int FileOffset(int offset, int size)
            {
                return _start + Bounds(offset, size);
            }

            public byte Byte(int offset)
            {
                return Data[_start + Bounds(offset, 1)];
            }

            public uint U32(int offset)
            {
                return BitConverter.ToUInt32(Data, _start + Bounds(offset, 4));
            }

            public ulong U64(int offset)
            {
                if (offset % 8 != 0)
                {
                    throw new InvalidDataException("A serialized slot is misaligned.");
                }

                return BitConverter.ToUInt64(Data, _start + Bounds(offset, 8));
            }

            public long I64(int offset)
            {
                return unchecked((long)U64(offset));
            }

            private int Pointer(int field, int size)
            {
                ulong value = U64(field);
                if (value == 0 || value >= (ulong)_length)
                {
                    throw new InvalidDataException("A serialized reference is null or outside the arena.");
                }

                return Bounds((int)value, size);
            }

            private int Bounds(int offset, int size)
            {
                if (offset < 0 || size < 0 || (long)offset + size > _length)
                {
                    throw new InvalidDataException("A serialized reference is out of bounds.");
                }

                return offset;
            }
        }
    }
}
