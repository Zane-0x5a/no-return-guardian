using System;
using System.Collections.Generic;
using System.Linq;

namespace NoReturnGuardian.Core
{
    public static class SnapshotKinds
    {
        public const string Automatic = "automatic";
        public const string Manual = "manual";
        public const string Departure = "departure";
        public const string PreRestore = "pre_restore";

        public static bool IsPreparation(string kind)
        {
            return string.Equals(kind, Manual, StringComparison.OrdinalIgnoreCase)
                || string.Equals(kind, Departure, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static class SnapshotPurposes
    {
        public const string Preparation = "preparation";
        public const string PendingPreparation = "pending_preparation";
        public const string LiveState = "live_state";
    }

    public static class SnapshotCaptureBases
    {
        public const string GamedataWorkingState = "gamedata_working_state";
        public const string TwoPhaseCommittedState = "two_phase_committed_state";
        public const string HotSynthesizedState = "hot_synthesized_state";
        public const string NativeExportedState = "native_exported_state";
        public const string RawLiveState = "raw_live_state";
    }

    public static class PreparationConfirmations
    {
        public const string UserConfirmedHideout = "user_confirmed_hideout";
        public const string NativeDepartureObserved = "native_departure_observed";
    }

    /// <summary>
    /// What the departure recorder saw before an automatic departure capture: the route board's
    /// `player-next-task` in a playable hideout, and the last hideout save the game had finished before
    /// that event, which the recorder froze in Guardian storage while the player was still in the hideout.
    /// </summary>
    public sealed class DepartureEvidence
    {
        public const string RouteBoardEventSid = "A9456E5567D0CE70";
        public const int MaximumRouteIndex = 63;

        /// <summary>
        /// The capture is the hideout save written before the departure. The save the game writes while
        /// handling the departure already has the next encounter's index and the hideout reset for the next
        /// visit (fresh lockbox, route board past this node), so it is no hideout to return to (2026-10-01).
        /// </summary>
        public const string PreDepartureSave = "pre_departure_save";

        /// <summary>
        /// A new run's first hideout: the hub writes its own opening save (run play time 0-1 s) while the hideout
        /// opens and usually nothing else until the departure, so no save postdates the recorder's first sight of
        /// it. The recorder froze that opening save, and the departure save carried the same run (2026-10-11).
        /// </summary>
        public const string RunStartSave = "run_start_save";

        /// <summary>
        /// The same opening save, which the game had already moved into its native slots (no working files left).
        /// The working files are reconstructed from those exports through the verified transforms.
        /// </summary>
        public const string RunStartExport = "run_start_export";

        /// <summary>The run play time, in the state code's last field, of every observed opening save.</summary>
        public const uint RunStartMaximumSeconds = 1;

        public const string StagingDirectoryName = "departure-staging";

        public int RouteIndex { get; set; }
        public DateTime ObservedUtc { get; set; }
        public string RunSha256 { get; set; }

        /// <summary>The recorder's frozen copy of the profile files, under the storage's staging directory.</summary>
        public string StagedPath { get; set; }

        /// <summary>Which save the recorder froze: <see cref="PreDepartureSave"/> unless it is a run's opening save.</summary>
        public string Basis { get; set; }

        /// <summary>A basis whose hideout can be restored: the save before the departure, or a run's opening save.</summary>
        public static bool IsPreparationBasis(string basis)
        {
            return string.Equals(basis, PreDepartureSave, StringComparison.Ordinal) || IsRunStartBasis(basis);
        }

        /// <summary>Optional recorder-created JSON observation kept under departure staging.</summary>
        public string SkillObservationPath { get; set; }

        public static bool IsRunStartBasis(string basis)
        {
            return string.Equals(basis, RunStartSave, StringComparison.Ordinal)
                || string.Equals(basis, RunStartExport, StringComparison.Ordinal);
        }

        /// <summary>Whether a 24-digit state code is a run's opening save (play time within the opening second).</summary>
        public static bool IsRunStartCode(string stateCode)
        {
            uint seconds;
            return stateCode != null
                && stateCode.Length == 24
                && uint.TryParse(
                    stateCode.Substring(16),
                    System.Globalization.NumberStyles.AllowHexSpecifier,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out seconds)
                && seconds <= RunStartMaximumSeconds;
        }
    }

    public static class WorkingRunStateSignals
    {
        public const string Unknown = "unknown";
        public const string ObservedWord0 = "observed_word_00800000";
        public const string ObservedWord1 = "observed_word_00800001";

        public static bool IsRecognized(string signal)
        {
            return string.Equals(signal, ObservedWord0, StringComparison.Ordinal)
                || string.Equals(signal, ObservedWord1, StringComparison.Ordinal);
        }
    }

    public sealed class WorkingRunState
    {
        public string Code { get; set; }
        public string IdentityWord { get; set; }
        public string StateWord { get; set; }
        public uint Counter { get; set; }
        public int Offset { get; set; }
        public string StateSignal { get; set; }
    }

    public sealed class GuardianSettings
    {
        public GuardianSettings()
        {
            AutoMonitor = false;
            AutoProtectOnDeparture = true;
            PollIntervalMs = 1500;
            StabilityDelayMs = 2200;
            AutomaticRetention = 60;
            PanelTransparency = DefaultPanelTransparency;
            CheckForUpdates = true;
        }

        /// <summary>The window's list panel as first designed: a light tint over the frosted field.</summary>
        public const int DefaultPanelTransparency = 60;

        public string ProfilePath { get; set; }
        public string StoragePath { get; set; }
        public string GameExecutablePath { get; set; }
        public bool AutoMonitor { get; set; }
        public bool StartWithWindows { get; set; }
        public bool AutoProtectOnDeparture { get; set; }
        public int PollIntervalMs { get; set; }
        public int StabilityDelayMs { get; set; }
        public int AutomaticRetention { get; set; }
        /// <summary>Player opt-in: after every change keep only the newest three of each kind.</summary>
        public bool AutoCleanup { get; set; }
        /// <summary>Show pre-restore undo points in the list; hidden by default.</summary>
        public bool ShowUndoPoints { get; set; }
        /// <summary>The preparation the player restored last; automatic cleanup never removes it.</summary>
        public string LastRestoredSnapshotId { get; set; }
        /// <summary>How much of the shader field shows through the list panel, 0 (opaque) to 100 (frosting only).</summary>
        public int PanelTransparency { get; set; }
        /// <summary>Ask GitHub for the newest release after every start and every few hours, and offer a newer one in the window and the tray.</summary>
        public bool CheckForUpdates { get; set; }
        /// <summary>The newest published release seen by the last successful check, as major.minor.patch.</summary>
        public string LatestRelease { get; set; }
        /// <summary>The release the tray has already announced, so each version is announced once.</summary>
        public string AnnouncedRelease { get; set; }
        /// <summary>The release whose installer Guardian started; the next start tells the player whether it took.</summary>
        public string UpdatingTo { get; set; }
    }

    public sealed class SnapshotFileEntry
    {
        public string RelativePath { get; set; }
        public long Length { get; set; }
        public string LastWriteUtc { get; set; }
        public string Sha256 { get; set; }
    }

    public sealed class SnapshotManifest
    {
        public SnapshotManifest()
        {
            SchemaVersion = 5;
            Files = new List<SnapshotFileEntry>();
        }

        public int SchemaVersion { get; set; }
        public string Id { get; set; }
        public string CreatedUtc { get; set; }
        public string Kind { get; set; }
        public string Label { get; set; }
        public string SourceProfilePath { get; set; }
        public string Purpose { get; set; }
        public string CaptureBasis { get; set; }
        public string PreparationConfirmation { get; set; }
        public string Marker { get; set; }
        public string RunStateCode { get; set; }
        public long RunDataLength { get; set; }
        public long WorkingRunDataLength { get; set; }
        public long WorkingRunBackupLength { get; set; }
        public int MatchingBackupCount { get; set; }
        public long CoreWriteSkewMs { get; set; }
        public bool WorkingCopyShapeMatches { get; set; }
        public bool WorkingRunMirrorsMatch { get; set; }
        public bool WorkingProfileMirrorsMatch { get; set; }
        public string ProfileSlotStateCode { get; set; }
        public string ProfileSlotExportSha256 { get; set; }
        /// <summary>
        /// Hex of the captured 0P export's trailing 36 bytes, which the exported hash covers but the working file
        /// cannot reproduce. Absent on captures before 2026-10-17, whose exports all carried the first observed
        /// footer.
        /// </summary>
        public string ProfileSlotExportFooter { get; set; }
        public string DepartureEventSid { get; set; }
        public int? DepartureRouteIndex { get; set; }
        public string DepartureObservedUtc { get; set; }
        public string DepartureRunSha256 { get; set; }
        /// <summary>
        /// <see cref="DepartureEvidence.PreDepartureSave"/> for current captures, or one of the run-start bases for
        /// a new run's first hideout. Absent on the first departure captures, which bound the save the game wrote
        /// after the departure (see there).
        /// </summary>
        public string DepartureBasis { get; set; }
        /// <summary>Recorder-side event observation used to complete a verified Ellie opening tree.</summary>
        public SkillProtection SkillProtection { get; set; }
        /// <summary>
        /// Present when the captured run save had lost its trading-station stock and the stock of an earlier
        /// save of the same draw was written back into Guardian's copy (see <see cref="VendorStockRepair"/>).
        /// </summary>
        public VendorStockRepair VendorStockRepair { get; set; }
        public bool Complete { get; set; }
        public string CompositeSha256 { get; set; }
        public string ManifestSha256 { get; set; }
        public List<SnapshotFileEntry> Files { get; set; }
    }

    public sealed class SnapshotRecord
    {
        public SnapshotManifest Manifest { get; set; }
        public string DirectoryPath { get; set; }
        public bool StructurallyValid { get; set; }
        public string StatusMessage { get; set; }
        public string WorkingRunStateCode { get; set; }
        public string WorkingRunStateWord { get; set; }
        public uint WorkingRunCounter { get; set; }
        public int WorkingRunStateOffset { get; set; }
        public string WorkingRunStateSignal { get; set; }
        public bool SkillStateUnavailable { get; set; }

        public DateTime CreatedUtc
        {
            get
            {
                DateTime value;
                return DateTime.TryParse(
                    Manifest == null ? null : Manifest.CreatedUtc,
                    null,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out value)
                    ? value.ToUniversalTime()
                    : DateTime.MinValue;
            }
        }
    }

    public static class SnapshotPolicy
    {
        /// <summary>The snapshot is a hideout the player can be returned to, by file or native restore.</summary>
        public static bool IsConfirmedPreparation(SnapshotRecord record)
        {
            return HasRecognizedRunState(record) && IsConfirmedPreparation(record.Manifest);
        }

        public static bool IsConfirmedPreparation(SnapshotManifest manifest)
        {
            return HasCoherentWorkingStateEvidence(manifest)
                && manifest.SchemaVersion >= 4
                && (string.Equals(
                        manifest.Kind,
                        SnapshotKinds.Manual,
                        StringComparison.OrdinalIgnoreCase)
                    && string.Equals(
                        manifest.PreparationConfirmation,
                        PreparationConfirmations.UserConfirmedHideout,
                        StringComparison.Ordinal)
                    || DepartureEvidenceMatches(manifest)
                    && DepartureEvidence.IsPreparationBasis(manifest.DepartureBasis));
        }

        /// <summary>
        /// An early departure capture of the save the game wrote while handling the departure. Rebuilding
        /// its hideout shows a broken route board and a refilled lockbox, so it never restores a hideout;
        /// it only starts its own encounter again, by replaying the recorded departure straight away.
        /// </summary>
        public static bool IsPostDepartureSave(SnapshotRecord record)
        {
            return HasRecognizedRunState(record)
                && HasCoherentWorkingStateEvidence(record.Manifest)
                && DepartureEvidenceMatches(record.Manifest)
                && record.Manifest.DepartureBasis == null;
        }

        /// <summary>The snapshot's own encounter can be started again (with a recorded departure).</summary>
        public static bool IsRedeployablePreparation(SnapshotRecord record)
        {
            return IsConfirmedPreparation(record) || IsPostDepartureSave(record);
        }

        /// <summary>
        /// How a snapshot may be used, as one word for the native scripts, which take it from VerifySnapshot
        /// instead of deciding it again: "hideout" returns the player to its hideout, "restart" only starts
        /// its own encounter again, "none" neither.
        /// </summary>
        public static string Use(SnapshotRecord record)
        {
            return IsConfirmedPreparation(record) ? "hideout" : IsPostDepartureSave(record) ? "restart" : "none";
        }

        private static bool HasRecognizedRunState(SnapshotRecord record)
        {
            return record != null
                && record.StructurallyValid
                && !record.SkillStateUnavailable
                && record.Manifest != null
                && WorkingRunStateSignals.IsRecognized(record.WorkingRunStateSignal)
                && string.Equals(
                    record.WorkingRunStateCode,
                    record.Manifest.RunStateCode,
                    StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Structural check: the confirmation is a known scene assertion with its evidence.</summary>
        public static bool HasValidConfirmationEvidence(SnapshotManifest manifest)
        {
            return manifest != null
                && (string.Equals(
                        manifest.PreparationConfirmation,
                        PreparationConfirmations.UserConfirmedHideout,
                        StringComparison.Ordinal)
                    || DepartureEvidenceMatches(manifest));
        }

        /// <summary>
        /// A departure capture is only a hot working-state copy whose R0A is the exact hideout save the
        /// recorder bound when the route board's event was observed. Its basis says which save that was; a
        /// run-start basis must hold a run's opening save.
        /// </summary>
        public static bool DepartureEvidenceMatches(SnapshotManifest manifest)
        {
            DateTime observed;
            SnapshotFileEntry run = manifest == null
                ? null
                : (manifest.Files ?? new List<SnapshotFileEntry>()).FirstOrDefault(item => string.Equals(
                    SaveLayout.NormalizeRelativePath(item == null ? null : item.RelativePath),
                    SaveLayout.GameRunPath,
                    StringComparison.OrdinalIgnoreCase));
            return manifest != null
                && manifest.SchemaVersion >= 5
                && string.Equals(
                    manifest.PreparationConfirmation,
                    PreparationConfirmations.NativeDepartureObserved,
                    StringComparison.Ordinal)
                && string.Equals(
                    manifest.Kind,
                    SnapshotKinds.Departure,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    manifest.CaptureBasis,
                    SnapshotCaptureBases.HotSynthesizedState,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    manifest.DepartureEventSid,
                    DepartureEvidence.RouteBoardEventSid,
                    StringComparison.Ordinal)
                && manifest.DepartureRouteIndex.HasValue
                && manifest.DepartureRouteIndex.Value >= 0
                && manifest.DepartureRouteIndex.Value <= DepartureEvidence.MaximumRouteIndex
                && DateTime.TryParse(
                    manifest.DepartureObservedUtc,
                    null,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out observed)
                && run != null
                && !string.IsNullOrWhiteSpace(manifest.DepartureRunSha256)
                && manifest.DepartureRunSha256.Length == 64
                // The bound save is the copy before a lost trading-station stock was returned into it.
                && string.Equals(
                    manifest.DepartureRunSha256,
                    BoundRunHash(manifest, run.Sha256),
                    StringComparison.OrdinalIgnoreCase)
                && (manifest.DepartureBasis == null
                    || string.Equals(manifest.DepartureBasis, DepartureEvidence.PreDepartureSave, StringComparison.Ordinal)
                    || DepartureEvidence.IsRunStartBasis(manifest.DepartureBasis)
                    && DepartureEvidence.IsRunStartCode(manifest.RunStateCode));
        }

        public static bool HasCoherentWorkingStateEvidence(SnapshotManifest manifest)
        {
            return manifest != null
                && manifest.SchemaVersion >= 4
                && manifest.Complete
                && string.Equals(
                    manifest.Purpose,
                    SnapshotPurposes.Preparation,
                    StringComparison.OrdinalIgnoreCase)
                && IsRestorableCaptureBasis(manifest.CaptureBasis)
                && !string.IsNullOrWhiteSpace(manifest.Marker)
                && !string.IsNullOrWhiteSpace(manifest.RunStateCode)
                && manifest.RunStateCode.Length == 24
                && manifest.WorkingRunDataLength > 1024
                && manifest.WorkingRunBackupLength > 1024
                && manifest.WorkingRunDataLength == manifest.WorkingRunBackupLength
                && manifest.RunDataLength
                    == manifest.WorkingRunDataLength + SaveLayout.SlotEnvelopeLength
                && manifest.WorkingCopyShapeMatches
                && manifest.WorkingRunMirrorsMatch
                && manifest.WorkingProfileMirrorsMatch
                && ManifestLengthMatches(
                    manifest,
                    SaveLayout.RunDataPath,
                    manifest.RunDataLength)
                && ManifestLengthMatches(
                    manifest,
                    SaveLayout.GameRunPath,
                    manifest.WorkingRunDataLength)
                && ManifestLengthMatches(
                    manifest,
                    SaveLayout.GameRunBackupPath,
                    manifest.WorkingRunBackupLength)
                && ManifestMirrorMatches(
                    manifest,
                    SaveLayout.GameRunPath,
                    SaveLayout.GameRunBackupPath)
                && ManifestMirrorMatches(
                    manifest,
                    SaveLayout.GameProfilePath,
                    SaveLayout.GameProfileBackupPath)
                && NativeExportEvidenceMatches(manifest)
                && (manifest.SchemaVersion >= 5 || manifest.MatchingBackupCount >= 2)
                && manifest.CoreWriteSkewMs >= 0
                && manifest.CoreWriteSkewMs <= 30000;
        }

        private static bool IsRestorableCaptureBasis(string captureBasis)
        {
            return string.Equals(
                    captureBasis,
                    SnapshotCaptureBases.TwoPhaseCommittedState,
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    captureBasis,
                    SnapshotCaptureBases.HotSynthesizedState,
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    captureBasis,
                    SnapshotCaptureBases.NativeExportedState,
                    StringComparison.OrdinalIgnoreCase);
        }

        private static bool NativeExportEvidenceMatches(SnapshotManifest manifest)
        {
            bool nativeExport = string.Equals(
                manifest.CaptureBasis,
                SnapshotCaptureBases.NativeExportedState,
                StringComparison.OrdinalIgnoreCase);
            if (manifest.SchemaVersion < 6)
            {
                return !nativeExport;
            }

            return nativeExport
                ? !string.IsNullOrWhiteSpace(manifest.ProfileSlotStateCode)
                    && manifest.ProfileSlotStateCode.Length == 24
                    && !string.IsNullOrWhiteSpace(manifest.ProfileSlotExportSha256)
                    && manifest.ProfileSlotExportSha256.Length == 64
                : manifest.SchemaVersion >= 7;
        }

        private static string BoundRunHash(SnapshotManifest manifest, string fallback)
        {
            if (manifest.SkillProtection != null
                && !string.IsNullOrWhiteSpace(manifest.SkillProtection.OriginalRunSha256))
            {
                return manifest.SkillProtection.OriginalRunSha256;
            }

            return manifest.VendorStockRepair == null
                ? fallback
                : manifest.VendorStockRepair.OriginalRunSha256;
        }

        private static bool ManifestLengthMatches(
            SnapshotManifest manifest,
            string relativePath,
            long expectedLength)
        {
            SnapshotFileEntry entry = (manifest.Files ?? new List<SnapshotFileEntry>())
                .FirstOrDefault(item => string.Equals(
                    SaveLayout.NormalizeRelativePath(
                        item == null ? null : item.RelativePath),
                    relativePath,
                    StringComparison.OrdinalIgnoreCase));
            return entry != null && entry.Length == expectedLength;
        }

        private static bool ManifestMirrorMatches(
            SnapshotManifest manifest,
            string firstPath,
            string secondPath)
        {
            SnapshotFileEntry first = null;
            SnapshotFileEntry second = null;
            foreach (SnapshotFileEntry entry in manifest.Files ?? new List<SnapshotFileEntry>())
            {
                string path = SaveLayout.NormalizeRelativePath(
                    entry == null ? null : entry.RelativePath);
                if (string.Equals(path, firstPath, StringComparison.OrdinalIgnoreCase))
                {
                    first = entry;
                }
                else if (string.Equals(path, secondPath, StringComparison.OrdinalIgnoreCase))
                {
                    second = entry;
                }
            }

            return first != null
                && second != null
                && first.Length == second.Length
                && !string.IsNullOrWhiteSpace(first.Sha256)
                && string.Equals(first.Sha256, second.Sha256, StringComparison.OrdinalIgnoreCase);
        }
    }

    public sealed class RunSlotMetadata
    {
        public string title { get; set; }
        public string subTitle { get; set; }
        public string detail { get; set; }
        public long userParam { get; set; }
        public long mtime { get; set; }
    }

    public sealed class DecodedSlotPayload
    {
        public byte[] RawBytes { get; set; }
        public string Detail { get; set; }
        public string StateCode { get; set; }
        public string ExportSha256 { get; set; }
        /// <summary>
        /// The export's trailing 36 bytes. The game leaves whatever its buffer held there (all zero, or the
        /// tail of an earlier save); the CRC32C does not cover them, so they are kept rather than modeled.
        /// </summary>
        public byte[] Footer { get; set; }
    }

    public sealed class FileStamp
    {
        public string RelativePath { get; set; }
        public long Length { get; set; }
        public long LastWriteUtcTicks { get; set; }

        public bool SameAs(FileStamp other)
        {
            return other != null
                && string.Equals(RelativePath, other.RelativePath, StringComparison.OrdinalIgnoreCase)
                && Length == other.Length
                && LastWriteUtcTicks == other.LastWriteUtcTicks;
        }
    }

    public sealed class RunStateDescriptor
    {
        public RunStateDescriptor()
        {
            Files = new List<FileStamp>();
            NativeExportFiles = new List<FileStamp>();
        }

        public string ProfilePath { get; set; }
        public bool Complete { get; set; }
        public string Marker { get; set; }
        public string RunStateCode { get; set; }
        public long RunDataLength { get; set; }
        public long WorkingRunDataLength { get; set; }
        public long WorkingRunBackupLength { get; set; }
        public bool EmptyRunSlot { get; set; }
        public int MatchingBackupCount { get; set; }
        public string BackupEvidenceSignature { get; set; }
        public long CoreWriteSkewMs { get; set; }
        public bool WorkingCopyShapeMatches { get; set; }
        public bool WorkingRunMirrorsMatch { get; set; }
        public bool WorkingProfileMirrorsMatch { get; set; }
        public string WorkingRunStateCode { get; set; }
        public string WorkingRunStateWord { get; set; }
        public uint WorkingRunCounter { get; set; }
        public int WorkingRunStateOffset { get; set; }
        public string WorkingRunStateSignal { get; set; }
        public bool ManualCaptureReady { get; set; }
        public string ManualCaptureStatus { get; set; }
        public bool CommittedCaptureReady { get; set; }
        public string CommittedCaptureStatus { get; set; }
        public bool WorkingCaptureReady { get; set; }
        public bool NativeExportCaptureReady { get; set; }
        public string NativeExportCaptureStatus { get; set; }
        public string ManualCaptureBasis { get; set; }
        public string ProfileSlotStateCode { get; set; }
        public string ProfileSlotExportSha256 { get; set; }
        public long NativeExportWriteSkewMs { get; set; }
        public string QuickSignature { get; set; }
        public List<FileStamp> Files { get; set; }
        public List<FileStamp> NativeExportFiles { get; set; }

        public bool SameStamps(RunStateDescriptor other)
        {
            if (other == null || Files.Count != other.Files.Count)
            {
                return false;
            }

            for (int i = 0; i < Files.Count; i++)
            {
                if (!Files[i].SameAs(other.Files[i]))
                {
                    return false;
                }
            }

            return true;
        }

        public bool SameCaptureState(RunStateDescriptor other)
        {
            return other != null
                && SameStamps(other)
                && NativeStampStateMatches(other)
                && string.Equals(RunStateCode, other.RunStateCode, StringComparison.Ordinal)
                && RunDataLength == other.RunDataLength
                && WorkingRunDataLength == other.WorkingRunDataLength
                && WorkingRunBackupLength == other.WorkingRunBackupLength
                && MatchingBackupCount == other.MatchingBackupCount
                && string.Equals(
                    BackupEvidenceSignature,
                    other.BackupEvidenceSignature,
                    StringComparison.Ordinal)
                && CoreWriteSkewMs == other.CoreWriteSkewMs
                && WorkingCopyShapeMatches == other.WorkingCopyShapeMatches
                && WorkingRunMirrorsMatch == other.WorkingRunMirrorsMatch
                && WorkingProfileMirrorsMatch == other.WorkingProfileMirrorsMatch
                && string.Equals(
                    WorkingRunStateCode,
                    other.WorkingRunStateCode,
                    StringComparison.Ordinal)
                && string.Equals(
                    WorkingRunStateSignal,
                    other.WorkingRunStateSignal,
                    StringComparison.Ordinal)
                && ManualCaptureReady == other.ManualCaptureReady
                && CommittedCaptureReady == other.CommittedCaptureReady
                && WorkingCaptureReady == other.WorkingCaptureReady
                && NativeExportCaptureReady == other.NativeExportCaptureReady
                && string.Equals(
                    ManualCaptureBasis,
                    other.ManualCaptureBasis,
                    StringComparison.Ordinal)
                && NativeEvidenceStateMatches(other);
        }

        private bool NativeStampStateMatches(RunStateDescriptor other)
        {
            bool firstNative = string.Equals(
                ManualCaptureBasis,
                SnapshotCaptureBases.NativeExportedState,
                StringComparison.OrdinalIgnoreCase);
            bool secondNative = string.Equals(
                other == null ? null : other.ManualCaptureBasis,
                SnapshotCaptureBases.NativeExportedState,
                StringComparison.OrdinalIgnoreCase);
            return !firstNative && !secondNative
                || firstNative && secondNative
                    && SameStampList(NativeExportFiles, other.NativeExportFiles);
        }

        private bool NativeEvidenceStateMatches(RunStateDescriptor other)
        {
            if (!string.Equals(
                ManualCaptureBasis,
                SnapshotCaptureBases.NativeExportedState,
                StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return string.Equals(
                    ProfileSlotStateCode,
                    other.ProfileSlotStateCode,
                    StringComparison.Ordinal)
                && string.Equals(
                    ProfileSlotExportSha256,
                    other.ProfileSlotExportSha256,
                    StringComparison.Ordinal)
                && NativeExportWriteSkewMs == other.NativeExportWriteSkewMs;
        }

        private static bool SameStampList(
            IList<FileStamp> first,
            IList<FileStamp> second)
        {
            first = first ?? new List<FileStamp>();
            second = second ?? new List<FileStamp>();
            if (first.Count != second.Count)
            {
                return false;
            }

            for (int index = 0; index < first.Count; index++)
            {
                if (!first[index].SameAs(second[index]))
                {
                    return false;
                }
            }

            return true;
        }
    }

    public sealed class CaptureOptions
    {
        public CaptureOptions()
        {
            Kind = SnapshotKinds.Manual;
        }

        public string Kind { get; set; }
        public string Purpose { get; set; }
        public string Label { get; set; }
        public bool AllowIncomplete { get; set; }
        public bool UserConfirmedPreparation { get; set; }
        public bool SkipDeduplication { get; set; }
        public bool SkipRetention { get; set; }
    }

    public sealed class RestoreJournal
    {
        public RestoreJournal()
        {
            CompletedFiles = new List<string>();
        }

        public int SchemaVersion { get; set; }
        public string StartedUtc { get; set; }
        public string ProfilePath { get; set; }
        public string TargetSnapshotId { get; set; }
        public string UndoSnapshotId { get; set; }
        public string State { get; set; }
        public List<string> CompletedFiles { get; set; }
    }

    public sealed class RestoreOutcome
    {
        public string RestoredSnapshotId { get; set; }
        public string UndoSnapshotId { get; set; }
        public bool RolledBackAfterFailure { get; set; }
    }

    /// <summary>One profile's snapshots split into the newest per kind and everything older.</summary>
    public sealed class SnapshotCleanupPlan
    {
        public SnapshotCleanupPlan()
        {
            KeptPreparations = new List<SnapshotRecord>();
            KeptLiveStates = new List<SnapshotRecord>();
            KeptPinned = new List<SnapshotRecord>();
            Pinned = new HashSet<string>(StringComparer.Ordinal);
            Removals = new List<SnapshotRecord>();
        }

        public string ProfilePath { get; set; }
        public int KeepPerKind { get; set; }
        public List<SnapshotRecord> KeptPreparations { get; private set; }
        public List<SnapshotRecord> KeptLiveStates { get; private set; }
        /// <summary>Older snapshots kept only because they are pinned (in use).</summary>
        public List<SnapshotRecord> KeptPinned { get; private set; }
        public HashSet<string> Pinned { get; private set; }
        public List<SnapshotRecord> Removals { get; private set; }
        public int RemovedPreparations { get; set; }
        public int RemovedLiveStates { get; set; }
        public int RemovedOther { get; set; }
        public long RemovalBytes { get; set; }

        public bool SameRemovals(SnapshotCleanupPlan other)
        {
            return other != null
                && Ids(Removals).SetEquals(Ids(other.Removals))
                && Ids(KeptPreparations.Concat(KeptLiveStates).Concat(KeptPinned))
                    .SetEquals(Ids(other.KeptPreparations.Concat(other.KeptLiveStates).Concat(other.KeptPinned)));
        }

        private static HashSet<string> Ids(IEnumerable<SnapshotRecord> records)
        {
            return new HashSet<string>(
                records.Select(record => record.Manifest.Id),
                StringComparer.Ordinal);
        }
    }

    public sealed class SnapshotCleanupOutcome
    {
        public int Removed { get; set; }
        public int Failed { get; set; }
        public long FreedBytes { get; set; }
    }

    public sealed class MonitorSnapshotEventArgs : EventArgs
    {
        public SnapshotRecord Snapshot { get; set; }
        public bool Manual { get; set; }
    }

    public sealed class MonitorStatus
    {
        public bool GameRunning { get; set; }
        public string GameExecutablePath { get; set; }
        public bool RunStateComplete { get; set; }
        public bool ManualCaptureReady { get; set; }
        public string ManualCaptureStatus { get; set; }
        public string ManualCaptureBasis { get; set; }
        public bool PendingProtection { get; set; }
        public bool SuspectedRunEnded { get; set; }
        public string Marker { get; set; }
        public string WorkingRunStateCode { get; set; }
        public string WorkingRunStateWord { get; set; }
        public uint WorkingRunCounter { get; set; }
        public string WorkingRunStateSignal { get; set; }
        public string LastProtectedSnapshotId { get; set; }
        public string PreferredRestoreSnapshotId { get; set; }
        public string Message { get; set; }
    }

    public sealed class Result<T>
    {
        public bool Success { get; private set; }
        public string Code { get; private set; }
        public string Message { get; private set; }
        public T Value { get; private set; }

        public static Result<T> Ok(T value, string code, string message)
        {
            return new Result<T>
            {
                Success = true,
                Code = code,
                Message = message,
                Value = value
            };
        }

        public static Result<T> Fail(string code, string message)
        {
            return new Result<T>
            {
                Success = false,
                Code = code,
                Message = message,
                Value = default(T)
            };
        }
    }

    public interface IClock
    {
        DateTime UtcNow { get; }
    }

    public sealed class SystemClock : IClock
    {
        public DateTime UtcNow { get { return DateTime.UtcNow; } }
    }

    public interface IGameProcessProbe
    {
        bool IsGameRunning();
        string FindGameExecutablePath();
    }

    public sealed class SnapshotHooks
    {
        public Action AfterPayloadCopied { get; set; }
    }

    public sealed class RestoreHooks
    {
        public Action<string, int> AfterFileApplied { get; set; }
    }
}
