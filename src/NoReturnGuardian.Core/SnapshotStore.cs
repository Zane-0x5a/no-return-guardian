using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NoReturnGuardian.Core
{
    public sealed class SnapshotStore
    {
        private readonly object _gate = new object();
        private readonly IClock _clock;

        public SnapshotStore(string storageRoot, IClock clock)
        {
            if (string.IsNullOrWhiteSpace(storageRoot))
            {
                throw new ArgumentException("Storage root is empty.", "storageRoot");
            }

            StorageRoot = Path.GetFullPath(storageRoot);
            SnapshotsRoot = Path.Combine(StorageRoot, "snapshots");
            DeparturesRoot = Path.Combine(StorageRoot, "departures");
            JournalPath = Path.Combine(StorageRoot, "restore-journal.json");
            _clock = clock ?? new SystemClock();
            AutomaticRetention = 60;
            Directory.CreateDirectory(SnapshotsRoot);
        }

        public string StorageRoot { get; private set; }
        public string SnapshotsRoot { get; private set; }
        /// <summary>The departure recorder's per-snapshot route-board records; removed with their snapshot.</summary>
        public string DeparturesRoot { get; private set; }
        public string JournalPath { get; private set; }
        public int AutomaticRetention { get; set; }
        public SnapshotHooks CaptureHooks { get; set; }

        public Result<SnapshotRecord> Capture(string profilePath, CaptureOptions options)
        {
            lock (_gate)
            {
                options = options ?? new CaptureOptions();
                string pendingPath = null;
                try
                {
                    bool preparationRequest = string.Equals(
                        options.Purpose,
                        SnapshotPurposes.Preparation,
                        StringComparison.OrdinalIgnoreCase);
                    Result<RunStateDescriptor> beforeResult = RunStateProbe.Inspect(
                        profilePath,
                        options.AllowIncomplete || preparationRequest);
                    if (!beforeResult.Success)
                    {
                        return Result<SnapshotRecord>.Fail(
                            beforeResult.Code,
                            beforeResult.Message);
                    }

                    RunStateDescriptor before = beforeResult.Value;
                    string purpose = NormalizePurpose(options.Purpose, options.Kind);
                    if (string.IsNullOrWhiteSpace(purpose))
                    {
                        return Result<SnapshotRecord>.Fail(
                            "snapshot_purpose_missing",
                            "The snapshot purpose was not specified.");
                    }

                    if (string.Equals(
                            purpose,
                            SnapshotPurposes.Preparation,
                            StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(
                            options.Kind,
                            SnapshotKinds.Manual,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return Result<SnapshotRecord>.Fail(
                            "automatic_capture_disabled",
                            "Only an explicit manual hideout capture can create a preparation snapshot.");
                    }

                    if (string.Equals(
                            purpose,
                            SnapshotPurposes.Preparation,
                            StringComparison.OrdinalIgnoreCase)
                        && !options.UserConfirmedPreparation)
                    {
                        return Result<SnapshotRecord>.Fail(
                            "preparation_confirmation_required",
                            "The player must explicitly confirm that the game is in the hideout.");
                    }

                    if (string.Equals(
                        purpose,
                        SnapshotPurposes.Preparation,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        return CaptureHotPreparation(
                            profilePath,
                            options.Label,
                            options.UserConfirmedPreparation);
                    }

                    if (string.Equals(
                            purpose,
                            SnapshotPurposes.Preparation,
                            StringComparison.OrdinalIgnoreCase)
                        && !before.ManualCaptureReady)
                    {
                        return Result<SnapshotRecord>.Fail(
                            before.ManualCaptureStatus ?? "manual_capture_not_ready",
                            "The current files do not satisfy the manual capture gate.");
                    }

                    string id = BuildSnapshotId(options.Kind);
                    pendingPath = Path.Combine(SnapshotsRoot, ".pending-" + id);
                    string payloadRoot = Path.Combine(pendingPath, "payload");
                    Directory.CreateDirectory(payloadRoot);

                    foreach (FileStamp stamp in before.Files)
                    {
                        string source = SaveLayout.CombineUnderProfile(
                            profilePath,
                            stamp.RelativePath);
                        string destination = PayloadPath(pendingPath, stamp.RelativePath);
                        FileTools.CopyShared(source, destination);
                    }

                    if (CaptureHooks != null && CaptureHooks.AfterPayloadCopied != null)
                    {
                        CaptureHooks.AfterPayloadCopied();
                    }

                    Result<RunStateDescriptor> afterResult = RunStateProbe.Inspect(
                        profilePath,
                        options.AllowIncomplete);
                    if (!afterResult.Success || !before.SameCaptureState(afterResult.Value))
                    {
                        return Result<SnapshotRecord>.Fail(
                            "source_changed",
                            "The game save changed while the snapshot was being copied. It was not accepted.");
                    }

                    List<SnapshotFileEntry> entries = new List<SnapshotFileEntry>();
                    foreach (FileStamp stamp in before.Files)
                    {
                        string source = SaveLayout.CombineUnderProfile(
                            profilePath,
                            stamp.RelativePath);
                        string payload = PayloadPath(pendingPath, stamp.RelativePath);
                        string sourceHash = FileTools.Sha256(source, true);
                        string payloadHash = FileTools.Sha256(payload, false);
                        if (!string.Equals(sourceHash, payloadHash, StringComparison.OrdinalIgnoreCase))
                        {
                            return Result<SnapshotRecord>.Fail(
                                "copy_hash_mismatch",
                                "A copied save file did not match the live source.");
                        }

                        entries.Add(new SnapshotFileEntry
                        {
                            RelativePath = SaveLayout.NormalizeRelativePath(stamp.RelativePath),
                            Length = stamp.Length,
                            LastWriteUtc = new DateTime(
                                stamp.LastWriteUtcTicks,
                                DateTimeKind.Utc).ToString("O"),
                            Sha256 = payloadHash
                        });
                    }

                    Result<RunStateDescriptor> finalResult = RunStateProbe.Inspect(
                        profilePath,
                        options.AllowIncomplete);
                    if (!finalResult.Success || !before.SameCaptureState(finalResult.Value))
                    {
                        return Result<SnapshotRecord>.Fail(
                            "source_changed_after_hash",
                            "The game save changed before snapshot validation finished.");
                    }

                    SnapshotManifest manifest = new SnapshotManifest
                    {
                        Id = id,
                        CreatedUtc = _clock.UtcNow.ToUniversalTime().ToString("O"),
                        Kind = NormalizeKind(options.Kind),
                        Label = options.Label ?? string.Empty,
                        SourceProfilePath = before.ProfilePath,
                        Purpose = purpose,
                        CaptureBasis = string.Equals(
                            purpose,
                            SnapshotPurposes.Preparation,
                            StringComparison.OrdinalIgnoreCase)
                            ? SnapshotCaptureBases.GamedataWorkingState
                            : SnapshotCaptureBases.RawLiveState,
                        PreparationConfirmation = string.Equals(
                            purpose,
                            SnapshotPurposes.Preparation,
                            StringComparison.OrdinalIgnoreCase)
                            ? PreparationConfirmations.UserConfirmedHideout
                            : null,
                        Marker = ReadPayloadMarker(pendingPath, entries),
                        RunStateCode = before.RunStateCode,
                        RunDataLength = before.RunDataLength,
                        WorkingRunDataLength = before.WorkingRunDataLength,
                        WorkingRunBackupLength = before.WorkingRunBackupLength,
                        MatchingBackupCount = before.MatchingBackupCount,
                        CoreWriteSkewMs = before.CoreWriteSkewMs,
                        WorkingCopyShapeMatches = before.WorkingCopyShapeMatches,
                        WorkingRunMirrorsMatch = before.WorkingRunMirrorsMatch,
                        WorkingProfileMirrorsMatch = before.WorkingProfileMirrorsMatch,
                        Complete = before.Complete,
                        Files = entries.OrderBy(
                            item => item.RelativePath,
                            StringComparer.OrdinalIgnoreCase).ToList()
                    };
                    manifest.CompositeSha256 = FileTools.CompositeHash(manifest.Files);
                    manifest.ManifestSha256 = FileTools.ManifestHash(manifest);

                    if (!options.SkipDeduplication
                        && string.Equals(
                            manifest.Kind,
                            SnapshotKinds.Automatic,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        SnapshotRecord duplicate = ListSnapshotsInternal().FirstOrDefault(
                            item => item.StructurallyValid
                                && item.Manifest != null
                                && item.Manifest.Complete == manifest.Complete
                                && string.Equals(
                                    item.Manifest.Purpose,
                                    manifest.Purpose,
                                    StringComparison.OrdinalIgnoreCase)
                                && string.Equals(
                                    item.Manifest.SourceProfilePath,
                                    manifest.SourceProfilePath,
                                    StringComparison.OrdinalIgnoreCase)
                                && string.Equals(
                                    item.Manifest.CompositeSha256,
                                    manifest.CompositeSha256,
                                    StringComparison.OrdinalIgnoreCase));
                        if (duplicate != null)
                        {
                            return Result<SnapshotRecord>.Ok(
                                duplicate,
                                "snapshot_unchanged",
                                "The current run state is already protected.");
                        }
                    }

                    FileTools.WriteJsonAtomic(
                        Path.Combine(pendingPath, "manifest.json"),
                        manifest);

                    string finalPath = Path.Combine(SnapshotsRoot, id);
                    Directory.Move(pendingPath, finalPath);
                    pendingPath = null;

                    SnapshotRecord record = ReadRecord(finalPath);
                    if (!record.StructurallyValid)
                    {
                        return Result<SnapshotRecord>.Fail(
                            "snapshot_manifest_invalid",
                            record.StatusMessage);
                    }

                    if (!options.SkipRetention)
                    {
                        PruneAutomaticSnapshots(AutomaticRetention);
                    }

                    return Result<SnapshotRecord>.Ok(
                        record,
                        "snapshot_created",
                        "A verified run snapshot was created.");
                }
                catch (IOException exception)
                {
                    return Result<SnapshotRecord>.Fail("snapshot_io_failed", exception.Message);
                }
                catch (UnauthorizedAccessException exception)
                {
                    return Result<SnapshotRecord>.Fail("snapshot_access_denied", exception.Message);
                }
                catch (Exception exception)
                {
                    return Result<SnapshotRecord>.Fail("snapshot_failed", exception.Message);
                }
                finally
                {
                    if (!string.IsNullOrEmpty(pendingPath))
                    {
                        FileTools.TryDeleteDirectory(pendingPath);
                    }
                }
            }
        }

        public Result<SnapshotRecord> CaptureHotPreparation(
            string profilePath,
            string label,
            bool userConfirmedPreparation)
        {
            lock (_gate)
            {
                if (!userConfirmedPreparation)
                {
                    return Result<SnapshotRecord>.Fail(
                        "preparation_confirmation_required",
                        "The player must explicitly confirm that the game is in the hideout.");
                }

                return CaptureHotWorkingState(profilePath, label, null);
            }
        }

        /// <summary>
        /// Captures the hideout as it was when the player departed from the route board. The departure
        /// recorder froze the last settled hideout save in storage before the event and proved that the
        /// game wrote nothing else until its departure save; that copy goes through the same hot-capture
        /// gates as a manual save. Every working file must predate the event, and the copied R0A must be
        /// exactly the bytes the recorder bound.
        /// </summary>
        public Result<SnapshotRecord> CaptureDeparturePreparation(
            string profilePath,
            DepartureEvidence departure)
        {
            lock (_gate)
            {
                if (departure == null
                    || departure.RouteIndex < 0
                    || departure.RouteIndex > DepartureEvidence.MaximumRouteIndex
                    || departure.ObservedUtc == default(DateTime)
                    || string.IsNullOrWhiteSpace(departure.RunSha256)
                    || departure.RunSha256.Length != 64)
                {
                    return Result<SnapshotRecord>.Fail(
                        "departure_evidence_invalid",
                        "A departure capture needs the observed route index, time and bound save hash.");
                }

                string stagingRoot = Path.GetFullPath(Path.Combine(StorageRoot, DepartureEvidence.StagingDirectoryName))
                    .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string staged = string.IsNullOrWhiteSpace(departure.StagedPath)
                    ? null
                    : Path.GetFullPath(departure.StagedPath);
                if (staged == null
                    || !staged.StartsWith(stagingRoot, StringComparison.OrdinalIgnoreCase)
                    || !Directory.Exists(staged)
                    || string.IsNullOrWhiteSpace(profilePath)
                    || !Directory.Exists(profilePath))
                {
                    return Result<SnapshotRecord>.Fail(
                        "departure_stage_missing",
                        "A departure capture needs the recorder's frozen hideout save inside this storage.");
                }

                return CaptureHotWorkingState(staged, null, departure, Path.GetFullPath(profilePath));
            }
        }

        private Result<SnapshotRecord> CaptureHotWorkingState(
            string profilePath,
            string label,
            DepartureEvidence departure,
            string recordedProfilePath = null)
        {
            string pendingPath = null;
            string publishedPath = null;
            bool accepted = false;
            try
            {
                Result<RunStateDescriptor> beforeResult = RunStateProbe.Inspect(
                    profilePath,
                    true);
                if (!beforeResult.Success)
                {
                    return Result<SnapshotRecord>.Fail(
                        beforeResult.Code,
                        beforeResult.Message);
                }

                RunStateDescriptor before = beforeResult.Value;
                if (!before.ManualCaptureReady)
                {
                    return Result<SnapshotRecord>.Fail(
                        before.ManualCaptureStatus ?? "manual_capture_not_ready",
                        "The current files do not satisfy the manual capture gate.");
                }

                if (before.NativeExportCaptureReady
                    && !before.WorkingCaptureReady)
                {
                    if (departure != null)
                    {
                        return Result<SnapshotRecord>.Fail(
                            "departure_working_state_missing",
                            "The departure save left no hot working envelope to capture.");
                    }

                    return CaptureNativeExportPreparation(
                        profilePath,
                        label,
                        before);
                }

                if (departure != null)
                {
                    // The hideout save from before the event: the departure save the game writes while
                    // handling it has already reset the hideout for the next visit.
                    long observed = departure.ObservedUtc.ToUniversalTime().Ticks;
                    if (SaveLayout.WorkingProtectionFiles.Any(path =>
                    {
                        FileStamp stamp = before.Files.FirstOrDefault(item => string.Equals(
                            item.RelativePath,
                            path,
                            StringComparison.OrdinalIgnoreCase));
                        return stamp == null || stamp.LastWriteUtcTicks >= observed;
                    }))
                    {
                        return Result<SnapshotRecord>.Fail(
                            "departure_save_not_before_event",
                            "The frozen hideout files were not all written before the observed departure.");
                    }
                }

                string kind = departure == null ? SnapshotKinds.Manual : SnapshotKinds.Departure;
                string id = BuildSnapshotId(kind);
                pendingPath = Path.Combine(SnapshotsRoot, ".pending-" + id);
                Directory.CreateDirectory(Path.Combine(pendingPath, "payload"));

                Dictionary<string, FileStamp> sourceStamps = before.Files.ToDictionary(
                    item => item.RelativePath,
                    item => item,
                    StringComparer.OrdinalIgnoreCase);
                foreach (string relativePath in SaveLayout.WorkingProtectionFiles)
                {
                    FileStamp stamp;
                    if (!sourceStamps.TryGetValue(relativePath, out stamp))
                    {
                        return Result<SnapshotRecord>.Fail(
                            "run_incomplete",
                            "A required hot working-state file is missing.");
                    }

                    FileTools.CopyShared(
                        SaveLayout.CombineUnderProfile(profilePath, relativePath),
                        PayloadPath(pendingPath, relativePath));
                }

                Result<RunSlotMetadata> synthesis = RunSlotSynthesizer.Synthesize(
                    PayloadPath(pendingPath, SaveLayout.GameRunPath),
                    SaveLayout.CombineUnderProfile(profilePath, SaveLayout.RunParamsPath),
                    PayloadPath(pendingPath, SaveLayout.RunDataPath),
                    PayloadPath(pendingPath, SaveLayout.RunParamsPath),
                    PayloadPath(pendingPath, SaveLayout.RunIconPath),
                    before.WorkingRunStateCode);
                if (!synthesis.Success)
                {
                    return Result<SnapshotRecord>.Fail(
                        synthesis.Code,
                        synthesis.Message);
                }

                if (departure != null
                    && !string.Equals(
                        FileTools.Sha256(PayloadPath(pendingPath, SaveLayout.GameRunPath), false),
                        departure.RunSha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return Result<SnapshotRecord>.Fail(
                        "departure_save_changed",
                        "The frozen R0A is not the hideout save the recorder bound.");
                }

                if (CaptureHooks != null && CaptureHooks.AfterPayloadCopied != null)
                {
                    CaptureHooks.AfterPayloadCopied();
                }

                Result<RunStateDescriptor> afterResult = RunStateProbe.Inspect(
                    profilePath,
                    true);
                if (!afterResult.Success || !before.SameCaptureState(afterResult.Value))
                {
                    return Result<SnapshotRecord>.Fail(
                        "source_changed",
                        "The game save changed while the hot snapshot was being copied.");
                }

                List<SnapshotFileEntry> entries = new List<SnapshotFileEntry>();
                DateTime synthesizedUtc = _clock.UtcNow.ToUniversalTime();
                foreach (string relativePath in SaveLayout.RequiredFiles)
                {
                    string payload = PayloadPath(pendingPath, relativePath);
                    FileInfo payloadInfo = new FileInfo(payload);
                    payloadInfo.Refresh();
                    if (!payloadInfo.Exists || payloadInfo.Length <= 0)
                    {
                        return Result<SnapshotRecord>.Fail(
                            "snapshot_file_missing",
                            "A hot snapshot payload file is missing: " + relativePath);
                    }

                    FileStamp sourceStamp;
                    bool copiedWorking = SaveLayout.WorkingProtectionFiles.Any(path =>
                        string.Equals(path, relativePath, StringComparison.OrdinalIgnoreCase));
                    if (copiedWorking
                        && sourceStamps.TryGetValue(relativePath, out sourceStamp))
                    {
                        string sourceHash = FileTools.Sha256(
                            SaveLayout.CombineUnderProfile(profilePath, relativePath),
                            true);
                        string payloadHash = FileTools.Sha256(payload, false);
                        if (!string.Equals(
                            sourceHash,
                            payloadHash,
                            StringComparison.OrdinalIgnoreCase))
                        {
                            return Result<SnapshotRecord>.Fail(
                                "copy_hash_mismatch",
                                "A copied hot-state file did not match the live source.");
                        }
                    }

                    sourceStamps.TryGetValue(relativePath, out sourceStamp);
                    entries.Add(new SnapshotFileEntry
                    {
                        RelativePath = SaveLayout.NormalizeRelativePath(relativePath),
                        Length = payloadInfo.Length,
                        LastWriteUtc = (copiedWorking && sourceStamp != null
                            ? new DateTime(
                                sourceStamp.LastWriteUtcTicks,
                                DateTimeKind.Utc)
                            : synthesizedUtc).ToString("O"),
                        Sha256 = FileTools.Sha256(payload, false)
                    });
                }

                Result<bool> slotVerification = RunSlotSynthesizer.Verify(
                    PayloadPath(pendingPath, SaveLayout.GameRunPath),
                    PayloadPath(pendingPath, SaveLayout.RunDataPath),
                    PayloadPath(pendingPath, SaveLayout.RunParamsPath),
                    PayloadPath(pendingPath, SaveLayout.RunIconPath),
                    before.WorkingRunStateCode);
                if (!slotVerification.Success)
                {
                    return Result<SnapshotRecord>.Fail(
                        slotVerification.Code,
                        slotVerification.Message);
                }

                Result<RunStateDescriptor> finalResult = RunStateProbe.Inspect(
                    profilePath,
                    true);
                if (!finalResult.Success || !before.SameCaptureState(finalResult.Value))
                {
                    return Result<SnapshotRecord>.Fail(
                        "source_changed_after_hash",
                        "The game save changed before hot snapshot validation finished.");
                }

                SnapshotManifest manifest = new SnapshotManifest
                {
                    Id = id,
                    CreatedUtc = _clock.UtcNow.ToUniversalTime().ToString("O"),
                    Kind = kind,
                    Label = departure != null
                        ? "出发前战备"
                        : string.IsNullOrWhiteSpace(label) ? "手动战备" : label,
                    SourceProfilePath = recordedProfilePath ?? before.ProfilePath,
                    Purpose = SnapshotPurposes.Preparation,
                    CaptureBasis = SnapshotCaptureBases.HotSynthesizedState,
                    PreparationConfirmation = departure == null
                        ? PreparationConfirmations.UserConfirmedHideout
                        : PreparationConfirmations.NativeDepartureObserved,
                    DepartureEventSid = departure == null ? null : DepartureEvidence.RouteBoardEventSid,
                    DepartureRouteIndex = departure == null ? (int?)null : departure.RouteIndex,
                    DepartureObservedUtc = departure == null
                        ? null
                        : departure.ObservedUtc.ToUniversalTime().ToString("O"),
                    DepartureRunSha256 = departure == null ? null : departure.RunSha256.ToLowerInvariant(),
                    DepartureBasis = departure == null ? null : DepartureEvidence.PreDepartureSave,
                    Marker = ReadPayloadMarker(pendingPath, entries),
                    RunStateCode = before.WorkingRunStateCode,
                    RunDataLength = entries.First(item => string.Equals(
                        item.RelativePath,
                        SaveLayout.RunDataPath,
                        StringComparison.OrdinalIgnoreCase)).Length,
                    WorkingRunDataLength = before.WorkingRunDataLength,
                    WorkingRunBackupLength = before.WorkingRunBackupLength,
                    MatchingBackupCount = before.MatchingBackupCount,
                    CoreWriteSkewMs = before.CoreWriteSkewMs,
                    WorkingCopyShapeMatches = true,
                    WorkingRunMirrorsMatch = before.WorkingRunMirrorsMatch,
                    WorkingProfileMirrorsMatch = before.WorkingProfileMirrorsMatch,
                    Complete = true,
                    Files = entries.OrderBy(
                        item => item.RelativePath,
                        StringComparer.OrdinalIgnoreCase).ToList()
                };
                manifest.CompositeSha256 = FileTools.CompositeHash(manifest.Files);
                manifest.ManifestSha256 = FileTools.ManifestHash(manifest);
                FileTools.WriteJsonAtomic(
                    Path.Combine(pendingPath, "manifest.json"),
                    manifest);

                publishedPath = Path.Combine(SnapshotsRoot, id);
                Directory.Move(pendingPath, publishedPath);
                pendingPath = null;

                SnapshotRecord record = ReadRecord(publishedPath);
                Result<SnapshotRecord> verified = VerifyRecord(record, true);
                if (!verified.Success || !SnapshotPolicy.IsConfirmedPreparation(record))
                {
                    return Result<SnapshotRecord>.Fail(
                        verified.Success ? "snapshot_unconfirmed" : verified.Code,
                        verified.Success
                            ? "The hot snapshot did not satisfy the restore policy."
                            : verified.Message);
                }

                accepted = true;
                return Result<SnapshotRecord>.Ok(
                    record,
                    departure == null ? "hot_snapshot_created" : "departure_snapshot_created",
                    departure == null
                        ? "The current preparation state was hot-saved with a verified run-slot export."
                        : "The hideout save from before the departure was captured with a verified run-slot export.");
            }
            catch (IOException exception)
            {
                return Result<SnapshotRecord>.Fail("snapshot_io_failed", exception.Message);
            }
            catch (UnauthorizedAccessException exception)
            {
                return Result<SnapshotRecord>.Fail("snapshot_access_denied", exception.Message);
            }
            catch (Exception exception)
            {
                return Result<SnapshotRecord>.Fail("snapshot_failed", exception.Message);
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(pendingPath))
                {
                    FileTools.TryDeleteDirectory(pendingPath);
                }

                if (!accepted && !string.IsNullOrWhiteSpace(publishedPath))
                {
                    FileTools.TryDeleteDirectory(publishedPath);
                }
            }
        }

        private Result<SnapshotRecord> CaptureNativeExportPreparation(
            string profilePath,
            string label,
            RunStateDescriptor before)
        {
            string pendingPath = null;
            string publishedPath = null;
            bool accepted = false;
            try
            {
                if (before == null || !before.NativeExportCaptureReady)
                {
                    return Result<SnapshotRecord>.Fail(
                        "native_export_not_ready",
                        "The native export does not satisfy the manual capture gate.");
                }

                string id = BuildSnapshotId(SnapshotKinds.Manual);
                pendingPath = Path.Combine(SnapshotsRoot, ".pending-" + id);
                Directory.CreateDirectory(Path.Combine(pendingPath, "payload"));
                string sourceRoot = Path.Combine(pendingPath, ".native-source");
                Directory.CreateDirectory(sourceRoot);

                Dictionary<string, FileStamp> sourceStamps = before.NativeExportFiles
                    .ToDictionary(
                        item => item.RelativePath,
                        item => item,
                        StringComparer.OrdinalIgnoreCase);
                foreach (string relativePath in SaveLayout.NativeExportSourceFiles)
                {
                    FileStamp stamp;
                    if (!sourceStamps.TryGetValue(relativePath, out stamp))
                    {
                        return Result<SnapshotRecord>.Fail(
                            "run_incomplete",
                            "A required native export file is missing.");
                    }

                    FileTools.CopyShared(
                        SaveLayout.CombineUnderProfile(profilePath, relativePath),
                        NativeSourcePath(sourceRoot, relativePath));
                }

                Result<DecodedSlotPayload> runDecode = RunSlotSynthesizer.DecodeNativeExport(
                    NativeSourcePath(sourceRoot, SaveLayout.RunDataPath),
                    NativeSourcePath(sourceRoot, SaveLayout.RunParamsPath),
                    NativeSourcePath(sourceRoot, SaveLayout.RunIconPath));
                if (!runDecode.Success)
                {
                    return Result<SnapshotRecord>.Fail(runDecode.Code, runDecode.Message);
                }

                Result<DecodedSlotPayload> profileDecode = ProfileSlotCodec.DecodeNativeExport(
                    NativeSourcePath(sourceRoot, SaveLayout.ProfileDataPath),
                    NativeSourcePath(sourceRoot, SaveLayout.ProfileParamsPath),
                    NativeSourcePath(sourceRoot, SaveLayout.ProfileIconPath));
                if (!profileDecode.Success)
                {
                    return Result<SnapshotRecord>.Fail(profileDecode.Code, profileDecode.Message);
                }

                if (!string.Equals(
                        runDecode.Value.StateCode,
                        before.WorkingRunStateCode,
                        StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(
                        profileDecode.Value.StateCode,
                        before.ProfileSlotStateCode,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return Result<SnapshotRecord>.Fail(
                        "native_export_state_changed",
                        "The native export state changed before reconstruction.");
                }

                foreach (string relativePath in SaveLayout.CommittedSlotFiles.Concat(
                    new[] { SaveLayout.MarkerPath }))
                {
                    FileTools.CopyShared(
                        NativeSourcePath(sourceRoot, relativePath),
                        PayloadPath(pendingPath, relativePath));
                }

                WritePayloadBytes(
                    PayloadPath(pendingPath, SaveLayout.GameRunPath),
                    runDecode.Value.RawBytes);
                WritePayloadBytes(
                    PayloadPath(pendingPath, SaveLayout.GameRunBackupPath),
                    runDecode.Value.RawBytes);
                WritePayloadBytes(
                    PayloadPath(pendingPath, SaveLayout.GameProfilePath),
                    profileDecode.Value.RawBytes);
                WritePayloadBytes(
                    PayloadPath(pendingPath, SaveLayout.GameProfileBackupPath),
                    profileDecode.Value.RawBytes);

                if (CaptureHooks != null && CaptureHooks.AfterPayloadCopied != null)
                {
                    CaptureHooks.AfterPayloadCopied();
                }

                Result<RunStateDescriptor> afterResult = RunStateProbe.Inspect(
                    profilePath,
                    true);
                if (!afterResult.Success
                    || !before.SameCaptureState(afterResult.Value)
                    || !NativeSourcesMatch(profilePath, sourceRoot, sourceStamps))
                {
                    return Result<SnapshotRecord>.Fail(
                        "source_changed",
                        "The native export changed while the snapshot was being reconstructed.");
                }

                List<SnapshotFileEntry> entries = new List<SnapshotFileEntry>();
                DateTime synthesizedUtc = _clock.UtcNow.ToUniversalTime();
                foreach (string relativePath in SaveLayout.RequiredFiles)
                {
                    string payload = PayloadPath(pendingPath, relativePath);
                    FileInfo info = new FileInfo(payload);
                    info.Refresh();
                    if (!info.Exists || info.Length <= 0)
                    {
                        return Result<SnapshotRecord>.Fail(
                            "snapshot_file_missing",
                            "A reconstructed snapshot file is missing: " + relativePath);
                    }

                    FileStamp sourceStamp;
                    bool copiedSource = sourceStamps.TryGetValue(relativePath, out sourceStamp);
                    entries.Add(new SnapshotFileEntry
                    {
                        RelativePath = SaveLayout.NormalizeRelativePath(relativePath),
                        Length = info.Length,
                        LastWriteUtc = (copiedSource
                            ? new DateTime(
                                sourceStamp.LastWriteUtcTicks,
                                DateTimeKind.Utc)
                            : synthesizedUtc).ToString("O"),
                        Sha256 = FileTools.Sha256(payload, false)
                    });
                }

                Result<bool> runVerification = RunSlotSynthesizer.VerifyNativeExport(
                    PayloadPath(pendingPath, SaveLayout.GameRunPath),
                    PayloadPath(pendingPath, SaveLayout.RunDataPath),
                    PayloadPath(pendingPath, SaveLayout.RunParamsPath),
                    PayloadPath(pendingPath, SaveLayout.RunIconPath),
                    before.WorkingRunStateCode);
                if (!runVerification.Success)
                {
                    return Result<SnapshotRecord>.Fail(
                        runVerification.Code,
                        runVerification.Message);
                }

                Result<bool> profileVerification = ProfileSlotCodec.VerifyWorkingExportHash(
                    PayloadPath(pendingPath, SaveLayout.GameProfilePath),
                    profileDecode.Value.StateCode,
                    profileDecode.Value.ExportSha256);
                if (!profileVerification.Success)
                {
                    return Result<SnapshotRecord>.Fail(
                        profileVerification.Code,
                        profileVerification.Message);
                }

                Result<RunStateDescriptor> finalResult = RunStateProbe.Inspect(
                    profilePath,
                    true);
                if (!finalResult.Success
                    || !before.SameCaptureState(finalResult.Value)
                    || !NativeSourcesMatch(profilePath, sourceRoot, sourceStamps))
                {
                    return Result<SnapshotRecord>.Fail(
                        "source_changed_after_hash",
                        "The native export changed before snapshot validation finished.");
                }

                SnapshotManifest manifest = new SnapshotManifest
                {
                    SchemaVersion = 6,
                    Id = id,
                    CreatedUtc = _clock.UtcNow.ToUniversalTime().ToString("O"),
                    Kind = SnapshotKinds.Manual,
                    Label = string.IsNullOrWhiteSpace(label) ? "手动战备" : label,
                    SourceProfilePath = before.ProfilePath,
                    Purpose = SnapshotPurposes.Preparation,
                    CaptureBasis = SnapshotCaptureBases.NativeExportedState,
                    PreparationConfirmation = PreparationConfirmations.UserConfirmedHideout,
                    Marker = ReadPayloadMarker(pendingPath, entries),
                    RunStateCode = before.WorkingRunStateCode,
                    RunDataLength = runDecode.Value.RawBytes.Length + SaveLayout.SlotEnvelopeLength,
                    WorkingRunDataLength = runDecode.Value.RawBytes.Length,
                    WorkingRunBackupLength = runDecode.Value.RawBytes.Length,
                    MatchingBackupCount = 0,
                    CoreWriteSkewMs = before.NativeExportWriteSkewMs,
                    WorkingCopyShapeMatches = true,
                    WorkingRunMirrorsMatch = true,
                    WorkingProfileMirrorsMatch = true,
                    ProfileSlotStateCode = profileDecode.Value.StateCode,
                    ProfileSlotExportSha256 = profileDecode.Value.ExportSha256,
                    Complete = true,
                    Files = entries.OrderBy(
                        item => item.RelativePath,
                        StringComparer.OrdinalIgnoreCase).ToList()
                };
                manifest.CompositeSha256 = FileTools.CompositeHash(manifest.Files);
                manifest.ManifestSha256 = FileTools.ManifestHash(manifest);
                FileTools.TryDeleteDirectory(sourceRoot);
                FileTools.WriteJsonAtomic(Path.Combine(pendingPath, "manifest.json"), manifest);

                publishedPath = Path.Combine(SnapshotsRoot, id);
                Directory.Move(pendingPath, publishedPath);
                pendingPath = null;

                SnapshotRecord record = ReadRecord(publishedPath);
                Result<SnapshotRecord> verified = VerifyRecord(record, true);
                if (!verified.Success || !SnapshotPolicy.IsConfirmedPreparation(record))
                {
                    return Result<SnapshotRecord>.Fail(
                        verified.Success ? "snapshot_unconfirmed" : verified.Code,
                        verified.Success
                            ? "The reconstructed snapshot did not satisfy the restore policy."
                            : verified.Message);
                }

                accepted = true;
                return Result<SnapshotRecord>.Ok(
                    record,
                    "native_export_snapshot_created",
                    "The native hideout export was reconstructed and verified as a full snapshot.");
            }
            catch (IOException exception)
            {
                return Result<SnapshotRecord>.Fail("snapshot_io_failed", exception.Message);
            }
            catch (UnauthorizedAccessException exception)
            {
                return Result<SnapshotRecord>.Fail("snapshot_access_denied", exception.Message);
            }
            catch (Exception exception)
            {
                return Result<SnapshotRecord>.Fail("snapshot_failed", exception.Message);
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(pendingPath))
                {
                    FileTools.TryDeleteDirectory(pendingPath);
                }

                if (!accepted && !string.IsNullOrWhiteSpace(publishedPath))
                {
                    FileTools.TryDeleteDirectory(publishedPath);
                }
            }
        }

        public IList<SnapshotRecord> ListSnapshots()
        {
            lock (_gate)
            {
                return ListSnapshotsInternal();
            }
        }

        public Result<SnapshotRecord> GetSnapshot(string id)
        {
            lock (_gate)
            {
                try
                {
                    string path = ResolveSnapshotDirectory(id);
                    if (!Directory.Exists(path))
                    {
                        return Result<SnapshotRecord>.Fail(
                            "snapshot_not_found",
                            "The selected snapshot no longer exists.");
                    }

                    SnapshotRecord record = ReadRecord(path);
                    return record.StructurallyValid
                        ? Result<SnapshotRecord>.Ok(record, "snapshot_found", "Snapshot found.")
                        : Result<SnapshotRecord>.Fail("snapshot_invalid", record.StatusMessage);
                }
                catch (Exception exception)
                {
                    return Result<SnapshotRecord>.Fail("snapshot_lookup_failed", exception.Message);
                }
            }
        }

        public Result<SnapshotRecord> VerifySnapshot(string id, bool requireComplete)
        {
            lock (_gate)
            {
                Result<SnapshotRecord> found = GetSnapshot(id);
                if (!found.Success)
                {
                    return found;
                }

                return VerifyRecord(found.Value, requireComplete);
            }
        }

        public Result<bool> DeleteSnapshot(string id)
        {
            lock (_gate)
            {
                try
                {
                    if (HasPendingRestore())
                    {
                        return Result<bool>.Fail(
                            "snapshot_delete_blocked_restore_pending",
                            "Snapshots cannot be deleted while a restore transaction is pending.");
                    }

                    string path = ResolveSnapshotDirectory(id);
                    if (!Directory.Exists(path))
                    {
                        return Result<bool>.Fail(
                            "snapshot_not_found",
                            "The selected snapshot no longer exists.");
                    }

                    Directory.Delete(path, true);
                    DeleteDepartureRecord(id);
                    return Result<bool>.Ok(
                        true,
                        "snapshot_deleted",
                        "The selected snapshot was deleted.");
                }
                catch (IOException exception)
                {
                    return Result<bool>.Fail("snapshot_delete_io_failed", exception.Message);
                }
                catch (UnauthorizedAccessException exception)
                {
                    return Result<bool>.Fail("snapshot_delete_access_denied", exception.Message);
                }
                catch (Exception exception)
                {
                    return Result<bool>.Fail("snapshot_delete_failed", exception.Message);
                }
            }
        }

        public Result<RestoreOutcome> Restore(
            string snapshotId,
            string profilePath,
            IGameProcessProbe gameProbe,
            RestoreHooks hooks)
        {
            lock (_gate)
            {
                if (gameProbe != null && gameProbe.IsGameRunning())
                {
                    return Result<RestoreOutcome>.Fail(
                        "game_running",
                        "Exit the game completely before restoring a run snapshot.");
                }

                Result<SnapshotRecord> targetResult = VerifySnapshot(snapshotId, true);
                if (!targetResult.Success)
                {
                    return Result<RestoreOutcome>.Fail(targetResult.Code, targetResult.Message);
                }

                if (!SnapshotPolicy.IsConfirmedPreparation(targetResult.Value))
                {
                    return Result<RestoreOutcome>.Fail(
                        "snapshot_unconfirmed",
                        "The selected snapshot is not a verified preparation state and cannot be restored.");
                }

                if (!SameProfile(
                    targetResult.Value.Manifest.SourceProfilePath,
                    profilePath))
                {
                    return Result<RestoreOutcome>.Fail(
                        "snapshot_profile_mismatch",
                        "The selected snapshot belongs to a different save profile.");
                }

                CaptureOptions undoOptions = new CaptureOptions
                {
                    Kind = SnapshotKinds.PreRestore,
                    Purpose = SnapshotPurposes.LiveState,
                    Label = "恢复前现场",
                    AllowIncomplete = true,
                    SkipDeduplication = true,
                    SkipRetention = true
                };
                Result<SnapshotRecord> undoResult = Capture(profilePath, undoOptions);
                if (!undoResult.Success)
                {
                    return Result<RestoreOutcome>.Fail(
                        "undo_capture_failed",
                        "The current live state could not be secured before restore: "
                        + undoResult.Message);
                }

                return RestoreVerifiedTransaction(
                    targetResult.Value,
                    undoResult.Value,
                    profilePath,
                    hooks);
            }
        }

        public Result<RestoreOutcome> RestorePrepared(
            string snapshotId,
            string preRestoreSnapshotId,
            string profilePath,
            IGameProcessProbe gameProbe,
            RestoreHooks hooks)
        {
            lock (_gate)
            {
                if (gameProbe != null && gameProbe.IsGameRunning())
                {
                    return Result<RestoreOutcome>.Fail(
                        "game_running",
                        "Exit the game completely before restoring a run snapshot.");
                }

                if (HasPendingRestore())
                {
                    return Result<RestoreOutcome>.Fail(
                        "restore_pending",
                        "An earlier restore transaction must be recovered first.");
                }

                Result<SnapshotRecord> targetResult = VerifySnapshot(snapshotId, true);
                if (!targetResult.Success
                    || !SnapshotPolicy.IsConfirmedPreparation(targetResult.Value))
                {
                    return Result<RestoreOutcome>.Fail(
                        targetResult.Success ? "snapshot_unconfirmed" : targetResult.Code,
                        targetResult.Success
                            ? "The selected snapshot is not a verified preparation state."
                            : targetResult.Message);
                }

                Result<SnapshotRecord> undoResult = VerifySnapshot(
                    preRestoreSnapshotId,
                    false);
                if (!undoResult.Success
                    || undoResult.Value.Manifest == null
                    || undoResult.Value.Manifest.SchemaVersion < 2
                    || !string.Equals(
                        undoResult.Value.Manifest.Purpose,
                        SnapshotPurposes.LiveState,
                        StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(
                        undoResult.Value.Manifest.Kind,
                        SnapshotKinds.PreRestore,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return Result<RestoreOutcome>.Fail(
                        "undo_snapshot_invalid",
                        undoResult.Success
                            ? "The prepared recovery point is not a live pre-restore snapshot."
                            : undoResult.Message);
                }

                if (!SameProfile(targetResult.Value.Manifest.SourceProfilePath, profilePath)
                    || !SameProfile(undoResult.Value.Manifest.SourceProfilePath, profilePath))
                {
                    return Result<RestoreOutcome>.Fail(
                        "snapshot_profile_mismatch",
                        "The target or prepared recovery point belongs to another profile.");
                }

                return RestoreVerifiedTransaction(
                    targetResult.Value,
                    undoResult.Value,
                    profilePath,
                    hooks);
            }
        }

        private Result<RestoreOutcome> RestoreVerifiedTransaction(
            SnapshotRecord target,
            SnapshotRecord undo,
            string profilePath,
            RestoreHooks hooks)
        {
            RestoreJournal journal = new RestoreJournal
            {
                SchemaVersion = 1,
                StartedUtc = _clock.UtcNow.ToUniversalTime().ToString("O"),
                ProfilePath = Path.GetFullPath(profilePath),
                TargetSnapshotId = target.Manifest.Id,
                UndoSnapshotId = undo.Manifest.Id,
                State = "prepared"
            };

            try
            {
                FileTools.WriteJsonAtomic(JournalPath, journal);
                ApplyRecord(
                    target,
                    profilePath,
                    false,
                    hooks,
                    relativePath =>
                    {
                        journal.State = "applying";
                        journal.CompletedFiles.Add(relativePath);
                        FileTools.WriteJsonAtomic(JournalPath, journal);
                    });

                journal.State = "verified";
                FileTools.WriteJsonAtomic(JournalPath, journal);
                FileTools.TryDeleteFile(JournalPath);

                return Result<RestoreOutcome>.Ok(
                    new RestoreOutcome
                    {
                        RestoredSnapshotId = target.Manifest.Id,
                        UndoSnapshotId = undo.Manifest.Id,
                        RolledBackAfterFailure = false
                    },
                    "restore_written_verified",
                    "The confirmed preparation bytes were written back and verified. Game resume still requires in-game validation.");
            }
            catch (Exception restoreException)
            {
                try
                {
                    ApplyRecord(undo, profilePath, true, null, null);
                    FileTools.TryDeleteFile(JournalPath);
                    return Result<RestoreOutcome>.Fail(
                        "restore_failed_rolled_back",
                        "Restore failed, and the original live state was put back safely: "
                        + restoreException.Message);
                }
                catch (Exception rollbackException)
                {
                    journal.State = "rollback_failed";
                    try
                    {
                        FileTools.WriteJsonAtomic(JournalPath, journal);
                    }
                    catch
                    {
                        // Preserve the original recovery errors below.
                    }

                    return Result<RestoreOutcome>.Fail(
                        "restore_failed_rollback_failed",
                        "Restore and automatic rollback both failed. Do not launch the game. "
                        + "Restore error: " + restoreException.Message
                        + " Rollback error: " + rollbackException.Message);
                }
            }
        }

        public Result<RestoreOutcome> Undo(
            string preRestoreSnapshotId,
            string profilePath,
            IGameProcessProbe gameProbe)
        {
            lock (_gate)
            {
                if (gameProbe != null && gameProbe.IsGameRunning())
                {
                    return Result<RestoreOutcome>.Fail(
                        "game_running",
                        "Exit the game completely before undoing a restore.");
                }

                Result<SnapshotRecord> targetResult = VerifySnapshot(
                    preRestoreSnapshotId,
                    false);
                if (!targetResult.Success
                    || targetResult.Value.Manifest == null
                    || targetResult.Value.Manifest.SchemaVersion < 2
                    || !string.Equals(
                        targetResult.Value.Manifest.Purpose,
                        SnapshotPurposes.LiveState,
                        StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(
                        targetResult.Value.Manifest.Kind,
                        SnapshotKinds.PreRestore,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return Result<RestoreOutcome>.Fail(
                        "undo_snapshot_invalid",
                        targetResult.Success
                            ? "The selected snapshot is not a pre-restore recovery point."
                            : targetResult.Message);
                }

                if (!SameProfile(
                    targetResult.Value.Manifest.SourceProfilePath,
                    profilePath))
                {
                    return Result<RestoreOutcome>.Fail(
                        "snapshot_profile_mismatch",
                        "The recovery point belongs to a different save profile.");
                }

                Result<SnapshotRecord> currentResult = Capture(
                    profilePath,
                    new CaptureOptions
                    {
                        Kind = SnapshotKinds.PreRestore,
                        Purpose = SnapshotPurposes.LiveState,
                        Label = "撤销前现场",
                        AllowIncomplete = true,
                        SkipDeduplication = true,
                        SkipRetention = true
                    });
                if (!currentResult.Success)
                {
                    return Result<RestoreOutcome>.Fail(
                        "undo_capture_failed",
                        "The current live state could not be secured before undo: "
                        + currentResult.Message);
                }

                SnapshotRecord target = targetResult.Value;
                SnapshotRecord current = currentResult.Value;
                RestoreJournal journal = new RestoreJournal
                {
                    SchemaVersion = 1,
                    StartedUtc = _clock.UtcNow.ToUniversalTime().ToString("O"),
                    ProfilePath = Path.GetFullPath(profilePath),
                    TargetSnapshotId = target.Manifest.Id,
                    UndoSnapshotId = current.Manifest.Id,
                    State = "prepared"
                };

                try
                {
                    FileTools.WriteJsonAtomic(JournalPath, journal);
                    ApplyRecord(
                        target,
                        profilePath,
                        true,
                        null,
                        relativePath =>
                        {
                            journal.State = "undoing";
                            journal.CompletedFiles.Add(relativePath);
                            FileTools.WriteJsonAtomic(JournalPath, journal);
                        });
                    journal.State = "verified";
                    FileTools.WriteJsonAtomic(JournalPath, journal);
                    FileTools.TryDeleteFile(JournalPath);

                    return Result<RestoreOutcome>.Ok(
                        new RestoreOutcome
                        {
                            RestoredSnapshotId = target.Manifest.Id,
                            UndoSnapshotId = current.Manifest.Id,
                            RolledBackAfterFailure = false
                        },
                        "undo_complete",
                        "The pre-restore live state was restored and verified.");
                }
                catch (Exception undoException)
                {
                    try
                    {
                        ApplyRecord(current, profilePath, true, null, null);
                        FileTools.TryDeleteFile(JournalPath);
                        return Result<RestoreOutcome>.Fail(
                            "undo_failed_rolled_back",
                            "Undo failed, and the state from before undo was restored: "
                            + undoException.Message);
                    }
                    catch (Exception rollbackException)
                    {
                        journal.State = "rollback_failed";
                        try
                        {
                            FileTools.WriteJsonAtomic(JournalPath, journal);
                        }
                        catch
                        {
                            // The journal may already be the only recovery evidence.
                        }

                        return Result<RestoreOutcome>.Fail(
                            "undo_failed_rollback_failed",
                            "Undo and rollback both failed. Do not launch the game. "
                            + "Undo error: " + undoException.Message
                            + " Rollback error: " + rollbackException.Message);
                    }
                }
            }
        }

        public bool HasPendingRestore()
        {
            return File.Exists(JournalPath);
        }

        public Result<RestoreOutcome> RecoverInterruptedRestore(IGameProcessProbe gameProbe)
        {
            lock (_gate)
            {
                if (!File.Exists(JournalPath))
                {
                    return Result<RestoreOutcome>.Ok(
                        new RestoreOutcome(),
                        "no_pending_restore",
                        "No interrupted restore is pending.");
                }

                if (gameProbe != null && gameProbe.IsGameRunning())
                {
                    return Result<RestoreOutcome>.Fail(
                        "game_running_with_pending_restore",
                        "An interrupted restore is pending. Exit the game before recovery.");
                }

                try
                {
                    RestoreJournal journal = FileTools.ReadJson<RestoreJournal>(JournalPath);
                    if (journal == null
                        || journal.SchemaVersion != 1
                        || string.IsNullOrWhiteSpace(journal.ProfilePath)
                        || string.IsNullOrWhiteSpace(journal.UndoSnapshotId))
                    {
                        return Result<RestoreOutcome>.Fail(
                            "restore_journal_invalid",
                            "The interrupted-restore journal is invalid.");
                    }

                    Result<SnapshotRecord> undoResult = VerifySnapshot(
                        journal.UndoSnapshotId,
                        false);
                    if (!undoResult.Success)
                    {
                        return Result<RestoreOutcome>.Fail(
                            "undo_snapshot_invalid",
                            undoResult.Message);
                    }

                    if (!SameProfile(
                        undoResult.Value.Manifest.SourceProfilePath,
                        journal.ProfilePath))
                    {
                        return Result<RestoreOutcome>.Fail(
                            "snapshot_profile_mismatch",
                            "The interrupted restore recovery point belongs to a different save profile.");
                    }

                    ApplyRecord(undoResult.Value, journal.ProfilePath, true, null, null);
                    FileTools.TryDeleteFile(JournalPath);
                    return Result<RestoreOutcome>.Ok(
                        new RestoreOutcome
                        {
                            UndoSnapshotId = undoResult.Value.Manifest.Id,
                            RolledBackAfterFailure = true
                        },
                        "interrupted_restore_recovered",
                        "The live save was returned to its pre-restore state.");
                }
                catch (Exception exception)
                {
                    return Result<RestoreOutcome>.Fail(
                        "interrupted_restore_recovery_failed",
                        exception.Message);
                }
            }
        }

        public Result<int> PruneAutomaticSnapshots(int keepCount)
        {
            lock (_gate)
            {
                try
                {
                    int safeKeepCount = Math.Max(10, keepCount);
                    List<SnapshotRecord> automatic = ListSnapshotsInternal()
                        .Where(item => item.StructurallyValid
                            && item.Manifest != null
                            && string.Equals(
                                item.Manifest.Kind,
                                SnapshotKinds.Automatic,
                                StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(item => item.CreatedUtc)
                        .ToList();

                    int removed = 0;
                    foreach (SnapshotRecord record in automatic.Skip(safeKeepCount))
                    {
                        FileTools.TryDeleteDirectory(record.DirectoryPath);
                        if (!Directory.Exists(record.DirectoryPath))
                        {
                            removed++;
                        }
                    }

                    return Result<int>.Ok(
                        removed,
                        "retention_complete",
                        "Old automatic snapshots were pruned.");
                }
                catch (Exception exception)
                {
                    return Result<int>.Fail("retention_failed", exception.Message);
                }
            }
        }

        /// <summary>
        /// Keeps the newest automatic departure preparations of one profile and deletes older ones with
        /// their departure records. Manual preparations and undo points are never touched.
        /// </summary>
        public Result<int> PruneDepartureSnapshots(string profilePath, int keepCount)
        {
            lock (_gate)
            {
                if (HasPendingRestore())
                {
                    return Result<int>.Fail(
                        "departure_retention_blocked_restore_pending",
                        "Departure snapshots are not pruned while a restore transaction is pending.");
                }

                try
                {
                    int removed = 0;
                    foreach (SnapshotRecord record in ListSnapshotsInternal()
                        .Where(item => item.Manifest != null
                            && !string.IsNullOrWhiteSpace(item.Manifest.Id)
                            && string.Equals(item.Manifest.Kind, SnapshotKinds.Departure, StringComparison.OrdinalIgnoreCase)
                            && SameProfile(item.Manifest.SourceProfilePath, profilePath))
                        .OrderByDescending(item => item.CreatedUtc)
                        .Skip(Math.Max(1, keepCount)))
                    {
                        FileTools.TryDeleteDirectory(record.DirectoryPath);
                        if (!Directory.Exists(record.DirectoryPath))
                        {
                            DeleteDepartureRecord(record.Manifest.Id);
                            removed++;
                        }
                    }

                    return Result<int>.Ok(
                        removed,
                        "departure_retention_complete",
                        "Old departure snapshots were pruned.");
                }
                catch (Exception exception)
                {
                    return Result<int>.Fail("departure_retention_failed", exception.Message);
                }
            }
        }

        private void DeleteDepartureRecord(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                return;
            }

            try
            {
                File.Delete(Path.Combine(DeparturesRoot, id + ".json"));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        /// <summary>
        /// Keeps the newest restorable preparations and the newest undo points for one profile,
        /// and lists everything else in that profile for removal. Other profiles are untouched.
        /// Pinned snapshots (the one being protected, the last restored one, the one a departure
        /// is being recorded for) are kept whatever their age.
        /// </summary>
        public Result<SnapshotCleanupPlan> PlanRecentCleanup(
            string profilePath,
            int keepPerKind,
            IEnumerable<string> pinned = null)
        {
            lock (_gate)
            {
                try
                {
                    return Result<SnapshotCleanupPlan>.Ok(
                        BuildCleanupPlan(profilePath, keepPerKind, pinned),
                        "snapshot_cleanup_planned",
                        "Snapshot cleanup was planned.");
                }
                catch (Exception exception)
                {
                    return Result<SnapshotCleanupPlan>.Fail("snapshot_cleanup_failed", exception.Message);
                }
            }
        }

        public Result<SnapshotCleanupOutcome> CleanupToRecent(SnapshotCleanupPlan confirmed)
        {
            lock (_gate)
            {
                if (HasPendingRestore())
                {
                    return Result<SnapshotCleanupOutcome>.Fail(
                        "snapshot_cleanup_blocked_restore_pending",
                        "Snapshots cannot be cleaned up while a restore transaction is pending.");
                }

                try
                {
                    SnapshotCleanupPlan current = confirmed == null
                        ? null
                        : BuildCleanupPlan(confirmed.ProfilePath, confirmed.KeepPerKind, confirmed.Pinned);
                    if (current == null || !current.SameRemovals(confirmed))
                    {
                        return Result<SnapshotCleanupOutcome>.Fail(
                            "snapshot_cleanup_plan_changed",
                            "The snapshot list changed after confirmation; nothing was deleted.");
                    }

                    SnapshotCleanupOutcome outcome = new SnapshotCleanupOutcome();
                    foreach (SnapshotRecord record in current.Removals)
                    {
                        FileTools.TryDeleteDirectory(record.DirectoryPath);
                        if (Directory.Exists(record.DirectoryPath))
                        {
                            outcome.Failed++;
                            continue;
                        }

                        DeleteDepartureRecord(record.Manifest.Id);
                        outcome.Removed++;
                        outcome.FreedBytes += SnapshotBytes(record);
                    }

                    return outcome.Failed == 0
                        ? Result<SnapshotCleanupOutcome>.Ok(
                            outcome,
                            "snapshot_cleanup_complete",
                            "Old snapshots were deleted.")
                        : Result<SnapshotCleanupOutcome>.Fail(
                            "snapshot_cleanup_partial",
                            outcome.Failed + " snapshot folders could not be deleted.");
                }
                catch (Exception exception)
                {
                    return Result<SnapshotCleanupOutcome>.Fail("snapshot_cleanup_failed", exception.Message);
                }
            }
        }

        private SnapshotCleanupPlan BuildCleanupPlan(string profilePath, int keepPerKind, IEnumerable<string> pinned)
        {
            if (string.IsNullOrWhiteSpace(profilePath))
            {
                throw new ArgumentException("A save profile is required for snapshot cleanup.");
            }

            if (keepPerKind < 1)
            {
                throw new ArgumentOutOfRangeException("keepPerKind", "At least one snapshot per kind must be kept.");
            }

            List<SnapshotRecord> scope = ListSnapshotsInternal()
                .Where(record => record.Manifest != null
                    && !string.IsNullOrWhiteSpace(record.Manifest.Id)
                    && SameProfile(record.Manifest.SourceProfilePath, profilePath))
                .ToList();
            SnapshotCleanupPlan plan = new SnapshotCleanupPlan
            {
                ProfilePath = profilePath,
                KeepPerKind = keepPerKind
            };
            foreach (string kind in new[] { SnapshotKinds.Manual, SnapshotKinds.Departure })
            {
                // Early post-departure captures still restart their encounter, so they count as departures here.
                plan.KeptPreparations.AddRange(scope.Where(record => SnapshotPolicy.IsRedeployablePreparation(record)
                    && string.Equals(record.Manifest.Kind, kind, StringComparison.OrdinalIgnoreCase)).Take(keepPerKind));
            }

            plan.KeptLiveStates.AddRange(scope.Where(IsUndoPoint).Take(keepPerKind));
            HashSet<SnapshotRecord> kept = new HashSet<SnapshotRecord>(
                plan.KeptPreparations.Concat(plan.KeptLiveStates));
            plan.Pinned.UnionWith((pinned ?? Enumerable.Empty<string>()).Where(id => !string.IsNullOrWhiteSpace(id)));
            plan.KeptPinned.AddRange(scope.Where(record => !kept.Contains(record) && plan.Pinned.Contains(record.Manifest.Id)));
            kept.UnionWith(plan.KeptPinned);
            foreach (SnapshotRecord record in scope.Where(record => !kept.Contains(record)))
            {
                plan.Removals.Add(record);
                plan.RemovalBytes += SnapshotBytes(record);
                if (SnapshotKinds.IsPreparation(record.Manifest.Kind))
                {
                    plan.RemovedPreparations++;
                }
                else if (string.Equals(record.Manifest.Kind, SnapshotKinds.PreRestore, StringComparison.OrdinalIgnoreCase))
                {
                    plan.RemovedLiveStates++;
                }
                else
                {
                    plan.RemovedOther++;
                }
            }

            return plan;
        }

        /// <summary>Same test the window uses to pick the "undo latest restore" target.</summary>
        private static bool IsUndoPoint(SnapshotRecord record)
        {
            return record.StructurallyValid
                && record.Manifest.SchemaVersion >= 2
                && string.Equals(record.Manifest.Purpose, SnapshotPurposes.LiveState, StringComparison.OrdinalIgnoreCase)
                && string.Equals(record.Manifest.Kind, SnapshotKinds.PreRestore, StringComparison.OrdinalIgnoreCase);
        }

        private static long SnapshotBytes(SnapshotRecord record)
        {
            return record.Manifest.Files == null ? 0 : record.Manifest.Files.Sum(file => Math.Max(0, file.Length));
        }

        private void ApplyRecord(
            SnapshotRecord record,
            string profilePath,
            bool reconcileMissing,
            RestoreHooks hooks,
            Action<string> onApplied)
        {
            if (record == null
                || record.Manifest == null
                || record.Manifest.SchemaVersion < 2)
            {
                throw new InvalidDataException(
                    "Legacy snapshots do not contain the complete restore envelope.");
            }

            Result<SnapshotRecord> verification = VerifyRecord(record, !reconcileMissing);
            if (!verification.Success)
            {
                throw new InvalidDataException(verification.Message);
            }

            Dictionary<string, SnapshotFileEntry> entries = record.Manifest.Files.ToDictionary(
                item => SaveLayout.NormalizeRelativePath(item.RelativePath),
                item => item,
                StringComparer.OrdinalIgnoreCase);

            int appliedCount = 0;
            foreach (string relativePath in SaveLayout.RestoreOrder)
            {
                SnapshotFileEntry entry;
                string target = SaveLayout.CombineUnderProfile(profilePath, relativePath);
                if (!entries.TryGetValue(relativePath, out entry))
                {
                    if (reconcileMissing && File.Exists(target))
                    {
                        File.Delete(target);
                    }

                    continue;
                }

                string payload = PayloadPath(record.DirectoryPath, relativePath);
                string targetDirectory = Path.GetDirectoryName(target);
                Directory.CreateDirectory(targetDirectory);
                string staged = target + ".nrg-stage-" + Guid.NewGuid().ToString("N");
                try
                {
                    FileTools.CopyShared(payload, staged);
                    if (!string.Equals(
                        FileTools.Sha256(staged, false),
                        entry.Sha256,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException(
                            "Staged restore file failed checksum validation: " + relativePath);
                    }

                    FileTools.AtomicReplace(staged, target);
                    DateTime writeTime;
                    if (DateTime.TryParse(
                        entry.LastWriteUtc,
                        null,
                        System.Globalization.DateTimeStyles.RoundtripKind,
                        out writeTime))
                    {
                        File.SetLastWriteTimeUtc(
                            target,
                            reconcileMissing
                                ? writeTime.ToUniversalTime()
                                : _clock.UtcNow.ToUniversalTime());
                    }
                }
                finally
                {
                    FileTools.TryDeleteFile(staged);
                }

                appliedCount++;
                if (onApplied != null)
                {
                    onApplied(relativePath);
                }

                if (hooks != null && hooks.AfterFileApplied != null)
                {
                    hooks.AfterFileApplied(relativePath, appliedCount);
                }
            }

            VerifyLiveState(profilePath, record.Manifest, reconcileMissing);
        }

        private void VerifyLiveState(
            string profilePath,
            SnapshotManifest manifest,
            bool reconcileMissing)
        {
            Dictionary<string, SnapshotFileEntry> entries = manifest.Files.ToDictionary(
                item => SaveLayout.NormalizeRelativePath(item.RelativePath),
                item => item,
                StringComparer.OrdinalIgnoreCase);

            foreach (string relativePath in SaveLayout.RequiredFiles)
            {
                string livePath = SaveLayout.CombineUnderProfile(profilePath, relativePath);
                SnapshotFileEntry entry;
                if (!entries.TryGetValue(relativePath, out entry))
                {
                    if (reconcileMissing && File.Exists(livePath))
                    {
                        throw new InvalidDataException(
                            "A file that should be absent remains after rollback: " + relativePath);
                    }

                    continue;
                }

                if (!File.Exists(livePath))
                {
                    throw new FileNotFoundException(
                        "A restored save file is missing.",
                        livePath);
                }

                FileInfo info = new FileInfo(livePath);
                if (info.Length != entry.Length
                    || !string.Equals(
                        FileTools.Sha256(livePath, true),
                        entry.Sha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "A restored save file failed final verification: " + relativePath);
                }
            }
        }

        private Result<SnapshotRecord> VerifyRecord(
            SnapshotRecord record,
            bool requireComplete)
        {
            try
            {
                if (record == null || !record.StructurallyValid || record.Manifest == null)
                {
                    return Result<SnapshotRecord>.Fail(
                        "snapshot_invalid",
                        record == null ? "Snapshot is missing." : record.StatusMessage);
                }


                if (record.Manifest.SchemaVersion >= 2
                    && !string.Equals(
                        FileTools.ManifestHash(record.Manifest),
                        record.Manifest.ManifestSha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return Result<SnapshotRecord>.Fail(
                        "snapshot_manifest_hash_mismatch",
                        "Snapshot manifest checksum failed.");
                }

                if (requireComplete && !record.Manifest.Complete)
                {
                    return Result<SnapshotRecord>.Fail(
                        "snapshot_incomplete",
                        "A partial pre-restore snapshot cannot be used as a preparation state.");
                }

                foreach (SnapshotFileEntry entry in record.Manifest.Files)
                {
                    string payload = PayloadPath(record.DirectoryPath, entry.RelativePath);
                    if (!File.Exists(payload))
                    {
                        return Result<SnapshotRecord>.Fail(
                            "snapshot_file_missing",
                            "Snapshot payload is missing: " + entry.RelativePath);
                    }

                    FileInfo info = new FileInfo(payload);
                    if (info.Length != entry.Length)
                    {
                        return Result<SnapshotRecord>.Fail(
                            "snapshot_length_mismatch",
                            "Snapshot payload length changed: " + entry.RelativePath);
                    }

                    string hash = FileTools.Sha256(payload, false);
                    if (!string.Equals(hash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        return Result<SnapshotRecord>.Fail(
                            "snapshot_hash_mismatch",
                            "Snapshot payload checksum failed: " + entry.RelativePath);
                    }
                }

                string composite = FileTools.CompositeHash(record.Manifest.Files);
                if (!string.Equals(
                    composite,
                    record.Manifest.CompositeSha256,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return Result<SnapshotRecord>.Fail(
                        "snapshot_composite_mismatch",
                        "Snapshot manifest checksum failed.");
                }

                if (record.Manifest.SchemaVersion >= 4
                    && string.Equals(
                        record.Manifest.Purpose,
                        SnapshotPurposes.Preparation,
                        StringComparison.OrdinalIgnoreCase))
                {
                    bool nativeExport = string.Equals(
                        record.Manifest.CaptureBasis,
                        SnapshotCaptureBases.NativeExportedState,
                        StringComparison.OrdinalIgnoreCase);
                    Result<bool> slotVerification = nativeExport
                        ? RunSlotSynthesizer.VerifyNativeExport(
                            PayloadPath(record.DirectoryPath, SaveLayout.GameRunPath),
                            PayloadPath(record.DirectoryPath, SaveLayout.RunDataPath),
                            PayloadPath(record.DirectoryPath, SaveLayout.RunParamsPath),
                            PayloadPath(record.DirectoryPath, SaveLayout.RunIconPath),
                            record.Manifest.RunStateCode)
                        : RunSlotSynthesizer.Verify(
                            PayloadPath(record.DirectoryPath, SaveLayout.GameRunPath),
                            PayloadPath(record.DirectoryPath, SaveLayout.RunDataPath),
                            PayloadPath(record.DirectoryPath, SaveLayout.RunParamsPath),
                            PayloadPath(record.DirectoryPath, SaveLayout.RunIconPath),
                            record.Manifest.RunStateCode);
                    if (!slotVerification.Success)
                    {
                        return Result<SnapshotRecord>.Fail(
                            slotVerification.Code,
                            slotVerification.Message);
                    }

                    if (nativeExport)
                    {
                        Result<bool> profileVerification =
                            ProfileSlotCodec.VerifyWorkingExportHash(
                                PayloadPath(record.DirectoryPath, SaveLayout.GameProfilePath),
                                record.Manifest.ProfileSlotStateCode,
                                record.Manifest.ProfileSlotExportSha256);
                        if (!profileVerification.Success)
                        {
                            return Result<SnapshotRecord>.Fail(
                                profileVerification.Code,
                                profileVerification.Message);
                        }
                    }
                }

                return Result<SnapshotRecord>.Ok(
                    record,
                    "snapshot_verified",
                    "Snapshot payload and manifest were verified.");
            }
            catch (Exception exception)
            {
                return Result<SnapshotRecord>.Fail(
                    "snapshot_verification_failed",
                    exception.Message);
            }
        }

        private IList<SnapshotRecord> ListSnapshotsInternal()
        {
            if (!Directory.Exists(SnapshotsRoot))
            {
                return new List<SnapshotRecord>();
            }

            return Directory.GetDirectories(SnapshotsRoot)
                .Where(path => !Path.GetFileName(path).StartsWith(
                    ".pending-",
                    StringComparison.OrdinalIgnoreCase))
                .Select(ReadRecord)
                .OrderByDescending(item => item.CreatedUtc)
                .ToList();
        }

        private SnapshotRecord ReadRecord(string directoryPath)
        {
            SnapshotRecord record = new SnapshotRecord
            {
                DirectoryPath = directoryPath,
                StructurallyValid = false,
                StatusMessage = "Snapshot manifest is unavailable.",
                WorkingRunStateSignal = WorkingRunStateSignals.Unknown
            };

            try
            {
                string manifestPath = Path.Combine(directoryPath, "manifest.json");
                if (!File.Exists(manifestPath))
                {
                    return record;
                }

                SnapshotManifest manifest = FileTools.ReadJson<SnapshotManifest>(manifestPath);
                record.Manifest = manifest;
                string error = ValidateManifestStructure(manifest, directoryPath);
                record.StructurallyValid = string.IsNullOrEmpty(error);
                record.StatusMessage = record.StructurallyValid
                    ? "Snapshot manifest is structurally valid."
                    : error;
                if (record.StructurallyValid
                    && manifest.SchemaVersion >= 3
                    && string.Equals(
                        manifest.Purpose,
                        SnapshotPurposes.Preparation,
                        StringComparison.OrdinalIgnoreCase))
                {
                    Result<WorkingRunState> workingState = WorkingRunStateReader.Inspect(
                        PayloadPath(directoryPath, SaveLayout.GameRunPath));
                    if (workingState.Success)
                    {
                        record.WorkingRunStateCode = workingState.Value.Code;
                        record.WorkingRunStateWord = workingState.Value.StateWord;
                        record.WorkingRunCounter = workingState.Value.Counter;
                        record.WorkingRunStateOffset = workingState.Value.Offset;
                        record.WorkingRunStateSignal = workingState.Value.StateSignal;
                    }
                }
                return record;
            }
            catch (Exception exception)
            {
                record.StatusMessage = exception.Message;
                return record;
            }
        }

        private string ValidateManifestStructure(
            SnapshotManifest manifest,
            string directoryPath)
        {
            if (manifest == null
                || (manifest.SchemaVersion != 1
                    && manifest.SchemaVersion != 2
                    && manifest.SchemaVersion != 3
                    && manifest.SchemaVersion != 4
                    && manifest.SchemaVersion != 5
                    && manifest.SchemaVersion != 6))
            {
                return "Unsupported or missing snapshot manifest.";
            }

            if (string.IsNullOrWhiteSpace(manifest.Id)
                || !string.Equals(
                    Path.GetFileName(directoryPath),
                    manifest.Id,
                    StringComparison.Ordinal))
            {
                return "Snapshot identity does not match its directory.";
            }

            DateTime created;
            if (!DateTime.TryParse(
                manifest.CreatedUtc,
                null,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out created))
            {
                return "Snapshot creation time is invalid.";
            }

            if (manifest.Files == null)
            {
                return "Snapshot file list is missing.";
            }

            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (SnapshotFileEntry entry in manifest.Files)
            {
                string normalized = SaveLayout.NormalizeRelativePath(
                    entry == null ? null : entry.RelativePath);
                if (entry == null
                    || !SaveLayout.IsAllowedRelativePath(normalized)
                    || !seen.Add(normalized)
                    || entry.Length < 0
                    || string.IsNullOrWhiteSpace(entry.Sha256)
                    || entry.Sha256.Length != 64)
                {
                    return "Snapshot file list contains an unsafe or invalid entry.";
                }
            }

            string[] required = manifest.SchemaVersion >= 2
                ? SaveLayout.RequiredFiles
                : SaveLayout.LegacyRequiredFiles;
            if (manifest.Complete && required.Any(
                path => !seen.Contains(path)))
            {
                return "A complete snapshot is missing a required run file.";
            }

            if (manifest.SchemaVersion >= 2)
            {
                if (string.IsNullOrWhiteSpace(manifest.ManifestSha256)
                    || manifest.ManifestSha256.Length != 64
                    || !string.Equals(
                        FileTools.ManifestHash(manifest),
                        manifest.ManifestSha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return "Snapshot manifest checksum is invalid.";
                }

                bool preparation = string.Equals(
                    manifest.Purpose,
                    SnapshotPurposes.Preparation,
                    StringComparison.OrdinalIgnoreCase);
                bool liveState = string.Equals(
                    manifest.Purpose,
                    SnapshotPurposes.LiveState,
                    StringComparison.OrdinalIgnoreCase);
                if (!preparation && !liveState)
                {
                    return "Snapshot purpose is missing or invalid.";
                }

                if (preparation
                    && !SnapshotPolicy.HasCoherentWorkingStateEvidence(manifest))
                {
                    return "Preparation evidence is incomplete or inconsistent.";
                }

                if (preparation
                    && manifest.SchemaVersion >= 4
                    && !SnapshotPolicy.HasValidConfirmationEvidence(manifest))
                {
                    return "Preparation confirmation is missing or invalid.";
                }
            }

            if (string.IsNullOrWhiteSpace(manifest.CompositeSha256)
                || manifest.CompositeSha256.Length != 64)
            {
                return "Snapshot composite checksum is invalid.";
            }

            return null;
        }

        private static bool SameProfile(string first, string second)
        {
            try
            {
                return !string.IsNullOrWhiteSpace(first)
                    && !string.IsNullOrWhiteSpace(second)
                    && string.Equals(
                        Path.GetFullPath(first).TrimEnd(
                            Path.DirectorySeparatorChar,
                            Path.AltDirectorySeparatorChar),
                        Path.GetFullPath(second).TrimEnd(
                            Path.DirectorySeparatorChar,
                            Path.AltDirectorySeparatorChar),
                        StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private string ResolveSnapshotDirectory(string id)
        {
            if (string.IsNullOrWhiteSpace(id)
                || !string.Equals(Path.GetFileName(id), id, StringComparison.Ordinal)
                || id.Contains(".."))
            {
                throw new InvalidDataException("Snapshot identity is unsafe.");
            }

            string root = Path.GetFullPath(SnapshotsRoot)
                .TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string candidate = Path.GetFullPath(Path.Combine(root, id));
            if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Snapshot path escaped the storage root.");
            }

            return candidate;
        }

        private static string PayloadPath(string snapshotDirectory, string relativePath)
        {
            string normalized = SaveLayout.NormalizeRelativePath(relativePath);
            if (!SaveLayout.IsAllowedRelativePath(normalized))
            {
                throw new InvalidDataException("Unsafe snapshot payload path.");
            }

            string root = Path.GetFullPath(Path.Combine(snapshotDirectory, "payload"))
                .TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string candidate = Path.GetFullPath(Path.Combine(
                root,
                normalized.Replace('/', Path.DirectorySeparatorChar)));
            if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Snapshot payload escaped its root.");
            }

            return candidate;
        }

        private static string NativeSourcePath(string sourceRoot, string relativePath)
        {
            string normalized = SaveLayout.NormalizeRelativePath(relativePath);
            if (!SaveLayout.NativeExportSourceFiles.Any(path => string.Equals(
                path,
                normalized,
                StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException("Unsafe native export source path.");
            }

            string root = Path.GetFullPath(sourceRoot)
                .TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string candidate = Path.GetFullPath(Path.Combine(
                root,
                normalized.Replace('/', Path.DirectorySeparatorChar)));
            if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Native export source escaped its staging root.");
            }

            return candidate;
        }

        private static void WritePayloadBytes(string path, byte[] bytes)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using (FileStream stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
        }

        private static bool NativeSourcesMatch(
            string profilePath,
            string sourceRoot,
            IDictionary<string, FileStamp> sourceStamps)
        {
            foreach (string relativePath in SaveLayout.NativeExportSourceFiles)
            {
                FileStamp expected;
                if (!sourceStamps.TryGetValue(relativePath, out expected))
                {
                    return false;
                }

                FileStamp current = FileTools.GetStamp(profilePath, relativePath);
                string copied = NativeSourcePath(sourceRoot, relativePath);
                if (!expected.SameAs(current)
                    || !File.Exists(copied)
                    || !string.Equals(
                        FileTools.Sha256(
                            SaveLayout.CombineUnderProfile(profilePath, relativePath),
                            true),
                        FileTools.Sha256(copied, false),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        private string BuildSnapshotId(string kind)
        {
            return _clock.UtcNow.ToUniversalTime().ToString("yyyyMMdd-HHmmssfff")
                + "-" + NormalizeKind(kind).Replace('_', '-')
                + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        private static string NormalizeKind(string kind)
        {
            if (string.Equals(kind, SnapshotKinds.Manual, StringComparison.OrdinalIgnoreCase))
            {
                return SnapshotKinds.Manual;
            }

            if (string.Equals(kind, SnapshotKinds.Departure, StringComparison.OrdinalIgnoreCase))
            {
                return SnapshotKinds.Departure;
            }

            if (string.Equals(kind, SnapshotKinds.PreRestore, StringComparison.OrdinalIgnoreCase))
            {
                return SnapshotKinds.PreRestore;
            }

            return SnapshotKinds.Manual;
        }

        private static string NormalizePurpose(string purpose, string kind)
        {
            if (string.Equals(
                purpose,
                SnapshotPurposes.Preparation,
                StringComparison.OrdinalIgnoreCase))
            {
                return SnapshotPurposes.Preparation;
            }

            if (string.Equals(
                    purpose,
                    SnapshotPurposes.LiveState,
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    kind,
                    SnapshotKinds.PreRestore,
                    StringComparison.OrdinalIgnoreCase))
            {
                return SnapshotPurposes.LiveState;
            }

            return null;
        }

        private static string ReadPayloadMarker(
            string snapshotDirectory,
            IEnumerable<SnapshotFileEntry> entries)
        {
            bool hasMarker = entries.Any(entry => string.Equals(
                SaveLayout.NormalizeRelativePath(entry.RelativePath),
                SaveLayout.MarkerPath,
                StringComparison.OrdinalIgnoreCase));
            return hasMarker
                ? FileTools.ReadMarker(PayloadPath(snapshotDirectory, SaveLayout.MarkerPath))
                : string.Empty;
        }
    }
}
