using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace NoReturnGuardian.Core
{
    /// <summary>
    /// The original files of an explicitly confirmed manual preparation, as vouched for by the formal
    /// verifier. Repairs belong to Guardian's payload, never to this source identity. This receipt lets a
    /// recorder that arrived mid-hideout freeze that exact source without accepting arbitrary old saves.
    /// </summary>
    public sealed class ManualDepartureSource
    {
        public string SnapshotId { get; set; }
        public string ManifestSha256 { get; set; }
        public string ProfilePath { get; set; }
        public string ConfirmedUtc { get; set; }
        public string CaptureBasis { get; set; }
        public string RunExportFooter { get; set; }
        public string ProfileExportFooter { get; set; }
        public List<SnapshotFileEntry> Files { get; set; }

        public static ManualDepartureSource FromVerified(SnapshotRecord record)
        {
            SnapshotManifest manifest = record.Manifest;
            if (!SnapshotPolicy.IsConfirmedPreparation(record) || manifest.Kind != SnapshotKinds.Manual
                || manifest.PreparationConfirmation != PreparationConfirmations.UserConfirmedHideout)
                return null;
            bool exported = manifest.CaptureBasis == SnapshotCaptureBases.NativeExportedState;
            if (exported && (manifest.ManualNativeSourceEvidence == null
                || manifest.ManualNativeSourceEvidence.Count != 4)
                || !exported && manifest.CaptureBasis != SnapshotCaptureBases.HotSynthesizedState)
                return null;
            var source = new ManualDepartureSource
            {
                SnapshotId = manifest.Id, ManifestSha256 = manifest.ManifestSha256,
                ProfilePath = manifest.SourceProfilePath, ConfirmedUtc = manifest.CreatedUtc,
                CaptureBasis = manifest.CaptureBasis
            };
            if (exported)
            {
                source.RunExportFooter = manifest.SkillProtection == null
                    ? FileTools.ToHex(RunSlotSynthesizer.ObservedFooter(File.ReadAllBytes(
                        Payload(record, SaveLayout.RunDataPath))))
                    : manifest.SkillProtection.NativeExportFooter;
                // Older native captures have the codec's legacy footer. BuildExport preserves that default.
                source.ProfileExportFooter = manifest.ProfileSlotExportFooter;
            }
            source.Files = source.OriginalFiles(record);
            return source;
        }

        public bool IsExport { get { return CaptureBasis == SnapshotCaptureBases.NativeExportedState; } }

        private static string Payload(SnapshotRecord record, string relative)
        {
            return SaveLayout.CombineUnderProfile(Path.Combine(record.DirectoryPath, "payload"), relative);
        }

        private static SnapshotFileEntry Entry(SnapshotRecord record, string relative)
        {
            return record.Manifest.Files.Single(item => string.Equals(item.RelativePath, relative,
                StringComparison.OrdinalIgnoreCase));
        }

        private static SnapshotFileEntry Copy(SnapshotFileEntry entry, string relative = null,
            long? length = null, string hash = null, string written = null)
        {
            return new SnapshotFileEntry { RelativePath = relative ?? entry.RelativePath,
                Length = length ?? entry.Length, Sha256 = hash ?? entry.Sha256,
                LastWriteUtc = written ?? entry.LastWriteUtc };
        }

        private List<SnapshotFileEntry> OriginalFiles(SnapshotRecord record)
        {
            SnapshotManifest manifest = record.Manifest;
            SnapshotFileEntry run = Entry(record, SaveLayout.GameRunPath);
            string originalHash = manifest.SkillProtection != null ? manifest.SkillProtection.OriginalRunSha256
                : manifest.VendorStockRepair != null ? manifest.VendorStockRepair.OriginalRunSha256 : run.Sha256;
            long originalLength = manifest.SkillProtection == null ? run.Length : new FileInfo(Path.Combine(
                record.DirectoryPath, SkillState.OriginalEvidencePath.Replace('/', Path.DirectorySeparatorChar))).Length;
            if (!IsExport)
                return SaveLayout.WorkingProtectionFiles.Select(name =>
                    name == SaveLayout.GameRunPath || name == SaveLayout.GameRunBackupPath
                        ? Copy(Entry(record, name), length: originalLength, hash: originalHash)
                        : Copy(Entry(record, name))).ToList();

            // An export handoff keeps the native envelopes' own footer; normalized synthetic slots must
            // never be mistaken for the original live export. Native sources never return vendor stock.
            if (manifest.VendorStockRepair != null)
                throw new InvalidDataException("A native-export source cannot carry a stock repair.");
            byte[] original = File.ReadAllBytes(manifest.SkillProtection == null
                ? Payload(record, SaveLayout.GameRunPath)
                : Path.Combine(record.DirectoryPath, SkillState.OriginalEvidencePath.Replace('/', Path.DirectorySeparatorChar)));
            byte[] exported;
            string detail, error;
            if (!SkillState.IsHex(RunExportFooter, 72)
                || !RunSlotSynthesizer.TryBuildObservedExport(original, FileTools.FromHex(RunExportFooter),
                    out exported, out detail, out error))
                throw new InvalidDataException("The manual source's original run export cannot be rebuilt.");
            Result<byte[]> profile = ProfileSlotCodec.BuildExport(File.ReadAllBytes(Payload(record, SaveLayout.GameProfilePath)),
                ProfileExportFooter == null ? null : FileTools.FromHex(ProfileExportFooter));
            if (!profile.Success) throw new InvalidDataException(profile.Message);
            string expectedProfileHash = manifest.CaptureBasis == SnapshotCaptureBases.NativeExportedState
                ? manifest.ProfileSlotExportSha256
                : Files == null ? null : Files.Single(item => item.RelativePath == SaveLayout.ProfileDataPath).Sha256;
            if (!SkillState.IsHex(expectedProfileHash, 64)
                || !string.Equals(FileTools.Sha256Bytes(profile.Value), expectedProfileHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The manual source's original 0P export cannot be rebuilt.");
            string runWritten = manifest.CaptureBasis == SnapshotCaptureBases.NativeExportedState
                ? Entry(record, SaveLayout.RunDataPath).LastWriteUtc : run.LastWriteUtc;
            List<SnapshotFileEntry> nativeSourceEvidence = manifest.CaptureBasis == SnapshotCaptureBases.NativeExportedState
                ? manifest.ManualNativeSourceEvidence : Files;
            Dictionary<string, SnapshotFileEntry> nativeEvidence = (nativeSourceEvidence
                ?? new List<SnapshotFileEntry>()).ToDictionary(item => item.RelativePath, StringComparer.OrdinalIgnoreCase);
            SnapshotFileEntry nativeRunParams = nativeEvidence[SaveLayout.RunParamsPath];
            SnapshotFileEntry nativeRunIcon = nativeEvidence[SaveLayout.RunIconPath];
            SnapshotFileEntry nativeProfileParams = nativeEvidence[SaveLayout.ProfileParamsPath];
            SnapshotFileEntry nativeProfileIcon = nativeEvidence[SaveLayout.ProfileIconPath];
            return new List<SnapshotFileEntry>
            {
                new SnapshotFileEntry { RelativePath = SaveLayout.RunDataPath, Length = exported.Length,
                    Sha256 = FileTools.Sha256Bytes(exported), LastWriteUtc = runWritten },
                Copy(nativeRunParams), Copy(nativeRunIcon),
                new SnapshotFileEntry { RelativePath = SaveLayout.ProfileDataPath, Length = profile.Value.Length,
                    Sha256 = expectedProfileHash, LastWriteUtc = null },
                Copy(nativeProfileParams), Copy(nativeProfileIcon), Copy(Entry(record, SaveLayout.MarkerPath))
            }.OrderBy(item => item.RelativePath, StringComparer.Ordinal).ToList();
        }

        public bool MatchesFiles(string profile)
        {
            return Files != null && Files.All(entry =>
            {
                string path = SaveLayout.CombineUnderProfile(profile, entry.RelativePath);
                var info = new FileInfo(path);
                DateTime written;
                return info.Exists && info.Length == entry.Length
                    && (entry.LastWriteUtc == null || DateTime.TryParse(entry.LastWriteUtc, null,
                        DateTimeStyles.RoundtripKind, out written) && info.LastWriteTimeUtc == written.ToUniversalTime())
                    && string.Equals(FileTools.Sha256(path, true), entry.Sha256, StringComparison.OrdinalIgnoreCase);
            });
        }

        public bool MatchesPayload(SnapshotRecord record)
        {
            DateTime confirmed, observed;
            if (SnapshotId == null || !Regex.IsMatch(SnapshotId, @"^\d{8}-\d{9}-manual-[a-f0-9]{8}$")
                || !SkillState.IsHex(ManifestSha256, 64)
                || !string.Equals(Path.GetFullPath(ProfilePath), Path.GetFullPath(record.Manifest.SourceProfilePath),
                    StringComparison.OrdinalIgnoreCase)
                || !DateTime.TryParse(ConfirmedUtc, null, DateTimeStyles.RoundtripKind, out confirmed)
                || !DateTime.TryParse(record.Manifest.DepartureObservedUtc, null, DateTimeStyles.RoundtripKind, out observed)
                || confirmed.ToUniversalTime() >= observed.ToUniversalTime()
                || CaptureBasis != (record.Manifest.DepartureBasis == DepartureEvidence.ConfirmedManualExport
                    ? SnapshotCaptureBases.NativeExportedState : SnapshotCaptureBases.HotSynthesizedState)
                || !IsExport && (RunExportFooter != null || ProfileExportFooter != null)
                || IsExport && (RunExportFooter == null || !SkillState.IsHex(RunExportFooter, 72)
                    || ProfileExportFooter != null && !SkillState.IsHex(ProfileExportFooter, 72)))
                return false;
            List<SnapshotFileEntry> expected = OriginalFiles(record);
            return Files != null && Files.Count == expected.Count && expected.All(wanted =>
                Files.Count(actual => actual != null && actual.RelativePath == wanted.RelativePath
                    && actual.Length == wanted.Length && actual.LastWriteUtc == wanted.LastWriteUtc
                    && string.Equals(actual.Sha256, wanted.Sha256, StringComparison.OrdinalIgnoreCase)) == 1);
        }

        internal void AppendHash(StringBuilder builder)
        {
            // Length-prefix each value: filenames, timestamps and arbitrary profile paths cannot collide.
            foreach (string value in new[] { SnapshotId, ManifestSha256, ProfilePath, ConfirmedUtc, CaptureBasis,
                RunExportFooter, ProfileExportFooter }.Concat((Files ?? new List<SnapshotFileEntry>())
                    .OrderBy(entry => entry == null ? null : entry.RelativePath, StringComparer.Ordinal)
                    .SelectMany(entry => entry == null ? new[] { "<null>" } : new[] { entry.RelativePath,
                        entry.Length.ToString(CultureInfo.InvariantCulture), entry.LastWriteUtc, entry.Sha256 })))
            {
                string text = value ?? "";
                builder.Append(text.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(text);
            }
        }
    }
}
