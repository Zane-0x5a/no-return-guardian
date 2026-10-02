using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using NoReturnGuardian.Core;

internal static class WarmRestoreExperiment
{
    private const string ExpectedGameSha256 =
        "CAC7F729EAE6FAB743616B8216ACF20E9F05D8E51DA0E3B7A4892147569DD357";
    private const string ActiveJournalName = "warm-reload-experiment.json";

    [DllImport("ntdll.dll")]
    private static extern int NtSuspendProcess(IntPtr processHandle);

    [DllImport("ntdll.dll")]
    private static extern int NtResumeProcess(IntPtr processHandle);

    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            return Usage();
        }

        try
        {
            switch (args[0].ToLowerInvariant())
            {
                case "stage":
                    return args.Length == 5
                        ? Stage(args[1], args[2], args[3], args[4])
                        : Usage();
                case "preflight":
                    return args.Length == 4
                        ? Preflight(args[1], args[2], args[3])
                        : Usage();
                case "checkpoint":
                    return args.Length == 4
                        ? Checkpoint(args[1], args[2], args[3])
                        : Usage();
                case "verify":
                    return args.Length == 4
                        ? Verify(args[1], args[2], args[3])
                        : Usage();
                case "accept":
                    return args.Length == 4
                        ? Accept(args[1], args[2], args[3])
                        : Usage();
                case "recover":
                    return args.Length == 3
                        ? Recover(args[1], args[2])
                        : Usage();
                case "status":
                    return args.Length == 2 ? Status(args[1]) : Usage();
                default:
                    return Usage();
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("EXPERIMENT_FAILED " + exception.Message);
            return 90;
        }
    }

    private static int Preflight(
        string storageRoot,
        string profilePath,
        string snapshotId)
    {
        if (new GameProcessProbe().IsGameRunning())
        {
            Console.Error.WriteLine(
                "GAME_RUNNING preflight is read-only and must be run before launching the game");
            return 5;
        }

        SnapshotStore store = new SnapshotStore(storageRoot, new SystemClock());
        Result<SnapshotRecord> target = store.VerifySnapshot(snapshotId, true);
        if (!target.Success || !SnapshotPolicy.IsConfirmedPreparation(target.Value))
        {
            Console.Error.WriteLine(
                "TARGET_REJECTED code={0} message={1}",
                target.Code,
                target.Message);
            return 6;
        }

        Result<RunStateDescriptor> first = RunStateProbe.Inspect(profilePath, true);
        Thread.Sleep(2500);
        Result<RunStateDescriptor> second = RunStateProbe.Inspect(profilePath, true);
        if (!NativeSourceReady(first)
            || !NativeSourceReady(second)
            || !first.Value.SameCaptureState(second.Value))
        {
            Console.Error.WriteLine(
                "CURRENT_STATE_NOT_READY the current native export is not stable and coherent");
            return 7;
        }

        Result<LiveRawRun> current = ReadLiveRawRun(profilePath, target.Value);
        if (!current.Success)
        {
            Console.Error.WriteLine(
                "CURRENT_STATE_INVALID code={0} message={1}",
                current.Code,
                current.Message);
            return 8;
        }

        byte[] targetRun = File.ReadAllBytes(
            SnapshotPayload(target.Value, SaveLayout.GameRunPath));
        Result<WarmReloadLineageEvidence> comparison = WarmReloadLineageAnalyzer.Analyze(
            target.Value.Manifest.RunStateCode,
            current.Value.StateCode,
            current.Value.StateCode,
            targetRun,
            current.Value.RawBytes,
            current.Value.RawBytes);
        if (!comparison.Success
            || !string.Equals(
                comparison.Value.Classification,
                WarmReloadClassifications.CachedOriginalReasserted,
                StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                "PREFLIGHT_INCONCLUSIVE target and current generation could not be compared");
            return 9;
        }

        Console.WriteLine(
            "PREFLIGHT_READY target={0} target_state={1} current_state={2} byte_distance={3} source={4}",
            snapshotId,
            target.Value.Manifest.RunStateCode,
            current.Value.StateCode,
            comparison.Value.TargetByteDistance,
            current.Value.SourceKind);
        return 0;
    }

    private static int Stage(
        string storageRoot,
        string profilePath,
        string snapshotId,
        string confirmation)
    {
        if (!string.Equals(
            confirmation,
            "--confirmed-t2r-credits",
            StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                "CONFIRMATION_REQUIRED open the T2R credits page before staging");
            return 10;
        }

        string journalPath = ActiveJournalPath(storageRoot);
        if (File.Exists(journalPath))
        {
            Console.Error.WriteLine(
                "EXPERIMENT_ACTIVE recover or finish the existing warm reload experiment first");
            return 11;
        }

        List<Process> processes;
        string gamePath;
        Result<bool> processCheck = FindValidatedGameProcesses(out processes, out gamePath);
        if (!processCheck.Success)
        {
            Console.Error.WriteLine(
                "GAME_GATE_FAILED code={0} message={1}",
                processCheck.Code,
                processCheck.Message);
            return 12;
        }

        try
        {
            SnapshotStore store = new SnapshotStore(storageRoot, new SystemClock());
            if (store.HasPendingRestore())
            {
                Console.Error.WriteLine(
                    "RESTORE_PENDING close the existing restore transaction before experimenting");
                return 13;
            }

            Result<SnapshotRecord> targetResult = store.VerifySnapshot(snapshotId, true);
            if (!targetResult.Success
                || !SnapshotPolicy.IsConfirmedPreparation(targetResult.Value))
            {
                Console.Error.WriteLine(
                    "TARGET_REJECTED code={0} message={1}",
                    targetResult.Code,
                    targetResult.Message);
                return 14;
            }

            Result<ExperimentalNativeSource> first = InspectExperimentalNativeSource(
                profilePath,
                targetResult.Value);
            if (!first.Success)
            {
                Console.Error.WriteLine(
                    "CURRENT_STATE_NOT_EXPORTED code={0} status={1}",
                    first.Code,
                    first.Message);
                return 15;
            }

            Thread.Sleep(2500);
            Result<ExperimentalNativeSource> second = InspectExperimentalNativeSource(
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
                    StringComparison.Ordinal))
            {
                Console.Error.WriteLine(
                    "CURRENT_STATE_UNSTABLE the exported pre-experiment state changed");
                return 16;
            }

            string originalStateCode = second.Value.StateCode;
            string targetStateCode = targetResult.Value.Manifest.RunStateCode;
            if (string.Equals(
                originalStateCode,
                targetStateCode,
                StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine(
                    "TARGET_NOT_DISTINCT choose an older snapshot so cache reload can be proven");
                return 17;
            }

            string stableSignature = second.Value.Signature;
            List<Process> suspended = new List<Process>();
            Result<RestoreOutcome> restore = null;
            WarmReloadExperimentJournal journal = null;
            Exception stageError = null;
            Exception compensationError = null;
            bool resumeFailed = false;
            try
            {
                foreach (Process process in processes)
                {
                    process.Refresh();
                    if (process.HasExited)
                    {
                        throw new InvalidOperationException(
                            "The game exited before process suspension.");
                    }

                    int status = NtSuspendProcess(process.Handle);
                    if (status != 0)
                    {
                        throw new InvalidOperationException(
                            "NtSuspendProcess failed for PID " + process.Id
                            + " with status " + status + ".");
                    }

                    suspended.Add(process);
                    Console.WriteLine(
                        "SUSPENDED pid={0} name={1}",
                        process.Id,
                        process.ProcessName);
                }

                Result<ExperimentalNativeSource> suspendedSource =
                    InspectExperimentalNativeSource(profilePath, targetResult.Value);
                if (!suspendedSource.Success
                    || !string.Equals(
                        stableSignature,
                        suspendedSource.Value.Signature,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Save files changed between the stability gate and suspension.");
                }

                restore = store.Restore(
                    snapshotId,
                    profilePath,
                    new ExperimentStoppedProbe(),
                    null);
                if (!restore.Success)
                {
                    throw new InvalidOperationException(
                        "Target staging failed with code " + restore.Code
                        + ": " + restore.Message);
                }

                if (!AllTargetFilesMatch(profilePath, targetResult.Value.Manifest))
                {
                    throw new InvalidOperationException(
                        "The suspended disk envelope does not match the target.");
                }

                journal = new WarmReloadExperimentJournal
                {
                    SchemaVersion = 1,
                    ExperimentId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff")
                        + "-credits-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                    StartedUtc = DateTime.UtcNow.ToString("O"),
                    UpdatedUtc = DateTime.UtcNow.ToString("O"),
                    State = "target_staged_in_t2r_credits",
                    ProfilePath = Path.GetFullPath(profilePath),
                    GameExecutablePath = gamePath,
                    GameExecutableSha256 = ExpectedGameSha256,
                    TargetSnapshotId = snapshotId,
                    TargetStateCode = targetStateCode,
                    OriginalStateCode = originalStateCode,
                    UndoSnapshotId = restore.Value.UndoSnapshotId,
                    StageEnvelopeSignature = Sha256Text(LiveSignature(profilePath)),
                    OriginalSourceMode = second.Value.SourceMode,
                    OriginalRunIconSha256 = second.Value.RunIconSha256
                };
                FileTools.WriteJsonAtomic(journalPath, journal);
            }
            catch (Exception exception)
            {
                stageError = exception;
                if (restore != null && restore.Success)
                {
                    try
                    {
                        Result<RestoreOutcome> compensation = store.Undo(
                            restore.Value.UndoSnapshotId,
                            profilePath,
                            new ExperimentStoppedProbe());
                        if (!compensation.Success)
                        {
                            throw new InvalidOperationException(
                                compensation.Code + ": " + compensation.Message);
                        }
                    }
                    catch (Exception compensationException)
                    {
                        compensationError = compensationException;
                    }
                }
            }
            finally
            {
                for (int index = suspended.Count - 1; index >= 0; index--)
                {
                    Process process = suspended[index];
                    try
                    {
                        if (!process.HasExited)
                        {
                            int status = NtResumeProcess(process.Handle);
                            if (status != 0)
                            {
                                resumeFailed = true;
                            }
                            Console.WriteLine(
                                "RESUMED pid={0} status={1}",
                                process.Id,
                                status);
                        }
                    }
                    catch
                    {
                        resumeFailed = true;
                    }
                }
            }

            if (resumeFailed)
            {
                Console.Error.WriteLine(
                    "RESUME_INCOMPLETE close the game before doing anything else");
                return 18;
            }

            if (stageError != null)
            {
                Console.Error.WriteLine("STAGE_FAILED " + stageError.Message);
                if (compensationError != null)
                {
                    Console.Error.WriteLine(
                        "COMPENSATION_FAILED exit the game; recovery is required: "
                        + compensationError.Message);
                    return 20;
                }

                Console.Error.WriteLine("STAGE_COMPENSATED original disk state restored");
                return 19;
            }

            Thread.Sleep(2000);
            if (!AllTargetFilesMatch(profilePath, targetResult.Value.Manifest))
            {
                journal.State = "target_changed_before_credits_return";
                journal.UpdatedUtc = DateTime.UtcNow.ToString("O");
                FileTools.WriteJsonAtomic(journalPath, journal);
                Console.Error.WriteLine(
                    "TARGET_CHANGED the credits page changed the target before user return");
                return 21;
            }

            Console.WriteLine(
                "TARGET_STAGED experiment={0} target={1} state={2} undo={3}",
                journal.ExperimentId,
                journal.TargetSnapshotId,
                journal.TargetStateCode,
                journal.UndoSnapshotId);
            Console.WriteLine("NEXT exit the T2R credits page and return to the main menu");
            return 0;
        }
        finally
        {
            DisposeProcesses(processes);
        }
    }

    private static int Checkpoint(
        string storageRoot,
        string profilePath,
        string confirmation)
    {
        if (!string.Equals(
            confirmation,
            "--confirmed-main-menu-after-t2r",
            StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                "CONFIRMATION_REQUIRED return from T2R credits to the main menu first");
            return 30;
        }

        WarmReloadExperimentJournal journal = ReadActiveJournal(storageRoot, profilePath);
        List<Process> processes;
        string gamePath;
        Result<bool> processCheck = FindValidatedGameProcesses(out processes, out gamePath);
        if (!processCheck.Success)
        {
            Console.Error.WriteLine(processCheck.Code + " " + processCheck.Message);
            return 31;
        }

        try
        {
            SnapshotStore store = new SnapshotStore(storageRoot, new SystemClock());
            Result<SnapshotRecord> target = store.VerifySnapshot(
                journal.TargetSnapshotId,
                true);
            if (!target.Success)
            {
                Console.Error.WriteLine("TARGET_INVALID " + target.Message);
                return 32;
            }

            Result<RunStateDescriptor> firstProbe = RunStateProbe.Inspect(profilePath, true);
            string first = LiveSignature(profilePath);
            Thread.Sleep(2500);
            Result<RunStateDescriptor> secondProbe = RunStateProbe.Inspect(profilePath, true);
            string second = LiveSignature(profilePath);
            if (!firstProbe.Success
                || !secondProbe.Success
                || !firstProbe.Value.SameCaptureState(secondProbe.Value)
                || !string.Equals(first, second, StringComparison.Ordinal))
            {
                Console.Error.WriteLine(
                    "MAIN_MENU_UNSTABLE wait for the main menu save activity to settle");
                return 33;
            }

            Result<LiveRawRun> liveRun = ReadLiveRawRun(profilePath, target.Value);
            if (!liveRun.Success
                || !string.Equals(second, LiveSignature(profilePath), StringComparison.Ordinal))
            {
                Console.Error.WriteLine(
                    "MAIN_MENU_ENVELOPE_INVALID "
                    + (liveRun.Success ? "save files changed during analysis" : liveRun.Message));
                return 34;
            }

            SnapshotFileEntry targetMarker = target.Value.Manifest.Files.First(entry =>
                string.Equals(
                    entry.RelativePath,
                    SaveLayout.MarkerPath,
                    StringComparison.OrdinalIgnoreCase));
            if (!TargetFileMatches(profilePath, targetMarker))
            {
                Console.Error.WriteLine(
                    "MAIN_MENU_MARKER_CHANGED nr.bin no longer matches the staged target");
                return 35;
            }

            Result<WarmReloadLineageEvidence> analysis = AnalyzeObserved(
                storageRoot,
                journal,
                liveRun.Value.StateCode,
                liveRun.Value.RawBytes);
            if (!analysis.Success || !analysis.Value.TargetLineageSupported)
            {
                journal.State = "main_menu_target_changed";
                journal.MainMenuLineageClassification = analysis.Success
                    ? analysis.Value.Classification
                    : analysis.Code;
                journal.UpdatedUtc = DateTime.UtcNow.ToString("O");
                WriteActiveJournal(storageRoot, journal);
                Console.Error.WriteLine(
                    "MAIN_MENU_TARGET_CHANGED classification="
                    + journal.MainMenuLineageClassification);
                return 36;
            }

            string exactMode = ClassifyTargetEnvelope(profilePath, target.Value.Manifest);
            string mode = string.Equals(exactMode, "changed", StringComparison.Ordinal)
                ? liveRun.Value.SourceKind + "_target_lineage"
                : exactMode;
            journal.MainMenuEnvelopeMode = mode;
            journal.MainMenuEnvelopeSignature = Sha256Text(second);
            journal.MainMenuLineageClassification = analysis.Value.Classification;
            journal.State = "main_menu_confirmed_after_t2r";
            journal.UpdatedUtc = DateTime.UtcNow.ToString("O");
            WriteActiveJournal(storageRoot, journal);

            Console.WriteLine(
                "MAIN_MENU_CHECKPOINT mode={0} lineage={1} target={2}",
                mode,
                analysis.Value.Classification,
                journal.TargetStateCode);
            Console.WriteLine(
                "NEXT enter No Return and stop in the hideout without starting an encounter");
            return 0;
        }
        finally
        {
            DisposeProcesses(processes);
        }
    }

    private static int Verify(
        string storageRoot,
        string profilePath,
        string confirmation)
    {
        if (!string.Equals(
            confirmation,
            "--confirmed-no-return-hideout",
            StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                "CONFIRMATION_REQUIRED enter No Return and stop in the hideout first");
            return 40;
        }

        WarmReloadExperimentJournal journal = ReadActiveJournal(storageRoot, profilePath);
        if (!string.Equals(
            journal.State,
            "main_menu_confirmed_after_t2r",
            StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                "CHECKPOINT_REQUIRED record the post-credits main menu checkpoint first");
            return 41;
        }

        List<Process> processes;
        string gamePath;
        Result<bool> processCheck = FindValidatedGameProcesses(out processes, out gamePath);
        if (!processCheck.Success)
        {
            Console.Error.WriteLine(processCheck.Code + " " + processCheck.Message);
            return 42;
        }

        try
        {
            Result<WarmReloadLineageEvidence> analysis = AnalyzeCurrent(
                storageRoot,
                profilePath,
                journal);
            if (!analysis.Success)
            {
                Console.Error.WriteLine(
                    "VERIFY_INCONCLUSIVE code={0} message={1}",
                    analysis.Code,
                    analysis.Message);
                return 43;
            }

            ApplyAnalysis(journal, analysis.Value);
            journal.State = analysis.Value.TargetLineageSupported
                ? "target_lineage_observed_pending_player"
                : "warm_reload_rejected";
            journal.UpdatedUtc = DateTime.UtcNow.ToString("O");
            WriteActiveJournal(storageRoot, journal);
            Console.WriteLine(
                "LINEAGE classification={0} target_distance={1} original_distance={2} observed={3}",
                analysis.Value.Classification,
                analysis.Value.TargetByteDistance,
                analysis.Value.OriginalByteDistance,
                analysis.Value.ObservedStateCode);
            if (!analysis.Value.TargetLineageSupported)
            {
                Console.Error.WriteLine(
                    "WARM_RELOAD_REJECTED the target generation was not established");
                return 44;
            }

            Console.WriteLine(
                "NEXT inspect currency, inventory, upgrades, and the trading station before acceptance");
            return 0;
        }
        finally
        {
            DisposeProcesses(processes);
        }
    }

    private static int Accept(
        string storageRoot,
        string profilePath,
        string confirmation)
    {
        if (!string.Equals(
            confirmation,
            "--confirmed-trading-station",
            StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                "CONFIRMATION_REQUIRED verify the hideout and trading station first");
            return 50;
        }

        WarmReloadExperimentJournal journal = ReadActiveJournal(storageRoot, profilePath);
        if (!string.Equals(
            journal.State,
            "target_lineage_observed_pending_player",
            StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                "LINEAGE_REQUIRED target lineage must be verified before player acceptance");
            return 51;
        }

        List<Process> processes;
        string gamePath;
        Result<bool> processCheck = FindValidatedGameProcesses(out processes, out gamePath);
        if (!processCheck.Success)
        {
            Console.Error.WriteLine(processCheck.Code + " " + processCheck.Message);
            return 52;
        }

        try
        {
            Result<RunStateDescriptor> first = RunStateProbe.Inspect(profilePath, true);
            Thread.Sleep(2500);
            Result<RunStateDescriptor> second = RunStateProbe.Inspect(profilePath, true);
            if (!ContinuationMatches(journal, first)
                || !ContinuationMatches(journal, second))
            {
                Console.Error.WriteLine(
                    "LINEAGE_CHANGED the current hideout no longer supports the target lineage");
                return 53;
            }

            journal.State = "accepted_in_game_and_trading_station";
            journal.AcceptanceBasis = "verified_reentry_lineage_plus_target_counter_continuation";
            journal.AcceptanceObservedStateCode = second.Value.WorkingRunStateCode;
            journal.PlayerAcceptedUtc = DateTime.UtcNow.ToString("O");
            journal.UpdatedUtc = journal.PlayerAcceptedUtc;
            string archive = ArchiveJournal(storageRoot, journal);
            Console.WriteLine(
                "WARM_RELOAD_ACCEPTED experiment={0} evidence={1}",
                journal.ExperimentId,
                archive);
            return 0;
        }
        finally
        {
            DisposeProcesses(processes);
        }
    }

    private static bool ContinuationMatches(
        WarmReloadExperimentJournal journal,
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

    private static int Recover(string storageRoot, string profilePath)
    {
        WarmReloadExperimentJournal journal = ReadActiveJournal(storageRoot, profilePath);
        GameProcessProbe processProbe = new GameProcessProbe();
        if (processProbe.IsGameRunning())
        {
            Console.Error.WriteLine(
                "GAME_RUNNING exit the game completely before recovering the original state");
            return 60;
        }

        SnapshotStore store = new SnapshotStore(storageRoot, new SystemClock());
        Result<RestoreOutcome> recovery = store.Undo(
            journal.UndoSnapshotId,
            profilePath,
            processProbe);
        if (!recovery.Success)
        {
            Console.Error.WriteLine(
                "RECOVERY_FAILED code={0} message={1}",
                recovery.Code,
                recovery.Message);
            return 61;
        }

        journal.State = "original_state_recovered_after_full_exit";
        journal.UpdatedUtc = DateTime.UtcNow.ToString("O");
        string archive = ArchiveJournal(storageRoot, journal);
        Console.WriteLine(
            "ORIGINAL_RECOVERED snapshot={0} evidence={1}",
            journal.UndoSnapshotId,
            archive);
        return 0;
    }

    private static int Status(string storageRoot)
    {
        string path = ActiveJournalPath(storageRoot);
        if (!File.Exists(path))
        {
            Console.WriteLine("NO_ACTIVE_EXPERIMENT");
            return 0;
        }

        WarmReloadExperimentJournal journal =
            FileTools.ReadJson<WarmReloadExperimentJournal>(path);
        Console.WriteLine(
            "ACTIVE experiment={0} state={1} target={2} target_state={3} original_state={4} undo={5}",
            journal.ExperimentId,
            journal.State,
            journal.TargetSnapshotId,
            journal.TargetStateCode,
            journal.OriginalStateCode,
            journal.UndoSnapshotId);
        if (!string.IsNullOrWhiteSpace(journal.LineageClassification))
        {
            Console.WriteLine(
                "LINEAGE classification={0} target_distance={1} original_distance={2} observed={3}",
                journal.LineageClassification,
                journal.TargetByteDistance,
                journal.OriginalByteDistance,
                journal.ObservedStateCode);
        }
        return 0;
    }

    private static Result<WarmReloadLineageEvidence> AnalyzeCurrent(
        string storageRoot,
        string profilePath,
        WarmReloadExperimentJournal journal)
    {
        Result<RunStateDescriptor> first = RunStateProbe.Inspect(profilePath, true);
        if (!WorkingHideoutReady(first))
        {
            return Result<WarmReloadLineageEvidence>.Fail(
                "warm_reload_hideout_not_ready",
                first.Success ? first.Value.ManualCaptureStatus : first.Message);
        }

        Thread.Sleep(2500);
        Result<RunStateDescriptor> second = RunStateProbe.Inspect(profilePath, true);
        if (!WorkingHideoutReady(second)
            || !first.Value.SameCaptureState(second.Value))
        {
            return Result<WarmReloadLineageEvidence>.Fail(
                "warm_reload_hideout_unstable",
                "The observed hideout state changed during the evidence gate.");
        }

        Result<LiveRawRun> observed = ReadLiveRawRun(profilePath);
        if (!observed.Success)
        {
            return Result<WarmReloadLineageEvidence>.Fail(
                observed.Code,
                observed.Message);
        }

        return AnalyzeObserved(
            storageRoot,
            journal,
            second.Value.WorkingRunStateCode,
            observed.Value.RawBytes);
    }

    private static Result<WarmReloadLineageEvidence> AnalyzeObserved(
        string storageRoot,
        WarmReloadExperimentJournal journal,
        string observedStateCode,
        byte[] observedRun)
    {
        SnapshotStore store = new SnapshotStore(storageRoot, new SystemClock());
        Result<SnapshotRecord> target = store.VerifySnapshot(journal.TargetSnapshotId, true);
        Result<SnapshotRecord> undo = store.VerifySnapshot(journal.UndoSnapshotId, false);
        if (!target.Success || !undo.Success)
        {
            return Result<WarmReloadLineageEvidence>.Fail(
                "warm_reload_evidence_snapshot_invalid",
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
            string run = SaveLayout.CombineUnderProfile(profilePath, SaveLayout.GameRunPath);
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
                "original_run_missing",
                "The pre-experiment snapshot has no reconstructable R0A payload.");
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
            ? Result<byte[]>.Ok(decoded.Value.RawBytes, "native_run_decoded", "Native R0A decoded.")
            : Result<byte[]>.Fail(decoded.Code, decoded.Message);
    }

    private static Result<ExperimentalNativeSource> InspectExperimentalNativeSource(
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
            return Result<ExperimentalNativeSource>.Fail(
                workingCount == workingFiles.Length
                    ? "experimental_source_not_exported"
                    : "experimental_source_partial_working_envelope",
                "The credits experiment requires all four working files to be absent.");
        }

        foreach (string relativePath in SaveLayout.NativeExportSourceFiles)
        {
            string path = SaveLayout.CombineUnderProfile(profilePath, relativePath);
            if (!File.Exists(path) || new FileInfo(path).Length <= 0)
            {
                return Result<ExperimentalNativeSource>.Fail(
                    "experimental_native_source_incomplete",
                    "The current native export is missing " + relativePath + ".");
            }
        }

        string runData = SaveLayout.CombineUnderProfile(
            profilePath,
            SaveLayout.RunDataPath);
        string runParams = SaveLayout.CombineUnderProfile(
            profilePath,
            SaveLayout.RunParamsPath);
        string liveRunIcon = SaveLayout.CombineUnderProfile(
            profilePath,
            SaveLayout.RunIconPath);
        string canonicalIcon = SnapshotPayload(
            canonicalTarget,
            SaveLayout.RunIconPath);
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
            return Result<ExperimentalNativeSource>.Fail(decoded.Code, decoded.Message);
        }

        if (string.IsNullOrWhiteSpace(decoded.Value.StateCode)
            || decoded.Value.StateCode.Length != 24
            || !string.Equals(
                decoded.Value.StateCode.Substring(8, 8),
                "00800000",
                StringComparison.Ordinal))
        {
            return Result<ExperimentalNativeSource>.Fail(
                "experimental_native_source_not_hideout",
                "The current exported R0A is not an observed non-encounter state.");
        }

        return Result<ExperimentalNativeSource>.Ok(
            new ExperimentalNativeSource
            {
                StateCode = decoded.Value.StateCode,
                Signature = ObservedSourceSignature(
                    profilePath,
                    SaveLayout.NativeExportSourceFiles),
                SourceMode = sourceMode,
                RunIconSha256 = FileTools.Sha256(liveRunIcon, true)
            },
            sourceMode,
            "The current native export is stable enough for an isolated warm experiment.");
    }

    private static string ObservedSourceSignature(
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
        WarmReloadExperimentJournal journal,
        WarmReloadLineageEvidence evidence)
    {
        journal.LineageClassification = evidence.Classification;
        journal.TargetByteDistance = evidence.TargetByteDistance;
        journal.OriginalByteDistance = evidence.OriginalByteDistance;
        journal.ObservedStateCode = evidence.ObservedStateCode;
        journal.TargetLineageSupported = evidence.TargetLineageSupported;
        journal.ExactTargetObserved = evidence.ExactTargetObserved;
    }

    private static bool NativeSourceReady(Result<RunStateDescriptor> result)
    {
        return result.Success
            && result.Value.NativeExportCaptureReady
            && string.Equals(
                result.Value.ManualCaptureBasis,
                SnapshotCaptureBases.NativeExportedState,
                StringComparison.Ordinal);
    }

    private static bool WorkingHideoutReady(Result<RunStateDescriptor> result)
    {
        return result.Success
            && result.Value.WorkingCaptureReady
            && result.Value.WorkingRunMirrorsMatch
            && result.Value.WorkingProfileMirrorsMatch
            && string.Equals(
                result.Value.WorkingRunStateSignal,
                WorkingRunStateSignals.ObservedWord0,
                StringComparison.Ordinal);
    }

    private static Result<bool> FindValidatedGameProcesses(
        out List<Process> processes,
        out string gamePath)
    {
        processes = new List<Process>();
        processes.AddRange(Process.GetProcessesByName("tlou-ii"));
        processes.AddRange(Process.GetProcessesByName("tlou-ii-l"));
        processes = processes.OrderBy(item => item.Id).ToList();
        gamePath = null;
        if (processes.Count == 0)
        {
            return Result<bool>.Fail("game_not_running", "The game process is not running.");
        }

        Process primary = processes.FirstOrDefault(item => string.Equals(
            item.ProcessName,
            "tlou-ii",
            StringComparison.OrdinalIgnoreCase));
        if (primary == null)
        {
            DisposeProcesses(processes);
            processes = new List<Process>();
            return Result<bool>.Fail(
                "primary_game_process_missing",
                "The retail tlou-ii process was not found.");
        }

        try
        {
            gamePath = primary.MainModule.FileName;
            string hash = FileTools.Sha256(gamePath, true);
            if (!string.Equals(hash, ExpectedGameSha256, StringComparison.OrdinalIgnoreCase))
            {
                DisposeProcesses(processes);
                processes = new List<Process>();
                return Result<bool>.Fail(
                    "game_build_unrecognized",
                    "The running game executable does not match the statically verified build: "
                    + hash);
            }
        }
        catch (Exception exception)
        {
            DisposeProcesses(processes);
            processes = new List<Process>();
            return Result<bool>.Fail("game_image_unreadable", exception.Message);
        }

        return Result<bool>.Ok(true, "game_build_verified", "Game build verified.");
    }

    private static void DisposeProcesses(IEnumerable<Process> processes)
    {
        foreach (Process process in processes ?? Enumerable.Empty<Process>())
        {
            process.Dispose();
        }
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
        if (!stableFiles.All(path => TargetFileMatches(
            profilePath,
            target.Files.First(entry => string.Equals(
                entry.RelativePath,
                path,
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
        if (workingFiles.All(path => TargetFileMatches(
            profilePath,
            target.Files.First(entry => string.Equals(
                entry.RelativePath,
                path,
                StringComparison.OrdinalIgnoreCase)))))
        {
            return "full_envelope_stable";
        }

        return workingFiles.All(path => !File.Exists(
            SaveLayout.CombineUnderProfile(profilePath, path)))
            ? "slot_normalized"
            : "changed";
    }

    private static bool AllTargetFilesMatch(
        string profilePath,
        SnapshotManifest target)
    {
        return target.Files.All(entry => TargetFileMatches(profilePath, entry));
    }

    private static bool TargetFileMatches(
        string profilePath,
        SnapshotFileEntry entry)
    {
        string fullPath = SaveLayout.CombineUnderProfile(profilePath, entry.RelativePath);
        if (!File.Exists(fullPath))
        {
            return false;
        }

        FileInfo info = new FileInfo(fullPath);
        info.Refresh();
        return info.Length == entry.Length
            && string.Equals(
                FileTools.Sha256(fullPath, true),
                entry.Sha256,
                StringComparison.OrdinalIgnoreCase);
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

    private static string Sha256Text(string value)
    {
        using (System.Security.Cryptography.SHA256 algorithm =
            System.Security.Cryptography.SHA256.Create())
        {
            byte[] digest = algorithm.ComputeHash(Encoding.UTF8.GetBytes(value));
            return string.Concat(digest.Select(item => item.ToString("X2")));
        }
    }

    private static WarmReloadExperimentJournal ReadActiveJournal(
        string storageRoot,
        string profilePath)
    {
        string path = ActiveJournalPath(storageRoot);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException("No warm reload experiment is active.");
        }

        WarmReloadExperimentJournal journal =
            FileTools.ReadJson<WarmReloadExperimentJournal>(path);
        if (journal == null
            || journal.SchemaVersion != 1
            || !SameProfile(journal.ProfilePath, profilePath))
        {
            throw new InvalidDataException(
                "The active warm reload experiment journal is invalid or belongs to another profile.");
        }

        return journal;
    }

    private static void WriteActiveJournal(
        string storageRoot,
        WarmReloadExperimentJournal journal)
    {
        FileTools.WriteJsonAtomic(ActiveJournalPath(storageRoot), journal);
    }

    private static string ArchiveJournal(
        string storageRoot,
        WarmReloadExperimentJournal journal)
    {
        string archiveRoot = Path.Combine(storageRoot, "warm-reload-experiments");
        Directory.CreateDirectory(archiveRoot);
        string archive = Path.Combine(archiveRoot, journal.ExperimentId + ".json");
        FileTools.WriteJsonAtomic(archive, journal);
        File.Delete(ActiveJournalPath(storageRoot));
        return archive;
    }

    private static string ActiveJournalPath(string storageRoot)
    {
        return Path.Combine(Path.GetFullPath(storageRoot), ActiveJournalName);
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

    private static int Usage()
    {
        Console.Error.WriteLine(
            "Usage:\n"
            + "  WarmRestoreExperiment preflight <storage> <profile> <snapshot>\n"
            + "  WarmRestoreExperiment stage <storage> <profile> <snapshot> --confirmed-t2r-credits\n"
            + "  WarmRestoreExperiment checkpoint <storage> <profile> --confirmed-main-menu-after-t2r\n"
            + "  WarmRestoreExperiment verify <storage> <profile> --confirmed-no-return-hideout\n"
            + "  WarmRestoreExperiment accept <storage> <profile> --confirmed-trading-station\n"
            + "  WarmRestoreExperiment recover <storage> <profile>\n"
            + "  WarmRestoreExperiment status <storage>");
        return 2;
    }

    private sealed class ExperimentStoppedProbe : IGameProcessProbe
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

    public sealed class WarmReloadExperimentJournal
    {
        public int SchemaVersion { get; set; }
        public string ExperimentId { get; set; }
        public string StartedUtc { get; set; }
        public string UpdatedUtc { get; set; }
        public string State { get; set; }
        public string ProfilePath { get; set; }
        public string GameExecutablePath { get; set; }
        public string GameExecutableSha256 { get; set; }
        public string TargetSnapshotId { get; set; }
        public string TargetStateCode { get; set; }
        public string OriginalStateCode { get; set; }
        public string UndoSnapshotId { get; set; }
        public string OriginalSourceMode { get; set; }
        public string OriginalRunIconSha256 { get; set; }
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
    }

    public sealed class LiveRawRun
    {
        public byte[] RawBytes { get; set; }
        public string StateCode { get; set; }
        public string SourceKind { get; set; }
    }

    public sealed class ExperimentalNativeSource
    {
        public string StateCode { get; set; }
        public string Signature { get; set; }
        public string SourceMode { get; set; }
        public string RunIconSha256 { get; set; }
    }
}
