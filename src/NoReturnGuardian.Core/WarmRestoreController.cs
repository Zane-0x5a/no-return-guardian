using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace NoReturnGuardian.Core
{
    public static class WarmRestorePhases
    {
        public const string ReadyToSuspend = "ready_to_suspend";
        public const string ProcessSuspended = "process_suspended";
        public const string TargetStagedInCredits = "target_staged_in_t2r_credits";
        public const string MainMenuConfirmed = "main_menu_confirmed_after_t2r";
        public const string TargetLineagePendingPlayer = "target_lineage_observed_pending_player";
        public const string Rejected = "warm_reload_rejected";
        public const string ResumeRequired = "process_resume_required";
        public const string StageFailed = "stage_failed_original_restored";
        public const string Recovered = "original_state_recovered_after_full_exit";
        public const string Accepted = "accepted_in_game_and_trading_station";
    }

    public sealed class WarmRestoreJournal
    {
        public WarmRestoreJournal()
        {
            GameProcessIds = new List<int>();
        }

        public int SchemaVersion { get; set; }
        public string OperationId { get; set; }
        public string StartedUtc { get; set; }
        public string UpdatedUtc { get; set; }
        public string State { get; set; }
        public string ProfilePath { get; set; }
        public string GameExecutablePath { get; set; }
        public string GameExecutableSha256 { get; set; }
        public List<int> GameProcessIds { get; set; }
        public string TargetSnapshotId { get; set; }
        public string TargetStateCode { get; set; }
        public string OriginalStateCode { get; set; }
        public string UndoSnapshotId { get; set; }
        public string OriginalSourceMode { get; set; }
        public string OriginalRunIconSha256 { get; set; }
        public string OriginalSourceSignature { get; set; }
        public string StageEnvelopeSignature { get; set; }
        public string MainMenuEnvelopeMode { get; set; }
        public string MainMenuEnvelopeSignature { get; set; }
        public string MainMenuLineageClassification { get; set; }
        public string LineageClassification { get; set; }
        public bool TargetLineageSupported { get; set; }
        public bool ExactTargetObserved { get; set; }
        public long TargetByteDistance { get; set; }
        public long OriginalByteDistance { get; set; }
        public string ObservedStateCode { get; set; }
        public string AcceptanceBasis { get; set; }
        public string AcceptanceObservedStateCode { get; set; }
        public string PlayerAcceptedUtc { get; set; }
        public string LastFailureCode { get; set; }
        public string LastFailureMessage { get; set; }
    }

    public sealed class WarmRestoreOutcome
    {
        public WarmRestoreJournal Journal { get; set; }
        public string ArchivePath { get; set; }
    }

    public sealed class WarmRestorePreflight
    {
        public string ProfilePath { get; set; }
        public string SnapshotId { get; set; }
        public string SourceSignature { get; set; }
        public string SourceStateCode { get; set; }
    }

    public sealed class WarmRestoreHooks
    {
        public Action BeforeTargetWrite { get; set; }
        public Action<string> AfterJournalPersisted { get; set; }
    }

    public interface IWarmRestoreDelay
    {
        void Wait(TimeSpan duration);
    }

    public sealed class SystemWarmRestoreDelay : IWarmRestoreDelay
    {
        public void Wait(TimeSpan duration)
        {
            if (duration > TimeSpan.Zero)
            {
                Thread.Sleep(duration);
            }
        }
    }

    public interface IWarmRestoreProcessSession : IDisposable
    {
        string ExecutablePath { get; }
        string ExecutableSha256 { get; }
        IList<int> ProcessIds { get; }
        bool Suspended { get; }
        Result<bool> Suspend();
        Result<bool> Resume();
    }

    public interface IWarmRestoreProcessRuntime
    {
        bool IsGameRunning();
        Result<IWarmRestoreProcessSession> OpenValidatedSession();
    }

    public sealed class WindowsWarmRestoreProcessRuntime : IWarmRestoreProcessRuntime
    {
        public const string VerifiedGameSha256 =
            "CAC7F729EAE6FAB743616B8216ACF20E9F05D8E51DA0E3B7A4892147569DD357";

        private static readonly string[] ProcessNames = { "tlou-ii", "tlou-ii-l" };

        public bool IsGameRunning()
        {
            return new GameProcessProbe().IsGameRunning();
        }

        public Result<IWarmRestoreProcessSession> OpenValidatedSession()
        {
            List<Process> processes = new List<Process>();
            try
            {
                foreach (string name in ProcessNames)
                {
                    processes.AddRange(Process.GetProcessesByName(name));
                }

                processes = processes.OrderBy(item => item.Id).ToList();
                if (processes.Count == 0)
                {
                    return Result<IWarmRestoreProcessSession>.Fail(
                        "game_not_running",
                        "The game process is not running.");
                }

                Process primary = processes.FirstOrDefault(item => string.Equals(
                    item.ProcessName,
                    "tlou-ii",
                    StringComparison.OrdinalIgnoreCase));
                if (primary == null)
                {
                    DisposeAll(processes);
                    return Result<IWarmRestoreProcessSession>.Fail(
                        "primary_game_process_missing",
                        "The retail tlou-ii process was not found.");
                }

                string executablePath = primary.MainModule == null
                    ? null
                    : primary.MainModule.FileName;
                if (string.IsNullOrWhiteSpace(executablePath))
                {
                    DisposeAll(processes);
                    return Result<IWarmRestoreProcessSession>.Fail(
                        "game_image_unreadable",
                        "The running game executable could not be resolved.");
                }

                string executableSha256 = FileTools.Sha256(executablePath, true);
                if (!string.Equals(
                    executableSha256,
                    VerifiedGameSha256,
                    StringComparison.OrdinalIgnoreCase))
                {
                    DisposeAll(processes);
                    return Result<IWarmRestoreProcessSession>.Fail(
                        "game_build_unrecognized",
                        "The running game executable is not the verified warm-reload build: "
                        + executableSha256);
                }

                return Result<IWarmRestoreProcessSession>.Ok(
                    new WindowsWarmRestoreProcessSession(
                        processes,
                        executablePath,
                        executableSha256),
                    "game_build_verified",
                    "The running game build is verified for warm reload.");
            }
            catch (Exception exception)
            {
                DisposeAll(processes);
                return Result<IWarmRestoreProcessSession>.Fail(
                    "game_image_unreadable",
                    exception.Message);
            }
        }

        private static void DisposeAll(IEnumerable<Process> processes)
        {
            foreach (Process process in processes ?? Enumerable.Empty<Process>())
            {
                process.Dispose();
            }
        }
    }

    internal sealed class WindowsWarmRestoreProcessSession : IWarmRestoreProcessSession
    {
        private readonly List<Process> _processes;

        public WindowsWarmRestoreProcessSession(
            IEnumerable<Process> processes,
            string executablePath,
            string executableSha256)
        {
            _processes = processes.OrderBy(item => item.Id).ToList();
            ExecutablePath = executablePath;
            ExecutableSha256 = executableSha256;
            ProcessIds = _processes.Select(item => item.Id).ToList();
        }

        public string ExecutablePath { get; private set; }
        public string ExecutableSha256 { get; private set; }
        public IList<int> ProcessIds { get; private set; }
        public bool Suspended { get; private set; }

        public Result<bool> Suspend()
        {
            List<Process> suspended = new List<Process>();
            try
            {
                foreach (Process process in _processes)
                {
                    process.Refresh();
                    if (process.HasExited)
                    {
                        throw new InvalidOperationException(
                            "The game exited before process suspension.");
                    }

                    int status = WarmRestoreNative.NtSuspendProcess(process.Handle);
                    if (status != 0)
                    {
                        throw new InvalidOperationException(
                            "NtSuspendProcess failed for PID " + process.Id
                            + " with status " + status + ".");
                    }

                    suspended.Add(process);
                }

                Suspended = true;
                return Result<bool>.Ok(true, "game_suspended", "Game processes suspended.");
            }
            catch (Exception exception)
            {
                foreach (Process process in suspended.AsEnumerable().Reverse())
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            WarmRestoreNative.NtResumeProcess(process.Handle);
                        }
                    }
                    catch
                    {
                        // The caller receives a fail-closed suspension result below.
                    }
                }

                Suspended = false;
                return Result<bool>.Fail("game_suspend_failed", exception.Message);
            }
        }

        public Result<bool> Resume()
        {
            List<string> failures = new List<string>();
            foreach (Process process in _processes.AsEnumerable().Reverse())
            {
                try
                {
                    process.Refresh();
                    if (!process.HasExited)
                    {
                        int status = WarmRestoreNative.NtResumeProcess(process.Handle);
                        if (status != 0)
                        {
                            failures.Add("PID " + process.Id + " status " + status);
                        }
                    }
                }
                catch (Exception exception)
                {
                    failures.Add("PID " + process.Id + " " + exception.Message);
                }
            }

            if (failures.Count > 0)
            {
                return Result<bool>.Fail(
                    "game_resume_failed",
                    string.Join("; ", failures));
            }

            Suspended = false;
            return Result<bool>.Ok(true, "game_resumed", "Game processes resumed.");
        }

        public void Dispose()
        {
            foreach (Process process in _processes)
            {
                process.Dispose();
            }
        }
    }

    internal static class WarmRestoreNative
    {
        [DllImport("ntdll.dll")]
        internal static extern int NtSuspendProcess(IntPtr processHandle);

        [DllImport("ntdll.dll")]
        internal static extern int NtResumeProcess(IntPtr processHandle);
    }

    public sealed class WarmRestoreController
    {
        private const string ActiveJournalName = "warm-restore.json";
        private readonly object _gate = new object();
        private readonly SnapshotStore _store;
        private readonly IClock _clock;
        private readonly IWarmRestoreProcessRuntime _runtime;
        private readonly IWarmRestoreDelay _delay;

        public WarmRestoreController(SnapshotStore store, IClock clock)
            : this(
                store,
                clock,
                new WindowsWarmRestoreProcessRuntime(),
                new SystemWarmRestoreDelay())
        {
        }

        public WarmRestoreController(
            SnapshotStore store,
            IClock clock,
            IWarmRestoreProcessRuntime runtime,
            IWarmRestoreDelay delay)
        {
            if (store == null)
            {
                throw new ArgumentNullException("store");
            }

            _store = store;
            _clock = clock ?? new SystemClock();
            _runtime = runtime ?? new WindowsWarmRestoreProcessRuntime();
            _delay = delay ?? new SystemWarmRestoreDelay();
            StabilityDelay = TimeSpan.FromMilliseconds(2500);
        }

        public TimeSpan StabilityDelay { get; set; }
        public WarmRestoreHooks Hooks { get; set; }
        public string JournalPath
        {
            get { return Path.Combine(_store.StorageRoot, ActiveJournalName); }
        }

        public bool HasActiveOperation
        {
            get { return File.Exists(JournalPath); }
        }

        public Result<WarmRestoreJournal> GetActiveOperation(string profilePath)
        {
            lock (_gate)
            {
                return ReadJournal(profilePath);
            }
        }

        public Result<bool> PreflightStage(
            string profilePath,
            string snapshotId)
        {
            Result<WarmRestorePreflight> result = PreflightStageEvidence(
                profilePath,
                snapshotId);
            return result.Success
                ? Result<bool>.Ok(true, result.Code, result.Message)
                : Result<bool>.Fail(result.Code, result.Message);
        }

        public Result<WarmRestorePreflight> PreflightStageEvidence(
            string profilePath,
            string snapshotId)
        {
            lock (_gate)
            {
                Result<SnapshotRecord> targetResult = ValidateStageTarget(
                    profilePath,
                    snapshotId);
                if (!targetResult.Success)
                {
                    return Result<WarmRestorePreflight>.Fail(
                        targetResult.Code,
                        targetResult.Message);
                }

                Result<IWarmRestoreProcessSession> sessionResult =
                    _runtime.OpenValidatedSession();
                if (!sessionResult.Success)
                {
                    return Result<WarmRestorePreflight>.Fail(
                        sessionResult.Code,
                        sessionResult.Message);
                }

                using (IWarmRestoreProcessSession session = sessionResult.Value)
                {
                    Result<WarmNativeSource> first = InspectWarmNativeSource(
                        profilePath,
                        targetResult.Value);
                    if (!first.Success)
                    {
                        return Result<WarmRestorePreflight>.Fail(
                            first.Code,
                            first.Message);
                    }

                    _delay.Wait(StabilityDelay);
                    Result<WarmNativeSource> second = InspectWarmNativeSource(
                        profilePath,
                        targetResult.Value);
                    if (!second.Success
                        || !string.Equals(
                            first.Value.Signature,
                            second.Value.Signature,
                            StringComparison.Ordinal)
                        || !string.Equals(
                            first.Value.StateCode,
                            second.Value.StateCode,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return Result<WarmRestorePreflight>.Fail(
                            "warm_restore_source_unstable",
                            "The current exported run changed during the preflight stability gate.");
                    }

                    if (string.Equals(
                        targetResult.Value.Manifest.RunStateCode,
                        second.Value.StateCode,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        return Result<WarmRestorePreflight>.Fail(
                            "warm_restore_target_not_distinct",
                            "Choose an older snapshot so the reload can be proven against the current run.");
                    }

                    return Result<WarmRestorePreflight>.Ok(
                        new WarmRestorePreflight
                        {
                            ProfilePath = Path.GetFullPath(profilePath),
                            SnapshotId = targetResult.Value.Manifest.Id,
                            SourceSignature = second.Value.Signature,
                            SourceStateCode = second.Value.StateCode
                        },
                        "warm_restore_preflight_ready",
                        "The target, current native export, and verified game build are ready for warm navigation.");
                }
            }
        }

        public Result<WarmRestoreOutcome> Stage(
            string profilePath,
            string snapshotId)
        {
            return Stage(profilePath, snapshotId, null);
        }

        public Result<WarmRestoreOutcome> Stage(
            string profilePath,
            string snapshotId,
            WarmRestorePreflight preflight)
        {
            lock (_gate)
            {
                Result<SnapshotRecord> targetResult = ValidateStageTarget(
                    profilePath,
                    snapshotId);
                if (!targetResult.Success)
                {
                    return Result<WarmRestoreOutcome>.Fail(
                        targetResult.Code,
                        targetResult.Message);
                }

                Result<IWarmRestoreProcessSession> sessionResult =
                    _runtime.OpenValidatedSession();
                if (!sessionResult.Success)
                {
                    return Result<WarmRestoreOutcome>.Fail(
                        sessionResult.Code,
                        sessionResult.Message);
                }

                using (IWarmRestoreProcessSession session = sessionResult.Value)
                {
                    return StageWithSession(
                        profilePath,
                        targetResult.Value,
                        session,
                        preflight);
                }
            }
        }

        private Result<SnapshotRecord> ValidateStageTarget(
            string profilePath,
            string snapshotId)
        {
            if (HasActiveOperation)
            {
                return Result<SnapshotRecord>.Fail(
                    "warm_restore_active",
                    "Finish or recover the active warm restore before starting another one.");
            }

            if (_store.HasPendingRestore())
            {
                return Result<SnapshotRecord>.Fail(
                    "restore_pending",
                    "An ordinary restore transaction is still pending.");
            }

            Result<SnapshotRecord> targetResult = _store.VerifySnapshot(snapshotId, true);
            if (!targetResult.Success
                || !SnapshotPolicy.IsConfirmedPreparation(targetResult.Value)
                || !SameProfile(targetResult.Value.Manifest.SourceProfilePath, profilePath))
            {
                return Result<SnapshotRecord>.Fail(
                    targetResult.Success ? "warm_restore_target_rejected" : targetResult.Code,
                    targetResult.Success
                        ? "The selected snapshot is not a confirmed preparation state for this profile."
                        : targetResult.Message);
            }

            return targetResult;
        }

        private Result<WarmRestoreOutcome> StageWithSession(
            string profilePath,
            SnapshotRecord target,
            IWarmRestoreProcessSession session,
            WarmRestorePreflight preflight)
        {
            Result<WarmNativeSource> first = InspectWarmNativeSource(profilePath, target);
            if (!first.Success)
            {
                return Result<WarmRestoreOutcome>.Fail(first.Code, first.Message);
            }

            WarmNativeSource stableSource = first.Value;
            if (preflight == null)
            {
                _delay.Wait(StabilityDelay);
                Result<WarmNativeSource> second = InspectWarmNativeSource(profilePath, target);
                if (!second.Success
                    || !string.Equals(
                        first.Value.Signature,
                        second.Value.Signature,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        first.Value.StateCode,
                        second.Value.StateCode,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return Result<WarmRestoreOutcome>.Fail(
                        "warm_restore_source_unstable",
                        "The current exported run changed during the stability gate.");
                }

                stableSource = second.Value;
            }
            else if (!SameProfile(preflight.ProfilePath, profilePath)
                || !string.Equals(
                    preflight.SnapshotId,
                    target.Manifest.Id,
                    StringComparison.Ordinal)
                || !string.Equals(
                    preflight.SourceSignature,
                    first.Value.Signature,
                    StringComparison.Ordinal)
                || !string.Equals(
                    preflight.SourceStateCode,
                    first.Value.StateCode,
                    StringComparison.OrdinalIgnoreCase))
            {
                return Result<WarmRestoreOutcome>.Fail(
                    "warm_restore_source_changed_after_navigation",
                    "The current exported run changed after preflight and before target staging.");
            }

            string targetStateCode = target.Manifest.RunStateCode;
            if (string.Equals(
                targetStateCode,
                stableSource.StateCode,
                StringComparison.OrdinalIgnoreCase))
            {
                return Result<WarmRestoreOutcome>.Fail(
                    "warm_restore_target_not_distinct",
                    "Choose an older snapshot so the reload can be proven against the current run.");
            }

            Result<SnapshotRecord> undoResult = _store.Capture(
                profilePath,
                new CaptureOptions
                {
                    Kind = SnapshotKinds.PreRestore,
                    Purpose = SnapshotPurposes.LiveState,
                    Label = "暖恢复前现场",
                    AllowIncomplete = true,
                    SkipDeduplication = true,
                    SkipRetention = true
                });
            if (!undoResult.Success)
            {
                return Result<WarmRestoreOutcome>.Fail(
                    "warm_restore_undo_capture_failed",
                    undoResult.Message);
            }

            Result<WarmNativeSource> secured = InspectWarmNativeSource(profilePath, target);
            if (!secured.Success
                || !string.Equals(
                    stableSource.Signature,
                    secured.Value.Signature,
                    StringComparison.Ordinal))
            {
                return Result<WarmRestoreOutcome>.Fail(
                    "warm_restore_source_changed_after_undo",
                    "The current export changed while its recovery point was being secured.");
            }

            WarmRestoreJournal journal = new WarmRestoreJournal
            {
                SchemaVersion = 2,
                OperationId = _clock.UtcNow.ToUniversalTime().ToString("yyyyMMdd-HHmmssfff")
                    + "-warm-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                StartedUtc = _clock.UtcNow.ToUniversalTime().ToString("O"),
                State = WarmRestorePhases.ReadyToSuspend,
                ProfilePath = Path.GetFullPath(profilePath),
                GameExecutablePath = session.ExecutablePath,
                GameExecutableSha256 = session.ExecutableSha256,
                GameProcessIds = session.ProcessIds.OrderBy(value => value).ToList(),
                TargetSnapshotId = target.Manifest.Id,
                TargetStateCode = targetStateCode,
                OriginalStateCode = secured.Value.StateCode,
                UndoSnapshotId = undoResult.Value.Manifest.Id,
                OriginalSourceMode = secured.Value.SourceMode,
                OriginalRunIconSha256 = secured.Value.RunIconSha256,
                OriginalSourceSignature = secured.Value.Signature
            };
            PersistJournal(journal);

            bool targetApplied = false;
            bool compensated = false;
            OperationFailure failure = null;
            Result<bool> resumeResult = null;
            try
            {
                Result<bool> suspendResult = session.Suspend();
                if (!suspendResult.Success)
                {
                    throw new OperationFailure(suspendResult.Code, suspendResult.Message);
                }

                journal.State = WarmRestorePhases.ProcessSuspended;
                PersistJournal(journal);

                Result<WarmNativeSource> suspendedSource = InspectWarmNativeSource(
                    profilePath,
                    target);
                if (!suspendedSource.Success
                    || !string.Equals(
                        journal.OriginalSourceSignature,
                        suspendedSource.Value.Signature,
                        StringComparison.Ordinal))
                {
                    throw new OperationFailure(
                        "warm_restore_source_changed_before_write",
                        "Save files changed between the stability gate and process suspension.");
                }

                if (Hooks != null && Hooks.BeforeTargetWrite != null)
                {
                    Hooks.BeforeTargetWrite();
                }

                Result<RestoreOutcome> restore = _store.RestorePrepared(
                    journal.TargetSnapshotId,
                    journal.UndoSnapshotId,
                    profilePath,
                    new WarmRestoreStoppedProbe(),
                    null);
                if (!restore.Success)
                {
                    throw new OperationFailure(restore.Code, restore.Message);
                }

                targetApplied = true;
                if (!AllTargetFilesMatch(profilePath, target.Manifest))
                {
                    throw new OperationFailure(
                        "warm_restore_target_verification_failed",
                        "The suspended disk envelope did not match the target snapshot.");
                }

                journal.StageEnvelopeSignature = FileTools.Sha256Text(
                    LiveSignature(profilePath));
                journal.State = WarmRestorePhases.TargetStagedInCredits;
                PersistJournal(journal);
            }
            catch (Exception exception)
            {
                failure = exception as OperationFailure
                    ?? new OperationFailure("warm_restore_stage_failed", exception.Message);
                if (targetApplied)
                {
                    Result<RestoreOutcome> compensation = _store.Undo(
                        journal.UndoSnapshotId,
                        profilePath,
                        new WarmRestoreStoppedProbe());
                    compensated = compensation.Success;
                    if (!compensation.Success)
                    {
                        failure = new OperationFailure(
                            "warm_restore_compensation_failed",
                            failure.Message + " Compensation failed: " + compensation.Message);
                    }
                }
                else
                {
                    compensated = !_store.HasPendingRestore();
                }

                journal.State = compensated
                    ? WarmRestorePhases.StageFailed
                    : WarmRestorePhases.ResumeRequired;
                journal.LastFailureCode = failure.Code;
                journal.LastFailureMessage = failure.Message;
                PersistJournal(journal);
            }
            finally
            {
                resumeResult = session.Resume();
            }

            if (resumeResult == null || !resumeResult.Success)
            {
                journal.State = WarmRestorePhases.ResumeRequired;
                journal.LastFailureCode = resumeResult == null
                    ? "game_resume_failed"
                    : resumeResult.Code;
                journal.LastFailureMessage = resumeResult == null
                    ? "The game process resume result is unavailable."
                    : resumeResult.Message;
                PersistJournal(journal);
                return Result<WarmRestoreOutcome>.Fail(
                    journal.LastFailureCode,
                    journal.LastFailureMessage);
            }

            if (failure != null)
            {
                string archive = compensated ? ArchiveJournal(journal) : null;
                return Result<WarmRestoreOutcome>.Fail(
                    failure.Code,
                    compensated
                        ? failure.Message + " The original disk state was restored. Evidence: "
                            + archive
                        : failure.Message);
            }

            _delay.Wait(TimeSpan.FromMilliseconds(2000));
            if (!AllTargetFilesMatch(profilePath, target.Manifest))
            {
                journal.LastFailureCode = "warm_restore_target_changed_in_credits";
                journal.LastFailureMessage =
                    "The credits page changed the staged target before returning to the main menu.";
                PersistJournal(journal);
                return Result<WarmRestoreOutcome>.Fail(
                    journal.LastFailureCode,
                    journal.LastFailureMessage);
            }

            return Result<WarmRestoreOutcome>.Ok(
                new WarmRestoreOutcome { Journal = journal },
                "warm_restore_target_staged",
                "The selected snapshot is staged. Return from T2R credits to the main menu.");
        }

        public Result<WarmRestoreOutcome> CheckpointMainMenu(string profilePath)
        {
            lock (_gate)
            {
                Result<WarmRestoreJournal> journalResult = ReadJournal(profilePath);
                if (!journalResult.Success || journalResult.Value == null)
                {
                    return Result<WarmRestoreOutcome>.Fail(
                        journalResult.Success ? "warm_restore_not_active" : journalResult.Code,
                        journalResult.Success
                            ? "There is no warm restore waiting for a main-menu checkpoint."
                            : journalResult.Message);
                }

                WarmRestoreJournal journal = journalResult.Value;
                if (!string.Equals(
                    journal.State,
                    WarmRestorePhases.TargetStagedInCredits,
                    StringComparison.Ordinal))
                {
                    return Result<WarmRestoreOutcome>.Fail(
                        "warm_restore_checkpoint_not_ready",
                        "Return from T2R credits only after the target staging phase completes.");
                }

                Result<IWarmRestoreProcessSession> sessionResult =
                    _runtime.OpenValidatedSession();
                if (!sessionResult.Success)
                {
                    return Result<WarmRestoreOutcome>.Fail(
                        sessionResult.Code,
                        sessionResult.Message);
                }

                using (IWarmRestoreProcessSession session = sessionResult.Value)
                {
                    Result<SnapshotRecord> target = _store.VerifySnapshot(
                        journal.TargetSnapshotId,
                        true);
                    if (!target.Success)
                    {
                        return Result<WarmRestoreOutcome>.Fail(
                            target.Code,
                            target.Message);
                    }

                    Result<RunStateDescriptor> firstProbe = RunStateProbe.Inspect(
                        profilePath,
                        true);
                    string firstSignature = LiveSignature(profilePath);
                    _delay.Wait(StabilityDelay);
                    Result<RunStateDescriptor> secondProbe = RunStateProbe.Inspect(
                        profilePath,
                        true);
                    string secondSignature = LiveSignature(profilePath);
                    if (!firstProbe.Success
                        || !secondProbe.Success
                        || !firstProbe.Value.SameCaptureState(secondProbe.Value)
                        || !string.Equals(
                            firstSignature,
                            secondSignature,
                            StringComparison.Ordinal))
                    {
                        return Result<WarmRestoreOutcome>.Fail(
                            "warm_restore_main_menu_unstable",
                            "Wait for the post-credits main-menu save activity to settle.");
                    }

                    Result<LiveRawRun> liveRun = ReadLiveRawRun(profilePath, target.Value);
                    if (!liveRun.Success
                        || !string.Equals(
                            secondSignature,
                            LiveSignature(profilePath),
                            StringComparison.Ordinal))
                    {
                        return Result<WarmRestoreOutcome>.Fail(
                            "warm_restore_main_menu_envelope_invalid",
                            liveRun.Success
                                ? "Save files changed while the main-menu evidence was read."
                                : liveRun.Message);
                    }

                    SnapshotFileEntry targetMarker = target.Value.Manifest.Files.FirstOrDefault(
                        entry => string.Equals(
                            entry.RelativePath,
                            SaveLayout.MarkerPath,
                            StringComparison.OrdinalIgnoreCase));
                    if (targetMarker == null || !TargetFileMatches(profilePath, targetMarker))
                    {
                        return Result<WarmRestoreOutcome>.Fail(
                            "warm_restore_main_menu_marker_changed",
                            "The No Return marker no longer matches the staged target.");
                    }

                    Result<WarmReloadLineageEvidence> analysis = AnalyzeObserved(
                        journal,
                        liveRun.Value.StateCode,
                        liveRun.Value.RawBytes);
                    if (!analysis.Success || !analysis.Value.TargetLineageSupported)
                    {
                        journal.State = WarmRestorePhases.Rejected;
                        journal.MainMenuLineageClassification = analysis.Success
                            ? analysis.Value.Classification
                            : analysis.Code;
                        journal.LastFailureCode = "warm_restore_main_menu_target_changed";
                        journal.LastFailureMessage = analysis.Success
                            ? analysis.Value.Classification
                            : analysis.Message;
                        PersistJournal(journal);
                        return Result<WarmRestoreOutcome>.Fail(
                            journal.LastFailureCode,
                            journal.LastFailureMessage);
                    }

                    string exactMode = ClassifyTargetEnvelope(profilePath, target.Value.Manifest);
                    journal.MainMenuEnvelopeMode = string.Equals(
                        exactMode,
                        "changed",
                        StringComparison.Ordinal)
                        ? liveRun.Value.SourceKind + "_target_lineage"
                        : exactMode;
                    journal.MainMenuEnvelopeSignature = FileTools.Sha256Text(secondSignature);
                    journal.MainMenuLineageClassification = analysis.Value.Classification;
                    journal.State = WarmRestorePhases.MainMenuConfirmed;
                    PersistJournal(journal);

                    return Result<WarmRestoreOutcome>.Ok(
                        new WarmRestoreOutcome { Journal = journal },
                        "warm_restore_main_menu_confirmed",
                        "The post-credits main-menu lineage matches the target. Enter No Return and stop in the hideout.");
                }
            }
        }

        public Result<WarmRestoreOutcome> VerifyHideout(string profilePath)
        {
            lock (_gate)
            {
                Result<WarmRestoreJournal> journalResult = ReadJournal(profilePath);
                if (!journalResult.Success || journalResult.Value == null)
                {
                    return Result<WarmRestoreOutcome>.Fail(
                        journalResult.Success ? "warm_restore_not_active" : journalResult.Code,
                        journalResult.Success
                            ? "There is no warm restore waiting for hideout verification."
                            : journalResult.Message);
                }

                WarmRestoreJournal journal = journalResult.Value;
                if (!string.Equals(
                    journal.State,
                    WarmRestorePhases.MainMenuConfirmed,
                    StringComparison.Ordinal))
                {
                    return Result<WarmRestoreOutcome>.Fail(
                        "warm_restore_hideout_not_ready",
                        "Record the post-credits main-menu checkpoint before verifying No Return.");
                }

                Result<IWarmRestoreProcessSession> sessionResult =
                    _runtime.OpenValidatedSession();
                if (!sessionResult.Success)
                {
                    return Result<WarmRestoreOutcome>.Fail(
                        sessionResult.Code,
                        sessionResult.Message);
                }

                using (IWarmRestoreProcessSession session = sessionResult.Value)
                {
                    Result<WarmReloadLineageEvidence> analysis = AnalyzeCurrent(
                        profilePath,
                        journal);
                    if (!analysis.Success)
                    {
                        return Result<WarmRestoreOutcome>.Fail(
                            analysis.Code,
                            analysis.Message);
                    }

                    ApplyAnalysis(journal, analysis.Value);
                    journal.State = analysis.Value.TargetLineageSupported
                        ? WarmRestorePhases.TargetLineagePendingPlayer
                        : WarmRestorePhases.Rejected;
                    if (!analysis.Value.TargetLineageSupported)
                    {
                        journal.LastFailureCode = "warm_restore_rejected";
                        journal.LastFailureMessage = analysis.Value.Classification;
                    }

                    PersistJournal(journal);
                    if (!analysis.Value.TargetLineageSupported)
                    {
                        return Result<WarmRestoreOutcome>.Fail(
                            journal.LastFailureCode,
                            journal.LastFailureMessage);
                    }

                    return Result<WarmRestoreOutcome>.Ok(
                        new WarmRestoreOutcome { Journal = journal },
                        "warm_restore_hideout_verified",
                        "The target lineage was observed in No Return. Verify the trading station before accepting the restore.");
                }
            }
        }

        public Result<WarmRestoreOutcome> AcceptTradingStation(string profilePath)
        {
            lock (_gate)
            {
                Result<WarmRestoreJournal> journalResult = ReadJournal(profilePath);
                if (!journalResult.Success || journalResult.Value == null)
                {
                    return Result<WarmRestoreOutcome>.Fail(
                        journalResult.Success ? "warm_restore_not_active" : journalResult.Code,
                        journalResult.Success
                            ? "There is no warm restore waiting for player acceptance."
                            : journalResult.Message);
                }

                WarmRestoreJournal journal = journalResult.Value;
                if (!string.Equals(
                    journal.State,
                    WarmRestorePhases.TargetLineagePendingPlayer,
                    StringComparison.Ordinal)
                    || !journal.TargetLineageSupported)
                {
                    return Result<WarmRestoreOutcome>.Fail(
                        "warm_restore_lineage_required",
                        "A verified hideout target lineage is required before player acceptance.");
                }

                Result<IWarmRestoreProcessSession> sessionResult =
                    _runtime.OpenValidatedSession();
                if (!sessionResult.Success)
                {
                    return Result<WarmRestoreOutcome>.Fail(
                        sessionResult.Code,
                        sessionResult.Message);
                }

                using (IWarmRestoreProcessSession session = sessionResult.Value)
                {
                    Result<RunStateDescriptor> first = RunStateProbe.Inspect(profilePath, true);
                    _delay.Wait(StabilityDelay);
                    Result<RunStateDescriptor> second = RunStateProbe.Inspect(profilePath, true);
                    if (!ContinuationMatches(journal, first)
                        || !ContinuationMatches(journal, second))
                    {
                        return Result<WarmRestoreOutcome>.Fail(
                            "warm_restore_lineage_changed",
                            "The current hideout no longer supports the verified target lineage.");
                    }

                    journal.State = WarmRestorePhases.Accepted;
                    journal.AcceptanceBasis =
                        "locked_reentry_lineage_plus_target_counter_continuation";
                    journal.AcceptanceObservedStateCode = second.Value.WorkingRunStateCode;
                    journal.PlayerAcceptedUtc = _clock.UtcNow.ToUniversalTime().ToString("O");
                    string archive = ArchiveJournal(journal);
                    return Result<WarmRestoreOutcome>.Ok(
                        new WarmRestoreOutcome
                        {
                            Journal = journal,
                            ArchivePath = archive
                        },
                        "warm_restore_accepted",
                        "The warm restore was accepted in-game and at the trading station.");
                }
            }
        }

        public Result<WarmRestoreOutcome> RecoverAfterFullExit(string profilePath)
        {
            lock (_gate)
            {
                Result<WarmRestoreJournal> journalResult = ReadJournal(profilePath);
                if (!journalResult.Success || journalResult.Value == null)
                {
                    return Result<WarmRestoreOutcome>.Fail(
                        journalResult.Success ? "warm_restore_not_active" : journalResult.Code,
                        journalResult.Success
                            ? "There is no active warm restore to recover."
                            : journalResult.Message);
                }

                if (_runtime.IsGameRunning())
                {
                    return Result<WarmRestoreOutcome>.Fail(
                        "game_running",
                        "Exit the game completely before recovering the original run state.");
                }

                WarmRestoreJournal journal = journalResult.Value;
                if (_store.HasPendingRestore())
                {
                    Result<RestoreOutcome> interrupted = _store.RecoverInterruptedRestore(
                        new WarmRestoreStoppedProbe());
                    if (!interrupted.Success)
                    {
                        return Result<WarmRestoreOutcome>.Fail(
                            interrupted.Code,
                            interrupted.Message);
                    }
                }

                Result<RestoreOutcome> recovery = _store.Undo(
                    journal.UndoSnapshotId,
                    profilePath,
                    new WarmRestoreStoppedProbe());
                if (!recovery.Success)
                {
                    return Result<WarmRestoreOutcome>.Fail(
                        recovery.Code,
                        recovery.Message);
                }

                journal.State = WarmRestorePhases.Recovered;
                string archive = ArchiveJournal(journal);
                return Result<WarmRestoreOutcome>.Ok(
                    new WarmRestoreOutcome
                    {
                        Journal = journal,
                        ArchivePath = archive
                    },
                    "warm_restore_original_recovered",
                    "The original disk state was restored after full game exit.");
            }
        }

        public Result<WarmRestoreOutcome> ResumeInterruptedStage(string profilePath)
        {
            lock (_gate)
            {
                Result<WarmRestoreJournal> journalResult = ReadJournal(profilePath);
                if (!journalResult.Success || journalResult.Value == null)
                {
                    return Result<WarmRestoreOutcome>.Fail(
                        journalResult.Success ? "warm_restore_not_active" : journalResult.Code,
                        journalResult.Success
                            ? "There is no interrupted warm restore."
                            : journalResult.Message);
                }

                WarmRestoreJournal journal = journalResult.Value;
                bool stagingWindow = string.Equals(
                        journal.State,
                        WarmRestorePhases.ReadyToSuspend,
                        StringComparison.Ordinal)
                    || string.Equals(
                        journal.State,
                        WarmRestorePhases.ProcessSuspended,
                        StringComparison.Ordinal)
                    || string.Equals(
                        journal.State,
                        WarmRestorePhases.ResumeRequired,
                        StringComparison.Ordinal);
                if (!stagingWindow)
                {
                    return Result<WarmRestoreOutcome>.Ok(
                        new WarmRestoreOutcome { Journal = journal },
                        "warm_restore_resumable",
                        "The warm restore is waiting at a stable player-confirmation phase.");
                }

                Result<IWarmRestoreProcessSession> sessionResult =
                    _runtime.OpenValidatedSession();
                if (!sessionResult.Success)
                {
                    return Result<WarmRestoreOutcome>.Fail(
                        sessionResult.Code,
                        sessionResult.Message);
                }

                using (IWarmRestoreProcessSession session = sessionResult.Value)
                {
                    if (!SameProcessSet(journal.GameProcessIds, session.ProcessIds)
                        || !string.Equals(
                            journal.GameExecutableSha256,
                            session.ExecutableSha256,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return Result<WarmRestoreOutcome>.Fail(
                            "warm_restore_process_changed",
                            "The running game instance is not the one recorded by the interrupted warm restore.");
                    }

                    if (string.Equals(
                        journal.State,
                        WarmRestorePhases.ReadyToSuspend,
                        StringComparison.Ordinal))
                    {
                        Result<bool> resumeBeforeWrite = session.Resume();
                        if (!resumeBeforeWrite.Success)
                        {
                            return Result<WarmRestoreOutcome>.Fail(
                                resumeBeforeWrite.Code,
                                resumeBeforeWrite.Message);
                        }

                        journal.State = WarmRestorePhases.StageFailed;
                        journal.LastFailureCode = "warm_restore_interrupted_before_write";
                        journal.LastFailureMessage =
                            "The interrupted operation ended before any target write began.";
                        string archive = ArchiveJournal(journal);
                        return Result<WarmRestoreOutcome>.Ok(
                            new WarmRestoreOutcome
                            {
                                Journal = journal,
                                ArchivePath = archive
                            },
                            journal.LastFailureCode,
                            journal.LastFailureMessage);
                    }

                    if (_store.HasPendingRestore())
                    {
                        Result<RestoreOutcome> rollback = _store.RecoverInterruptedRestore(
                            new WarmRestoreStoppedProbe());
                        if (!rollback.Success)
                        {
                            return Result<WarmRestoreOutcome>.Fail(
                                rollback.Code,
                                rollback.Message);
                        }

                        Result<bool> resumedAfterRollback = session.Resume();
                        if (!resumedAfterRollback.Success)
                        {
                            return Result<WarmRestoreOutcome>.Fail(
                                resumedAfterRollback.Code,
                                resumedAfterRollback.Message);
                        }

                        journal.State = WarmRestorePhases.StageFailed;
                        journal.LastFailureCode = "warm_restore_interrupted_write_rolled_back";
                        journal.LastFailureMessage =
                            "The interrupted target write was rolled back before the game resumed.";
                        string archive = ArchiveJournal(journal);
                        return Result<WarmRestoreOutcome>.Ok(
                            new WarmRestoreOutcome
                            {
                                Journal = journal,
                                ArchivePath = archive
                            },
                            journal.LastFailureCode,
                            journal.LastFailureMessage);
                    }

                    Result<SnapshotRecord> target = _store.VerifySnapshot(
                        journal.TargetSnapshotId,
                        true);
                    if (target.Success && AllTargetFilesMatch(profilePath, target.Value.Manifest))
                    {
                        Result<bool> resumedTarget = session.Resume();
                        if (!resumedTarget.Success)
                        {
                            return Result<WarmRestoreOutcome>.Fail(
                                resumedTarget.Code,
                                resumedTarget.Message);
                        }

                        journal.State = WarmRestorePhases.TargetStagedInCredits;
                        PersistJournal(journal);
                        return Result<WarmRestoreOutcome>.Ok(
                            new WarmRestoreOutcome { Journal = journal },
                            "warm_restore_target_staged",
                            "The fully staged target was preserved and the game process resumed.");
                    }

                    Result<WarmNativeSource> current = target.Success
                        ? InspectWarmNativeSource(profilePath, target.Value)
                        : Result<WarmNativeSource>.Fail(target.Code, target.Message);
                    if (current.Success
                        && string.Equals(
                            current.Value.Signature,
                            journal.OriginalSourceSignature,
                            StringComparison.Ordinal))
                    {
                        Result<bool> resumedOriginal = session.Resume();
                        if (!resumedOriginal.Success)
                        {
                            return Result<WarmRestoreOutcome>.Fail(
                                resumedOriginal.Code,
                                resumedOriginal.Message);
                        }

                        journal.State = WarmRestorePhases.StageFailed;
                        journal.LastFailureCode = "warm_restore_interrupted_original_intact";
                        journal.LastFailureMessage =
                            "The original exported run remained intact; no target write was retained.";
                        string archive = ArchiveJournal(journal);
                        return Result<WarmRestoreOutcome>.Ok(
                            new WarmRestoreOutcome
                            {
                                Journal = journal,
                                ArchivePath = archive
                            },
                            journal.LastFailureCode,
                            journal.LastFailureMessage);
                    }

                    journal.LastFailureCode = "warm_restore_interrupted_state_inconclusive";
                    journal.LastFailureMessage =
                        "The interrupted disk state matches neither the exact target nor the secured original. Exit the game before recovery.";
                    PersistJournal(journal);
                    return Result<WarmRestoreOutcome>.Fail(
                        journal.LastFailureCode,
                        journal.LastFailureMessage);
                }
            }
        }

        private Result<WarmReloadLineageEvidence> AnalyzeCurrent(
            string profilePath,
            WarmRestoreJournal journal)
        {
            Result<RunStateDescriptor> first = RunStateProbe.Inspect(profilePath, true);
            if (!WorkingHideoutReady(first))
            {
                return Result<WarmReloadLineageEvidence>.Fail(
                    "warm_restore_hideout_not_ready",
                    first.Success ? first.Value.ManualCaptureStatus : first.Message);
            }

            _delay.Wait(StabilityDelay);
            Result<RunStateDescriptor> second = RunStateProbe.Inspect(profilePath, true);
            if (!WorkingHideoutReady(second)
                || !first.Value.SameCaptureState(second.Value))
            {
                return Result<WarmReloadLineageEvidence>.Fail(
                    "warm_restore_hideout_unstable",
                    "The observed hideout state changed during the lineage gate.");
            }

            Result<LiveRawRun> observed = ReadLiveRawRun(profilePath);
            if (!observed.Success)
            {
                return Result<WarmReloadLineageEvidence>.Fail(
                    observed.Code,
                    observed.Message);
            }

            return AnalyzeObserved(
                journal,
                second.Value.WorkingRunStateCode,
                observed.Value.RawBytes);
        }

        private Result<WarmReloadLineageEvidence> AnalyzeObserved(
            WarmRestoreJournal journal,
            string observedStateCode,
            byte[] observedRun)
        {
            Result<SnapshotRecord> target = _store.VerifySnapshot(
                journal.TargetSnapshotId,
                true);
            Result<SnapshotRecord> undo = _store.VerifySnapshot(
                journal.UndoSnapshotId,
                false);
            if (!target.Success || !undo.Success)
            {
                return Result<WarmReloadLineageEvidence>.Fail(
                    "warm_restore_evidence_snapshot_invalid",
                    target.Success ? undo.Message : target.Message);
            }

            byte[] targetRun = File.ReadAllBytes(
                SnapshotPayload(target.Value, SaveLayout.GameRunPath));
            Result<byte[]> originalRun = ReadRawRun(undo.Value, target.Value);
            if (!originalRun.Success)
            {
                return Result<WarmReloadLineageEvidence>.Fail(
                    originalRun.Code,
                    originalRun.Message);
            }

            return WarmReloadLineageAnalyzer.Analyze(
                journal.TargetStateCode,
                journal.OriginalStateCode,
                observedStateCode,
                targetRun,
                originalRun.Value,
                observedRun);
        }

        private static Result<LiveRawRun> ReadLiveRawRun(
            string profilePath,
            SnapshotRecord canonicalTarget = null)
        {
            string[] workingFiles =
            {
                SaveLayout.GameRunPath,
                SaveLayout.GameRunBackupPath,
                SaveLayout.GameProfilePath,
                SaveLayout.GameProfileBackupPath
            };
            int workingCount = workingFiles.Count(relativePath => File.Exists(
                SaveLayout.CombineUnderProfile(profilePath, relativePath)));
            if (workingCount == workingFiles.Length)
            {
                string run = SaveLayout.CombineUnderProfile(
                    profilePath,
                    SaveLayout.GameRunPath);
                string runBackup = SaveLayout.CombineUnderProfile(
                    profilePath,
                    SaveLayout.GameRunBackupPath);
                string profile = SaveLayout.CombineUnderProfile(
                    profilePath,
                    SaveLayout.GameProfilePath);
                string profileBackup = SaveLayout.CombineUnderProfile(
                    profilePath,
                    SaveLayout.GameProfileBackupPath);
                if (!string.Equals(
                        FileTools.Sha256(run, true),
                        FileTools.Sha256(runBackup, true),
                        StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(
                        FileTools.Sha256(profile, true),
                        FileTools.Sha256(profileBackup, true),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return Result<LiveRawRun>.Fail(
                        "live_working_mirrors_diverge",
                        "The live working mirrors are not byte-identical.");
                }

                Result<WorkingRunState> state = WorkingRunStateReader.Inspect(run);
                return state.Success
                    ? Result<LiveRawRun>.Ok(
                        new LiveRawRun
                        {
                            RawBytes = File.ReadAllBytes(run),
                            StateCode = state.Value.Code,
                            SourceKind = "working_envelope"
                        },
                        "live_working_run_read",
                        "Live working R0A read.")
                    : Result<LiveRawRun>.Fail(state.Code, state.Message);
            }

            if (workingCount != 0)
            {
                return Result<LiveRawRun>.Fail(
                    "live_working_envelope_partial",
                    "Only part of the live working envelope is present.");
            }

            Result<DecodedSlotPayload> decoded = RunSlotSynthesizer.DecodeNativeExport(
                SaveLayout.CombineUnderProfile(profilePath, SaveLayout.RunDataPath),
                SaveLayout.CombineUnderProfile(profilePath, SaveLayout.RunParamsPath),
                SaveLayout.CombineUnderProfile(profilePath, SaveLayout.RunIconPath));
            if (!decoded.Success
                && string.Equals(
                    decoded.Code,
                    "native_run_icon_unknown",
                    StringComparison.Ordinal)
                && canonicalTarget != null)
            {
                decoded = RunSlotSynthesizer.DecodeNativeExport(
                    SaveLayout.CombineUnderProfile(profilePath, SaveLayout.RunDataPath),
                    SaveLayout.CombineUnderProfile(profilePath, SaveLayout.RunParamsPath),
                    SnapshotPayload(canonicalTarget, SaveLayout.RunIconPath));
            }

            return decoded.Success
                ? Result<LiveRawRun>.Ok(
                    new LiveRawRun
                    {
                        RawBytes = decoded.Value.RawBytes,
                        StateCode = decoded.Value.StateCode,
                        SourceKind = "native_slot"
                    },
                    "live_native_run_decoded",
                    "Live native R0A decoded.")
                : Result<LiveRawRun>.Fail(decoded.Code, decoded.Message);
        }

        private static Result<byte[]> ReadRawRun(
            SnapshotRecord record,
            SnapshotRecord canonicalTarget)
        {
            string working = SnapshotPayload(record, SaveLayout.GameRunPath);
            if (File.Exists(working))
            {
                return Result<byte[]>.Ok(
                    File.ReadAllBytes(working),
                    "working_run_read",
                    "Working R0A read.");
            }

            string data = SnapshotPayload(record, SaveLayout.RunDataPath);
            string parameters = SnapshotPayload(record, SaveLayout.RunParamsPath);
            string icon = SnapshotPayload(record, SaveLayout.RunIconPath);
            if (!File.Exists(data) || !File.Exists(parameters) || !File.Exists(icon))
            {
                return Result<byte[]>.Fail(
                    "warm_restore_original_run_missing",
                    "The secured pre-stage snapshot has no reconstructable R0A payload.");
            }

            Result<DecodedSlotPayload> decoded = RunSlotSynthesizer.DecodeNativeExport(
                data,
                parameters,
                icon);
            if (!decoded.Success
                && string.Equals(
                    decoded.Code,
                    "native_run_icon_unknown",
                    StringComparison.Ordinal)
                && canonicalTarget != null)
            {
                decoded = RunSlotSynthesizer.DecodeNativeExport(
                    data,
                    parameters,
                    SnapshotPayload(canonicalTarget, SaveLayout.RunIconPath));
            }

            return decoded.Success
                ? Result<byte[]>.Ok(
                    decoded.Value.RawBytes,
                    "native_run_decoded",
                    "Native R0A decoded.")
                : Result<byte[]>.Fail(decoded.Code, decoded.Message);
        }

        private static Result<WarmNativeSource> InspectWarmNativeSource(
            string profilePath,
            SnapshotRecord canonicalTarget)
        {
            string[] workingFiles =
            {
                SaveLayout.GameRunPath,
                SaveLayout.GameRunBackupPath,
                SaveLayout.GameProfilePath,
                SaveLayout.GameProfileBackupPath
            };
            int workingCount = workingFiles.Count(relativePath => File.Exists(
                SaveLayout.CombineUnderProfile(profilePath, relativePath)));
            if (workingCount != 0)
            {
                return Result<WarmNativeSource>.Fail(
                    workingCount == workingFiles.Length
                        ? "warm_restore_source_not_exported"
                        : "warm_restore_source_partial_working_envelope",
                    "Warm restore requires the complete working envelope to be absent on the credits page.");
            }

            foreach (string relativePath in SaveLayout.NativeExportSourceFiles)
            {
                string path = SaveLayout.CombineUnderProfile(profilePath, relativePath);
                if (!File.Exists(path) || new FileInfo(path).Length <= 0)
                {
                    return Result<WarmNativeSource>.Fail(
                        "warm_restore_native_source_incomplete",
                        "The current native export is missing " + relativePath + ".");
                }
            }

            string runData = SaveLayout.CombineUnderProfile(profilePath, SaveLayout.RunDataPath);
            string runParams = SaveLayout.CombineUnderProfile(profilePath, SaveLayout.RunParamsPath);
            string liveRunIcon = SaveLayout.CombineUnderProfile(profilePath, SaveLayout.RunIconPath);
            string canonicalIcon = SnapshotPayload(canonicalTarget, SaveLayout.RunIconPath);
            Result<DecodedSlotPayload> decoded = RunSlotSynthesizer.DecodeNativeExport(
                runData,
                runParams,
                liveRunIcon);
            string sourceMode = "native_export_verified";
            if (!decoded.Success
                && string.Equals(
                    decoded.Code,
                    "native_run_icon_unknown",
                    StringComparison.Ordinal))
            {
                decoded = RunSlotSynthesizer.DecodeNativeExport(
                    runData,
                    runParams,
                    canonicalIcon);
                sourceMode = "native_export_surrogate_icon_verified";
            }

            if (!decoded.Success)
            {
                return Result<WarmNativeSource>.Fail(decoded.Code, decoded.Message);
            }

            if (string.IsNullOrWhiteSpace(decoded.Value.StateCode)
                || decoded.Value.StateCode.Length != 24
                || !string.Equals(
                    decoded.Value.StateCode.Substring(8, 8),
                    "00800000",
                    StringComparison.Ordinal))
            {
                return Result<WarmNativeSource>.Fail(
                    "warm_restore_native_source_not_hideout",
                    "The current exported R0A is not an observed non-encounter state.");
            }

            return Result<WarmNativeSource>.Ok(
                new WarmNativeSource
                {
                    StateCode = decoded.Value.StateCode,
                    Signature = SourceSignature(profilePath, SaveLayout.NativeExportSourceFiles),
                    SourceMode = sourceMode,
                    RunIconSha256 = FileTools.Sha256(liveRunIcon, true)
                },
                sourceMode,
                "The current native export is coherent for staged warm restore.");
        }

        private static string SourceSignature(
            string profilePath,
            IEnumerable<string> relativePaths)
        {
            StringBuilder builder = new StringBuilder();
            foreach (string relativePath in relativePaths.OrderBy(
                item => item,
                StringComparer.OrdinalIgnoreCase))
            {
                string path = SaveLayout.CombineUnderProfile(profilePath, relativePath);
                FileInfo info = new FileInfo(path);
                info.Refresh();
                builder.Append(relativePath);
                builder.Append('|');
                builder.Append(info.Length);
                builder.Append('|');
                builder.Append(info.LastWriteTimeUtc.Ticks);
                builder.Append('|');
                builder.Append(FileTools.Sha256(path, true));
                builder.Append('\n');
            }

            return builder.ToString();
        }

        private static void ApplyAnalysis(
            WarmRestoreJournal journal,
            WarmReloadLineageEvidence evidence)
        {
            journal.LineageClassification = evidence.Classification;
            journal.TargetByteDistance = evidence.TargetByteDistance;
            journal.OriginalByteDistance = evidence.OriginalByteDistance;
            journal.ObservedStateCode = evidence.ObservedStateCode;
            journal.TargetLineageSupported = evidence.TargetLineageSupported;
            journal.ExactTargetObserved = evidence.ExactTargetObserved;
        }

        private static bool WorkingHideoutReady(Result<RunStateDescriptor> result)
        {
            return result.Success
                && result.Value.WorkingCaptureReady
                && result.Value.WorkingRunMirrorsMatch
                && result.Value.WorkingProfileMirrorsMatch
                && WorkingRunStateSignals.IsRecognized(result.Value.WorkingRunStateSignal);
        }

        private static bool ContinuationMatches(
            WarmRestoreJournal journal,
            Result<RunStateDescriptor> result)
        {
            if (!WorkingHideoutReady(result)
                || string.IsNullOrWhiteSpace(journal.TargetStateCode)
                || string.IsNullOrWhiteSpace(journal.OriginalStateCode)
                || journal.TargetStateCode.Length != 24
                || journal.OriginalStateCode.Length != 24)
            {
                return false;
            }

            string observed = result.Value.WorkingRunStateCode;
            if (string.IsNullOrWhiteSpace(observed)
                || observed.Length != 24
                || !string.Equals(
                    observed.Substring(0, 8),
                    journal.TargetStateCode.Substring(0, 8),
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            uint targetCounter;
            uint originalCounter;
            uint observedCounter;
            if (!uint.TryParse(
                    journal.TargetStateCode.Substring(16, 8),
                    System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out targetCounter)
                || !uint.TryParse(
                    journal.OriginalStateCode.Substring(16, 8),
                    System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out originalCounter)
                || !uint.TryParse(
                    observed.Substring(16, 8),
                    System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out observedCounter))
            {
                return false;
            }

            return observedCounter >= targetCounter
                && (originalCounter <= targetCounter || observedCounter < originalCounter);
        }

        private static string ClassifyTargetEnvelope(
            string profilePath,
            SnapshotManifest target)
        {
            string[] stableFiles =
            {
                SaveLayout.MarkerPath,
                SaveLayout.RunDataPath,
                SaveLayout.RunParamsPath,
                SaveLayout.RunIconPath
            };
            if (!stableFiles.All(relativePath => TargetFileMatches(
                profilePath,
                target.Files.FirstOrDefault(entry => string.Equals(
                    entry.RelativePath,
                    relativePath,
                    StringComparison.OrdinalIgnoreCase)))))
            {
                return "changed";
            }

            string[] workingFiles =
            {
                SaveLayout.GameRunPath,
                SaveLayout.GameRunBackupPath,
                SaveLayout.GameProfilePath,
                SaveLayout.GameProfileBackupPath
            };
            return workingFiles.All(relativePath => TargetFileMatches(
                profilePath,
                target.Files.FirstOrDefault(entry => string.Equals(
                    entry.RelativePath,
                    relativePath,
                    StringComparison.OrdinalIgnoreCase))))
                ? "exact_working_envelope"
                : "exact_native_slot";
        }

        private static bool AllTargetFilesMatch(
            string profilePath,
            SnapshotManifest target)
        {
            if (target == null || target.Files == null)
            {
                return false;
            }

            return SaveLayout.RequiredFiles.All(relativePath => TargetFileMatches(
                profilePath,
                target.Files.FirstOrDefault(entry => string.Equals(
                    entry.RelativePath,
                    relativePath,
                    StringComparison.OrdinalIgnoreCase))));
        }

        private static bool TargetFileMatches(
            string profilePath,
            SnapshotFileEntry entry)
        {
            if (entry == null)
            {
                return false;
            }

            try
            {
                string path = SaveLayout.CombineUnderProfile(profilePath, entry.RelativePath);
                if (!File.Exists(path))
                {
                    return false;
                }

                FileInfo info = new FileInfo(path);
                info.Refresh();
                return info.Length == entry.Length
                    && string.Equals(
                        FileTools.Sha256(path, true),
                        entry.Sha256,
                        StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static string LiveSignature(string profilePath)
        {
            StringBuilder builder = new StringBuilder();
            foreach (string relativePath in SaveLayout.RequiredFiles.OrderBy(
                item => item,
                StringComparer.OrdinalIgnoreCase))
            {
                string fullPath = SaveLayout.CombineUnderProfile(profilePath, relativePath);
                builder.Append(relativePath);
                builder.Append('|');
                if (!File.Exists(fullPath))
                {
                    builder.Append("missing");
                }
                else
                {
                    FileInfo info = new FileInfo(fullPath);
                    info.Refresh();
                    builder.Append(info.Length);
                    builder.Append('|');
                    builder.Append(info.LastWriteTimeUtc.Ticks);
                    builder.Append('|');
                    builder.Append(FileTools.Sha256(fullPath, true));
                }

                builder.Append('\n');
            }

            return builder.ToString();
        }

        private static string SnapshotPayload(SnapshotRecord record, string relativePath)
        {
            return Path.Combine(
                record.DirectoryPath,
                "payload",
                relativePath.Replace('/', Path.DirectorySeparatorChar));
        }

        private Result<WarmRestoreJournal> ReadJournal(string profilePath)
        {
            if (!File.Exists(JournalPath))
            {
                return Result<WarmRestoreJournal>.Ok(
                    null,
                    "warm_restore_none",
                    "No active warm restore.");
            }

            try
            {
                WarmRestoreJournal journal = FileTools.ReadJson<WarmRestoreJournal>(
                    JournalPath);
                if (journal == null
                    || journal.SchemaVersion != 2
                    || string.IsNullOrWhiteSpace(journal.OperationId)
                    || string.IsNullOrWhiteSpace(journal.TargetSnapshotId)
                    || string.IsNullOrWhiteSpace(journal.UndoSnapshotId)
                    || !SameProfile(journal.ProfilePath, profilePath))
                {
                    return Result<WarmRestoreJournal>.Fail(
                        "warm_restore_journal_invalid",
                        "The active warm restore journal is invalid or belongs to another profile.");
                }

                if (journal.GameProcessIds == null)
                {
                    journal.GameProcessIds = new List<int>();
                }

                return Result<WarmRestoreJournal>.Ok(
                    journal,
                    "warm_restore_active",
                    "An active warm restore was found.");
            }
            catch (Exception exception)
            {
                return Result<WarmRestoreJournal>.Fail(
                    "warm_restore_journal_unreadable",
                    exception.Message);
            }
        }

        private void PersistJournal(WarmRestoreJournal journal)
        {
            journal.UpdatedUtc = _clock.UtcNow.ToUniversalTime().ToString("O");
            FileTools.WriteJsonAtomic(JournalPath, journal);
            if (Hooks != null && Hooks.AfterJournalPersisted != null)
            {
                Hooks.AfterJournalPersisted(journal.State);
            }
        }

        private string ArchiveJournal(WarmRestoreJournal journal)
        {
            journal.UpdatedUtc = _clock.UtcNow.ToUniversalTime().ToString("O");
            string archiveRoot = Path.Combine(_store.StorageRoot, "warm-reload-experiments");
            Directory.CreateDirectory(archiveRoot);
            string archive = Path.Combine(archiveRoot, journal.OperationId + ".json");
            FileTools.WriteJsonAtomic(archive, journal);
            FileTools.TryDeleteFile(JournalPath);
            return archive;
        }

        private static bool SameProfile(string first, string second)
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

        private static bool SameProcessSet(
            IEnumerable<int> first,
            IEnumerable<int> second)
        {
            int[] left = (first ?? Enumerable.Empty<int>()).OrderBy(value => value).ToArray();
            int[] right = (second ?? Enumerable.Empty<int>()).OrderBy(value => value).ToArray();
            return left.SequenceEqual(right);
        }

        private sealed class OperationFailure : Exception
        {
            public OperationFailure(string code, string message) : base(message)
            {
                Code = code;
            }

            public string Code { get; private set; }
        }

        private sealed class WarmNativeSource
        {
            public string StateCode { get; set; }
            public string Signature { get; set; }
            public string SourceMode { get; set; }
            public string RunIconSha256 { get; set; }
        }

        private sealed class LiveRawRun
        {
            public byte[] RawBytes { get; set; }
            public string StateCode { get; set; }
            public string SourceKind { get; set; }
        }

        private sealed class WarmRestoreStoppedProbe : IGameProcessProbe
        {
            public bool IsGameRunning()
            {
                return false;
            }

            public string FindGameExecutablePath()
            {
                return null;
            }
        }
    }
}
