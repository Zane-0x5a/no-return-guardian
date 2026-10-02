using System;
using System.Linq;

namespace NoReturnGuardian.Core
{
    public sealed class GuardianMonitor
    {
        private readonly SnapshotStore _store;
        private readonly IGameProcessProbe _gameProbe;
        private readonly IClock _clock;
        private string _profilePath;
        private string _stableSignature;
        private DateTime _stableSinceUtc;
        private bool? _lastGameRunning;
        private bool _encounterObserved;
        private bool _endNotificationRaised;

        public GuardianMonitor(
            SnapshotStore store,
            IGameProcessProbe gameProbe,
            IClock clock)
        {
            _store = store;
            _gameProbe = gameProbe;
            _clock = clock ?? new SystemClock();
            AutoMonitor = false;
            StabilityDelay = TimeSpan.FromMilliseconds(2200);
        }

        public bool AutoMonitor { get; set; }
        public TimeSpan StabilityDelay { get; set; }
        public string LastProtectedSnapshotId { get; private set; }
        public string ProtectedBeforeSuspectedEndId { get; private set; }
        public bool SuspectedRunEnded { get; private set; }

        public event EventHandler<MonitorSnapshotEventArgs> SnapshotCreated;
        public event EventHandler SuspectedEndDetected;

        public void SetProfile(string profilePath)
        {
            string normalized = string.IsNullOrWhiteSpace(profilePath)
                ? null
                : System.IO.Path.GetFullPath(profilePath);
            if (string.Equals(_profilePath, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _profilePath = normalized;
            _stableSignature = null;
            _lastGameRunning = null;
            _encounterObserved = false;
            SuspectedRunEnded = false;
            ProtectedBeforeSuspectedEndId = null;
            _endNotificationRaised = false;
            RefreshProtectedSnapshot();
        }

        public void RefreshProtectedSnapshot()
        {
            SnapshotRecord latest = _store.ListSnapshots()
                .FirstOrDefault(item => SnapshotPolicy.IsConfirmedPreparation(item)
                    && item.Manifest != null
                    && string.Equals(
                        item.Manifest.SourceProfilePath,
                        _profilePath,
                        StringComparison.OrdinalIgnoreCase));
            LastProtectedSnapshotId = latest == null ? null : latest.Manifest.Id;

            if (!string.IsNullOrWhiteSpace(ProtectedBeforeSuspectedEndId)
                && !_store.GetSnapshot(ProtectedBeforeSuspectedEndId).Success)
            {
                ProtectedBeforeSuspectedEndId = LastProtectedSnapshotId;
            }
        }

        public MonitorStatus Poll()
        {
            bool gameRunning = _gameProbe != null && _gameProbe.IsGameRunning();
            bool stoppedAfterObservedEncounter = _lastGameRunning.HasValue
                && _lastGameRunning.Value
                && !gameRunning
                && _encounterObserved;
            _lastGameRunning = gameRunning;
            if (stoppedAfterObservedEncounter)
            {
                MarkSuspectedEnd();
            }

            string executablePath = _gameProbe == null
                ? null
                : _gameProbe.FindGameExecutablePath();
            MonitorStatus status = new MonitorStatus
            {
                GameRunning = gameRunning,
                GameExecutablePath = executablePath,
                LastProtectedSnapshotId = LastProtectedSnapshotId,
                PreferredRestoreSnapshotId = ProtectedBeforeSuspectedEndId
                    ?? LastProtectedSnapshotId
            };

            if (string.IsNullOrWhiteSpace(_profilePath))
            {
                PopulateProtectionStatus(status);
                status.Message = "No save profile is selected.";
                return status;
            }

            Result<RunStateDescriptor> probe = RunStateProbe.Inspect(_profilePath, true);
            if (!probe.Success)
            {
                _stableSignature = null;
                PopulateProtectionStatus(status);
                status.Message = probe.Message;
                return status;
            }

            RunStateDescriptor descriptor = probe.Value;
            status.RunStateComplete = descriptor.Complete
                || descriptor.NativeExportCaptureReady;
            status.ManualCaptureReady = descriptor.ManualCaptureReady;
            status.ManualCaptureStatus = descriptor.ManualCaptureStatus;
            status.ManualCaptureBasis = descriptor.ManualCaptureBasis;
            status.Marker = descriptor.Marker;
            status.WorkingRunStateCode = descriptor.WorkingRunStateCode;
            status.WorkingRunStateWord = descriptor.WorkingRunStateWord;
            status.WorkingRunCounter = descriptor.WorkingRunCounter;
            status.WorkingRunStateSignal = descriptor.WorkingRunStateSignal;

            // The embedded phase word is a save-generation marker, not a reliable
            // scene classifier. A player-confirmed hideout can carry either observed
            // value, so lifecycle end detection must not infer an encounter from it.
            if (gameRunning)
            {
                ClearSuspectedEnd();
            }

            if (!descriptor.ManualCaptureReady)
            {
                _stableSignature = null;
                PopulateProtectionStatus(status);
                status.Message = ManualCaptureMessage(descriptor.ManualCaptureStatus);
                return status;
            }

            if (!string.Equals(
                _stableSignature,
                descriptor.QuickSignature,
                StringComparison.Ordinal))
            {
                _stableSignature = descriptor.QuickSignature;
                _stableSinceUtc = _clock.UtcNow;
                PopulateProtectionStatus(status);
                status.Message = "A coherent write batch was found; waiting for the stability window.";
                return status;
            }

            if (_clock.UtcNow - _stableSinceUtc < StabilityDelay)
            {
                PopulateProtectionStatus(status);
                status.Message = "The coherent write batch is still inside the stability window.";
                return status;
            }

            PopulateProtectionStatus(status);
            bool nativeExport = string.Equals(
                descriptor.ManualCaptureBasis,
                SnapshotCaptureBases.NativeExportedState,
                StringComparison.OrdinalIgnoreCase);
            status.Message = LastProtectedSnapshotId == null
                ? nativeExport
                    ? "A coherent native hideout export is available for manual protection."
                    : "The working state is coherent; use manual protection in the hideout."
                : "Monitoring read-only; only manual hideout points are restorable.";
            return status;
        }

        public Result<SnapshotRecord> ProtectNow(string label, bool userConfirmedHideout)
        {
            if (string.IsNullOrWhiteSpace(_profilePath))
            {
                return Result<SnapshotRecord>.Fail(
                    "profile_missing",
                    "No save profile is selected.");
            }

            if (!userConfirmedHideout)
            {
                return Result<SnapshotRecord>.Fail(
                    "preparation_confirmation_required",
                    "Manual protection requires an explicit player confirmation that the game is in the hideout.");
            }

            Result<RunStateDescriptor> probe = RunStateProbe.Inspect(_profilePath, true);
            if (!probe.Success)
            {
                return Result<SnapshotRecord>.Fail(probe.Code, probe.Message);
            }

            RunStateDescriptor descriptor = probe.Value;
            if (!descriptor.ManualCaptureReady)
            {
                return Result<SnapshotRecord>.Fail(
                    descriptor.ManualCaptureStatus ?? "manual_capture_not_ready",
                    ManualCaptureMessage(descriptor.ManualCaptureStatus));
            }

            if (!string.Equals(
                _stableSignature,
                descriptor.QuickSignature,
                StringComparison.Ordinal))
            {
                _stableSignature = descriptor.QuickSignature;
                _stableSinceUtc = _clock.UtcNow;
                return Result<SnapshotRecord>.Fail(
                    "run_not_stable",
                    "The preparation state just changed. Wait for the stability window and try again.");
            }

            if (_clock.UtcNow - _stableSinceUtc < StabilityDelay)
            {
                return Result<SnapshotRecord>.Fail(
                    "run_not_stable",
                    "The preparation state is still inside the stability window.");
            }

            Result<SnapshotRecord> capture = _store.CaptureHotPreparation(
                _profilePath,
                string.IsNullOrWhiteSpace(label) ? "手动战备" : label,
                true);
            if (capture.Success)
            {
                LastProtectedSnapshotId = capture.Value.Manifest.Id;
                _encounterObserved = false;
                ClearSuspectedEnd();
                OnSnapshotCreated(capture.Value, true);
            }

            return capture;
        }

        private void MarkSuspectedEnd()
        {
            SuspectedRunEnded = true;
            if (string.IsNullOrWhiteSpace(ProtectedBeforeSuspectedEndId))
            {
                ProtectedBeforeSuspectedEndId = LastProtectedSnapshotId;
            }

            if (!_endNotificationRaised)
            {
                _endNotificationRaised = true;
                EventHandler handler = SuspectedEndDetected;
                if (handler != null)
                {
                    handler(this, EventArgs.Empty);
                }
            }
        }

        private void ClearSuspectedEnd()
        {
            SuspectedRunEnded = false;
            ProtectedBeforeSuspectedEndId = null;
            _endNotificationRaised = false;
        }

        private void PopulateProtectionStatus(MonitorStatus status)
        {
            status.SuspectedRunEnded = SuspectedRunEnded;
            status.LastProtectedSnapshotId = LastProtectedSnapshotId;
            status.PreferredRestoreSnapshotId = ProtectedBeforeSuspectedEndId
                ?? LastProtectedSnapshotId;
        }

        private static string ManualCaptureMessage(string code)
        {
            switch (code)
            {
                case "run_working_mirror_mismatch":
                    return "The live R0A working file and its mirror disagree; no snapshot was created.";
                case "run_profile_mirror_mismatch":
                    return "The live profile working file and its mirror disagree; no snapshot was created.";
                case "run_generation_mixed":
                    return "The save files belong to different write generations; no snapshot was created.";
                case "run_working_envelope_partial":
                    return "The game is between working-envelope writes; no snapshot was created.";
                case "native_run_export_invalid":
                case "native_run_export_verification_failed":
                    return "The native R0A export failed strict verification; no snapshot was created.";
                case "native_profile_export_invalid":
                case "native_profile_export_verification_failed":
                    return "The native 0P export failed strict verification; no snapshot was created.";
                case "native_run_metadata_invalid":
                case "native_run_metadata_mismatch":
                case "native_profile_metadata_invalid":
                case "native_profile_metadata_mismatch":
                    return "Native slot metadata disagrees with its payload; no snapshot was created.";
                case "native_run_icon_unknown":
                case "native_profile_icon_unknown":
                    return "A native slot identifier is not recognized; no snapshot was created.";
                case "run_state_word_1_rejected":
                    return "Observed R0A state word 00800001; confirm the hideout before manual protection.";
                case "run_state_word_unrecognized":
                    return "The current R0A state word is not recognized; no snapshot was created.";
                default:
                    return "No coherent preparation state is currently available; no snapshot was created.";
            }
        }

        private void OnSnapshotCreated(SnapshotRecord record, bool manual)
        {
            EventHandler<MonitorSnapshotEventArgs> handler = SnapshotCreated;
            if (handler != null)
            {
                handler(this, new MonitorSnapshotEventArgs
                {
                    Snapshot = record,
                    Manual = manual
                });
            }
        }
    }
}
