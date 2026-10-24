using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace NoReturnGuardian.Core
{
    /// <summary>A read-only observation at the player's preparation boundary, never a source of random gifts.</summary>
    public sealed class SkillObservation
    {
        public int Version { get; set; }
        public string ProfilePath { get; set; }
        public int ProcessId { get; set; }
        public string ProcessBirth { get; set; }
        public string BuildSha256 { get; set; }
        public string ObservedUtc { get; set; }
        public string Boundary { get; set; }
        public long? DepartureObservedMs { get; set; }
        public string OriginalRunSha256 { get; set; }
        public string RunId { get; set; }
        public int CharacterIndex { get; set; }
        public long CompletedEncounters { get; set; }
        public string Seed { get; set; }
        public int Difficulty { get; set; }
        public string Task { get; set; }
        public int TaskWord { get; set; }
        public int Supplements { get; set; }
        public string Upgrades { get; set; }
        public List<string> Manuals { get; set; }
    }

    public sealed class SkillProtection
    {
        public string OriginalRunSha256 { get; set; }
        public string CompletedRunSha256 { get; set; }
        public string EvidenceSha256 { get; set; }
        public string NativeExportSha256 { get; set; }
        public string NativeExportFooter { get; set; }
        public SkillObservation Observation { get; set; }
    }

    public sealed class SkillStateReading
    {
        public int CharacterIndex { get; internal set; }
        public string RunId { get; internal set; }
        public long CompletedEncounters { get; internal set; }
        public string Seed { get; internal set; }
        public int Supplements { get; internal set; }
        public string Upgrades { get; internal set; }
        public List<string> Manuals { get; internal set; }
        internal List<SkillInventory> Inventories { get; set; }
        public bool IsEllie { get { return CharacterIndex == 0; } }
        public bool MissingStartingTree
        {
            get { return IsEllie && Manuals.Count < 2; }
        }
    }

    internal sealed class SkillInventory
    {
        public int CountOffset;
        public int Count;
        public int[] NameOffsets;
        public int[] AmountOffsets;
        public List<string> Manuals;
        public int Supplements;
        public int[] UpgradeOffsets;
        public string Upgrades;
    }

    /// <summary>
    /// Reads the game's fixed persistent-inventory slots. The only supported completion inserts the one
    /// observed Ellie opening manual into a zero slot and advances the occupied count. All other bytes,
    /// including purchased upgrades, supplements, the route board and lockbox, remain the original preparation's.
    /// </summary>
    public static class SkillState
    {
        public const string DefaultManual = "2787c0e3ef93b5a5";
        public const string GroundedDefaultManual = "4424ce6fb3e9b172";
        public const string OriginalEvidencePath = "evidence/skill-original-r0a.bin";
        private const ulong RunKey = 0x92AD3946449C662D;
        private const ulong CharacterKey = 0xBF552FA83AED497F;
        private const ulong RunIdKey = 0x0B55C175A3A096A0;
        private const ulong CompletedKey = 0xA3CFA1D0C8AA075A;
        private const ulong SeedKey = 0xC0447FC28ACA3578;
        private const ulong SupplementsKey = 0xE7052D97197FB860;
        private const ulong CheckpointKey = 0xC8F31246AF8D829F;
        private const ulong EllieKey = 0xC7B0F9AD15CF8832;
        private const ulong InventoriesKey = 0x8301275D350FBE60;
        private const ulong ItemsKey = 0x3E7884BF4F412C6F;
        private const ulong CountKey = 0x316F8BBD86015275;
        private const ulong NameKey = 0xC4BCADBA8E631B86;
        private const ulong AmountKey = 0x8113FE7EA2B16969;
        private static readonly HashSet<string> Bonus = new HashSet<string>(StringComparer.Ordinal)
        {
            "2787bee3ef93b23f", "bf62575017fd8a7c", "2787bbe3ef93ad26", "2787c7e3ef93c18a",
            "2787bae3ef93ab73", "2787bce3ef93aed9", "2787c8e3ef93c33d"
        };
        // ss-player-upgrade-path-manager's path-to-manual table, identical in both supported builds.
        private static readonly HashSet<string> Manuals = new HashSet<string>((
            "116353b2ccce482b,1485f600eec2cd27,17e95e53f2bd3736,1cb4067c1e589294,1cb4087c1e5895fa,1cb4097c1e5897ad," +
            "227a2d8497cb4f54,2787b9e3ef93a9c0,2787bae3ef93ab73,2787bbe3ef93ad26,2787bce3ef93aed9,2787bde3ef93b08c," +
            "2787bee3ef93b23f,2787c0e3ef93b5a5,2787c7e3ef93c18a,2787c8e3ef93c33d,2e15d6fbabb53a75,2e2c201b46157b08," +
            "2f3a2595eccdde5b,2f3a2695eccde00e,2f3a2795eccde1c1,3fd45fee7a7b986a,42418fecbfc2baa2,4424c86fb3e9a740," +
            "4424c96fb3e9a8f3,4424ca6fb3e9aaa6,4424cb6fb3e9ac59,4424cd6fb3e9afbf,4424ce6fb3e9b172,4424d66fb3e9bf0a," +
            "4424d76fb3e9c0bd,654afd95437c201b,7687929d79c6bd40,7687939d79c6bef3,76879b9d79c6cc8b,76879c9d79c6ce3e," +
            "76879e9d79c6d1a4,76879f9d79c6d357,7687a09d79c6d50a,7687a19d79c6d6bd,87a912d7db60a132,9ee918e828168396," +
            "9ee919e828168549,9ee91ee828168dc8,9ee91fe828168f7b,9ee920e82816912e,9ee922e828169494,9ee923e828169647," +
            "9ee924e8281697fa,9ee925e8281699ad,a643199c571b34ff,ad649bae8b5d4b47,b43c09ceb6183326,b43c0aceb61834d9," +
            "b4838e6a0f11c975,bf62575017fd8a7c,bf62585017fd8c2f,c8674c79d0b61967,cf17078265fc2fde,d80b06f7fa938693," +
            "de9e7bf3170b57a2,e7ca3044b9e11e1a,f0867bbbeafd3b4f,f476073b9a8af012").Split(','), StringComparer.Ordinal);

        public static bool IsBonus(string item) { return item != null && Bonus.Contains(item); }

        public static Result<SkillStateReading> Read(byte[] raw)
        {
            try
            {
                SaveArena a = new SaveArena(raw);
                Dictionary<ulong, SaveArena.Entry> run = a.Object(a.Container(a.Root, RunKey));
                long character = a.Integer(run, CharacterKey);
                if (character < 0 || character > 31) throw new InvalidDataException("Unknown Rogue character index.");
                var reading = new SkillStateReading
                {
                    CharacterIndex = (int)character,
                    RunId = a.Text(run, RunIdKey),
                    CompletedEncounters = a.Integer(run, CompletedKey),
                    Seed = a.Text(run, SeedKey),
                    Manuals = new List<string>(),
                    Inventories = new List<SkillInventory>()
                };
                if (!reading.IsEllie) return Result<SkillStateReading>.Ok(reading, "skill_state_other_character", "Another character's preparation.");
                if (string.IsNullOrWhiteSpace(reading.RunId) || reading.CompletedEncounters < 0)
                    throw new InvalidDataException("Invalid skill-state run identity.");
                Dictionary<ulong, SaveArena.Entry> checkpoint = a.Object(a.Container(a.Root, CheckpointKey));
                // The checkpoint retains both current gameplay and its gameplay backup. Neither may keep
                // the missing manual: the loader can consume either during its checkpoint rebuild.
                foreach (ulong key in new[] { 0x78F73F17E28699B5UL, 0x37015C928400C25BUL })
                {
                    Dictionary<ulong, SaveArena.Entry> gameplay = a.Object(a.Container(checkpoint, key));
                    Dictionary<ulong, SaveArena.Entry> ellie = a.Object(a.Container(gameplay, EllieKey));
                    int groups = a.Container(ellie, InventoriesKey);
                    int[] inventories = a.List(groups, 6, 3);
                    Dictionary<ulong, SaveArena.Entry> inventory = a.Object(a.Pointer(inventories[0], 0x20));
                    int items = a.Container(inventory, ItemsKey);
                    int[] slots = a.List(items, 6, 72);
                    long occupied = a.Integer(inventory, CountKey);
                    if (occupied < 0 || occupied > slots.Length) throw new InvalidDataException("Invalid persistent item count.");
                    var parsed = new SkillInventory { Count = (int)occupied,
                        CountOffset = inventory[CountKey].Slot, NameOffsets = new int[slots.Length],
                        AmountOffsets = new int[slots.Length], Manuals = new List<string>() };
                    var names = new HashSet<ulong>();
                    for (int index = 0; index < slots.Length; index++)
                    {
                        var item = a.Object(a.Pointer(slots[index], 0x20));
                        ulong name = unchecked((ulong)a.Integer(item, NameKey));
                        long amount = a.Integer(item, AmountKey);
                        if ((index < occupied) != (name != 0) || amount < 0 || amount > short.MaxValue
                            || (name == 0 && amount != 0) || (name != 0 && !names.Add(name)))
                            throw new InvalidDataException("Persistent item occupancy or quantity is invalid.");
                        parsed.NameOffsets[index] = item[NameKey].Slot;
                        parsed.AmountOffsets[index] = item[AmountKey].Slot;
                        if (name == SupplementsKey) parsed.Supplements = (int)amount;
                        if (index >= occupied && (a.Integer(item, 0xF801A98500D5306C) != 0
                            || a.Integer(item, 0x28E984D732353111) != 0))
                            throw new InvalidDataException("An empty persistent slot carries unrelated state.");
                        string id = name.ToString("x16");
                        if (Manuals.Contains(id))
                        {
                            if (amount != 1) throw new InvalidDataException("A training manual must have quantity one.");
                            parsed.Manuals.Add(id);
                        }
                    }
                    parsed.Manuals.Sort(StringComparer.Ordinal);
                    int[] upgradeCopies = a.List(a.Container(ellie, 0xC0225CEDFD536E8D), 6, 2);
                    var upgrades = a.Object(a.Pointer(upgradeCopies[0], 0x20));
                    parsed.UpgradeOffsets = a.List(a.Container(upgrades, 0xDED7279BD0C73D19), 1, 4)
                        .Concat(a.List(a.Container(upgrades, 0xAA508AFFEC4B4190), 1, 12)).ToArray();
                    parsed.Upgrades = string.Concat(parsed.UpgradeOffsets.Select(at =>
                        FileTools.ToHex(raw.Skip(at).Take(8).ToArray())));
                    if (reading.Inventories.Count != 0 && (!reading.Manuals.SequenceEqual(parsed.Manuals)
                        || reading.Supplements != parsed.Supplements || reading.Upgrades != parsed.Upgrades))
                        throw new InvalidDataException("Gameplay copies disagree about skill state.");
                    reading.Manuals = parsed.Manuals;
                    reading.Supplements = parsed.Supplements;
                    reading.Upgrades = parsed.Upgrades;
                    reading.Inventories.Add(parsed);
                }
                if (reading.Manuals.Contains(DefaultManual) == reading.Manuals.Contains(GroundedDefaultManual))
                    throw new InvalidDataException("Ellie's default training manual is absent or ambiguous.");
                return Result<SkillStateReading>.Ok(reading, "skill_state_read", "The saved training manuals were read.");
            }
            catch (Exception exception) when (exception is InvalidDataException || exception is ArgumentException
                || exception is OverflowException || exception is KeyNotFoundException)
            {
                return Result<SkillStateReading>.Fail("skill_state_unreadable", exception.Message);
            }
        }

        public static Result<byte[]> Complete(byte[] original, SkillObservation observed, string profilePath)
        {
            Result<SkillStateReading> parsed = Read(original);
            if (!parsed.Success) return Result<byte[]>.Fail(parsed.Code, parsed.Message);
            SkillStateReading state = parsed.Value;
            if (!state.IsEllie) return Result<byte[]>.Ok(original, "skill_state_kept", "Another character's state was kept.");
            if (observed == null)
                return state.MissingStartingTree
                    ? Result<byte[]>.Fail("skill_state_incomplete", "Ellie's extra starting training manual is absent; no reliable observation is available.")
                    : Result<byte[]>.Ok(original, "skill_state_kept", "The saved training manuals were kept.");
            DateTime time;
            if (observed.Version != 1 || observed.ProcessId <= 0 || !IsHex(observed.BuildSha256, 64)
                || !ulong.TryParse(observed.ProcessBirth, out _)
                || !DateTime.TryParse(observed.ObservedUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out time)
                || (observed.Boundary != "manual" && observed.Boundary != "departure")
                || !string.Equals(Path.GetFullPath(observed.ProfilePath ?? ""), Path.GetFullPath(profilePath), StringComparison.OrdinalIgnoreCase)
                || !string.Equals(observed.OriginalRunSha256, FileTools.Sha256Bytes(original), StringComparison.OrdinalIgnoreCase)
                || observed.CharacterIndex != state.CharacterIndex || observed.RunId != state.RunId
                || observed.CompletedEncounters != state.CompletedEncounters || observed.Seed != state.Seed
                || observed.Difficulty < 0 || observed.Difficulty > 5 || observed.TaskWord != 2
                || string.IsNullOrWhiteSpace(observed.Task) || observed.Supplements != state.Supplements
                || !IsHex(observed.Upgrades, 256) || observed.Manuals == null
                || observed.Manuals.Count > 64 || observed.Manuals.Distinct(StringComparer.Ordinal).Count() != observed.Manuals.Count
                || observed.Manuals.Any(id => !Manuals.Contains(id))
                || observed.Manuals.Contains(DefaultManual) != (observed.Difficulty != 5)
                || observed.Manuals.Contains(GroundedDefaultManual) != (observed.Difficulty == 5))
                return Result<byte[]>.Fail("skill_observation_mismatch", "The skill observation is not bound to this preparation.");
            List<string> expected = observed.Manuals.OrderBy(id => id, StringComparer.Ordinal).ToList();
            if (expected.SequenceEqual(state.Manuals) && string.Equals(observed.Upgrades, state.Upgrades, StringComparison.OrdinalIgnoreCase))
                return state.MissingStartingTree
                    ? Result<byte[]>.Fail("skill_state_incomplete", "The active preparation also lacks Ellie's extra training manual.")
                    : Result<byte[]>.Ok(original, "skill_state_kept", "Observed and saved training manuals agree.");
            string[] missing = expected.Except(state.Manuals).ToArray();
            if (!state.MissingStartingTree || !state.Manuals.Contains(DefaultManual) || missing.Length != 1 || !IsBonus(missing[0])
                || state.Manuals.Except(expected).Any() || expected.Count < 2)
                return Result<byte[]>.Fail("skill_state_conflict", "The observed skill difference is not the verified opening-manual omission.");
            byte[] completed = (byte[])original.Clone();
            // The live hideout can have the manual only in inventory, before the deferred controller sees it.
            // Accept either observed cache shape; purchased bits and every other cache item must agree.
            byte[] upgradeBytes = FileTools.FromHex(state.Upgrades);
            int cacheSlot = -1;
            if (!string.Equals(FileTools.ToHex(upgradeBytes), observed.Upgrades, StringComparison.OrdinalIgnoreCase))
            {
                for (int index = 4; index < 16; index++)
                    if (BitConverter.ToUInt64(upgradeBytes, index * 8) == 0) { cacheSlot = index; break; }
                if (cacheSlot < 0) return Result<byte[]>.Fail("skill_inventory_full", "The deferred upgrade item list is full.");
                Buffer.BlockCopy(BitConverter.GetBytes(Convert.ToUInt64(missing[0], 16)), 0, upgradeBytes, cacheSlot * 8, 8);
                if (!string.Equals(FileTools.ToHex(upgradeBytes), observed.Upgrades, StringComparison.OrdinalIgnoreCase))
                    return Result<byte[]>.Fail("skill_state_conflict", "Purchased upgrades or deferred items differ from this preparation.");
            }
            foreach (SkillInventory inventory in state.Inventories)
            {
                if (inventory.Count == inventory.NameOffsets.Length)
                    return Result<byte[]>.Fail("skill_inventory_full", "No empty persistent inventory slot is available.");
                Put(completed, inventory.NameOffsets[inventory.Count], unchecked((long)Convert.ToUInt64(missing[0], 16)));
                Put(completed, inventory.AmountOffsets[inventory.Count], 1);
                Put(completed, inventory.CountOffset, inventory.Count + 1);
                if (cacheSlot >= 0)
                    Put(completed, inventory.UpgradeOffsets[cacheSlot], unchecked((long)Convert.ToUInt64(missing[0], 16)));
            }
            Result<SkillStateReading> check = Read(completed);
            if (!check.Success || !check.Value.Manuals.SequenceEqual(expected)
                || !string.Equals(check.Value.Upgrades, observed.Upgrades, StringComparison.OrdinalIgnoreCase))
                return Result<byte[]>.Fail("skill_completion_failed", "The original training manual did not read back.");
            return Result<byte[]>.Ok(completed, "skill_state_completed", "The observed opening training manual was added to Guardian's copy.");
        }

        public static bool IsHex(string value, int length)
        {
            return value != null && value.Length == length && value.All(c => c >= '0' && c <= '9'
                || c >= 'a' && c <= 'f' || c >= 'A' && c <= 'F');
        }

        private static void Put(byte[] bytes, int offset, long value)
        {
            Buffer.BlockCopy(BitConverter.GetBytes(value), 0, bytes, offset, 8);
        }
    }

    /// <summary>Bounded reader of the observed working save's serialized arena; offsets are file offsets.</summary>
    internal sealed class SaveArena
    {
        internal sealed class Entry { public byte Kind; public int Slot; }
        private const int Start = 0x590 + 0x418;
        private readonly byte[] _raw;
        private readonly int _length;
        private readonly HashSet<int> _slots = new HashSet<int>();
        private readonly HashSet<int> _nodes = new HashSet<int>();
        public Dictionary<ulong, Entry> Root { get; private set; }

        public SaveArena(byte[] raw)
        {
            _raw = raw;
            if (raw == null || raw.Length < Start + 0x40 || raw.Length > 16 * 1024 * 1024
                || raw[0] != 0x93 || raw[1] != 0xea || raw.Skip(2).Take(14).Any(b => b != 0)
                || BitConverter.ToUInt32(raw, 0x594) != raw.Length - 0x590 || BitConverter.ToUInt32(raw, 0x598) != 27)
                throw new InvalidDataException("Unsupported working run save format.");
            _length = raw.Length - Start;
            if (U32(Start + 0x38) != _length || U64(Start + 0x30) != 0 || (raw[Start] != 5 && raw[Start] != 6))
                throw new InvalidDataException("Invalid serialized arena descriptor.");
            Pointer(Start + 0x10, 1);
            int slot = Pointer(Start + 8, 8);
            Visit(slot, raw[Start], 0);
            Root = Object(Pointer(slot, 0x20));
        }

        private void Visit(int slot, byte kind, int depth)
        {
            Bounds(slot, 8);
            if ((slot - Start) % 8 != 0 || !_slots.Add(slot) || depth > 128 || _slots.Count > 1000000)
                throw new InvalidDataException("Invalid or repeated serialized value ownership.");
            if (kind == 0 || kind == 1 || kind == 2 || kind == 3 || kind == 7) return;
            if (kind == 4)
            {
                if (U64(slot) != 0 && Array.IndexOf(_raw, (byte)0, Pointer(slot, 1)) < 0)
                    throw new InvalidDataException("Unterminated arena string.");
                return;
            }
            if (kind != 5 && kind != 6) throw new InvalidDataException("Unsupported serialized value kind.");
            int node = Pointer(slot, 0x20);
            if (!_nodes.Add(node)) throw new InvalidDataException("Serialized container ownership is repeated.");
            uint count = U32(node + 4);
            if (count > 1000000) throw new InvalidDataException("Serialized traversal budget exceeded.");
            if (U64(node + 24) != 0) Pointer(node + 24, checked((int)Math.Max(1, count * 8)));
            if (count == 0) return;
            int values = Pointer(node + 8, checked((int)count * 8));
            int kinds = Pointer(node + 16, (int)count);
            for (int index = 0; index < count; index++) Visit(values + index * 8, _raw[kinds + index], depth + 1);
        }

        private int Bounds(int offset, int size)
        {
            if (offset < Start || size < 0 || offset > _raw.Length - size)
                throw new InvalidDataException("Save reference lies outside its arena.");
            return offset;
        }
        private ulong U64(int offset)
        {
            if ((offset - Start) % 8 != 0) throw new InvalidDataException("Misaligned save field.");
            return BitConverter.ToUInt64(_raw, Bounds(offset, 8));
        }
        private uint U32(int offset) { return BitConverter.ToUInt32(_raw, Bounds(offset, 4)); }
        public int Pointer(int slot, int size)
        {
            ulong value = U64(slot);
            if (value == 0 || value > (ulong)_length) throw new InvalidDataException("Invalid serialized pointer.");
            return Bounds(checked(Start + (int)value), size);
        }
        public Dictionary<ulong, Entry> Object(int node)
        {
            int count = checked((int)U32(node + 4));
            if (count > 1000000) throw new InvalidDataException("Save object exceeds traversal budget.");
            var result = new Dictionary<ulong, Entry>();
            if (count == 0) return result;
            int values = Pointer(node + 8, checked(count * 8));
            int kinds = Pointer(node + 16, count);
            int keys = Pointer(node + 24, checked(count * 8));
            for (int index = 0; index < count; index++)
            {
                ulong key = U64(keys + index * 8);
                if (result.ContainsKey(key)) throw new InvalidDataException("Repeated key in a save object.");
                result.Add(key, new Entry { Kind = _raw[kinds + index], Slot = values + index * 8 });
            }
            return result;
        }
        public int Container(Dictionary<ulong, Entry> entries, ulong key)
        {
            Entry entry = entries[key];
            if (entry.Kind != 5 && entry.Kind != 6) throw new InvalidDataException("A required save container is absent.");
            return Pointer(entry.Slot, 0x20);
        }
        public int[] List(int node, byte kind, int expectedCount)
        {
            if (U32(node + 4) != expectedCount) throw new InvalidDataException("Unexpected persistent inventory shape.");
            int values = Pointer(node + 8, expectedCount * 8);
            int kinds = Pointer(node + 16, expectedCount);
            if (Enumerable.Range(0, expectedCount).Any(i => _raw[kinds + i] != kind))
                throw new InvalidDataException("Unexpected persistent inventory element type.");
            return Enumerable.Range(0, expectedCount).Select(i => values + i * 8).ToArray();
        }
        public long Integer(Dictionary<ulong, Entry> entries, ulong key)
        {
            Entry entry = entries[key];
            if (entry.Kind != 1) throw new InvalidDataException("A required integer field is absent.");
            return unchecked((long)U64(entry.Slot));
        }
        public string Text(Dictionary<ulong, Entry> entries, ulong key)
        {
            Entry entry = entries[key];
            if (entry.Kind != 4) throw new InvalidDataException("A required text field is absent.");
            int start = Pointer(entry.Slot, 1), end = Array.IndexOf(_raw, (byte)0, start);
            if (end < 0 || end - start > 256) throw new InvalidDataException("Invalid save identity string.");
            return new UTF8Encoding(false, true).GetString(_raw, start, end - start);
        }
    }
}
