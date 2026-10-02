using NoReturnGuardian.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace NoReturnGuardian.Tests
{
    internal static class Program
    {
        private static readonly List<TestCase> Tests = new List<TestCase>
        {
            new TestCase("redirected Documents profile discovery", TestProfileDiscovery),
            new TestCase("complete snapshot and checksum verification", TestSnapshotCapture),
            new TestCase("storage layer blocks automatic preparation capture", TestAutomaticCaptureBlocked),
            new TestCase("manual preparation requires explicit player confirmation", TestPreparationConfirmationRequired),
            new TestCase("hideout save frozen before the departure becomes a restorable departure preparation", TestDepartureCapture),
            new TestCase("save written while the game handled the departure only restarts its encounter", TestPostDepartureSaveOnlyRestarts),
            new TestCase("departure capture rejects stale, changed or unproven saves", TestDepartureCaptureRejected),
            new TestCase("departure evidence tampering or downgrade is rejected", TestDepartureEvidenceTamperingRejected),
            new TestCase("departure retention keeps manual points and drops old departure records", TestDepartureRetention),
            new TestCase("save mutation during copy is rejected", TestCaptureMutationRejected),
            new TestCase("backup evidence mutation during copy is rejected", TestBackupEvidenceMutationRejected),
            new TestCase("incomplete run is rejected as preparation", TestIncompleteCaptureRejected),
            new TestCase("hideout working state survives stale empty export slot", TestHideoutWorkingStateWithStaleEmptySlotAccepted),
            new TestCase("hideout hot capture ignores unknown live slot icon", TestHideoutCaptureWithUnknownLiveSlotIcon),
            new TestCase("mixed-generation run creates no manual snapshot", TestMixedGenerationCaptureRejected),
            new TestCase("R0A working-copy mismatch creates no snapshot", TestWorkingCopyMismatchRejected),
            new TestCase("same-length R0A mirror divergence creates no snapshot", TestWorkingMirrorContentMismatchRejected),
            new TestCase("working R0A state words remain semantically neutral", TestWorkingStateWordObservation),
            new TestCase("unknown working state word blocks manual capture", TestUnknownWorkingStateWordRejected),
            new TestCase("schema-5 manual capture does not deadlock on one witness", TestSingleWitnessManualCapture),
            new TestCase("native hideout export reconstructs a complete snapshot", TestNativeExportCapture),
            new TestCase("verified legacy R0A footer reconstructs a complete snapshot", TestNativeLegacyRunFooterCapture),
            new TestCase("native R0A CRC corruption blocks capture", TestNativeRunCrcRejected),
            new TestCase("unknown native R0A footer blocks capture", TestNativeRunFooterRejected),
            new TestCase("native 0P footer corruption blocks capture", TestNativeProfileFooterRejected),
            new TestCase("mixed-generation native export blocks capture", TestNativeExportGenerationRejected),
            new TestCase("partial working envelope cannot fall back to a stale slot", TestPartialWorkingEnvelopeRejected),
            new TestCase("native encounter export cannot be captured", TestNativeEncounterExportRejected),
            new TestCase("native export mutation during reconstruction is rejected", TestNativeExportMutationRejected),
            new TestCase("rehashed reconstructed 0P tampering is rejected", TestNativeProfileTamperingRejected),
            new TestCase("schema-6 native basis downgrade is rejected", TestNativeBasisDowngradeRejected),
            new TestCase("read-only monitor never captures observed word-1 writes", TestMonitorNeverCapturesObservedWord1Writes),
            new TestCase("corrupt payload is rejected", TestCorruptPayloadRejected),
            new TestCase("manifest control-field tampering is rejected", TestManifestTamperingRejected),
            new TestCase("rehashed divergent working mirror is rejected", TestRehashedMirrorDivergenceRejected),
            new TestCase("rehashed divergent synthesized slot is rejected", TestRehashedSynthesizedSlotRejected),
            new TestCase("rehashed divergent slot metadata is rejected", TestRehashedSlotMetadataRejected),
            new TestCase("unknown working header blocks hot synthesis", TestUnknownWorkingHeaderRejected),
            new TestCase("historical automatic preparation cannot be restored", TestHistoricalAutomaticRestoreRejected),
            new TestCase("historical manual encounter capture cannot be restored", TestHistoricalManualEncounterRestoreRejected),
            new TestCase("legacy schema-3 hot snapshot cannot be restored", TestSchema3HotSnapshotRejected),
            new TestCase("legacy four-file snapshot cannot be restored", TestLegacyRestoreRejected),
            new TestCase("restore is blocked while game runs", TestRestoreBlockedWhileRunning),
            new TestCase("restore is blocked across save profiles", TestRestoreBlockedAcrossProfiles),
            new TestCase("restore preserves permanent profile and built-in backups", TestRestorePreservesOutOfScopeFiles),
            new TestCase("restore refreshes all session timestamps", TestRestoreRefreshesTimestamps),
            new TestCase("missing death marker restores and undo returns exact partial state", TestMissingMarkerRestoreAndUndo),
            new TestCase("corrupt target never modifies live files", TestCorruptRestoreDoesNotWrite),
            new TestCase("mid-restore fault rolls back exact live state", TestMidRestoreRollback),
            new TestCase("interrupted restore journal recovers pre-restore state", TestInterruptedRestoreRecovery),
            new TestCase("path traversal manifest is rejected", TestPathTraversalRejected),
            new TestCase("snapshot deletion is transactional-state aware", TestSnapshotDeletion),
            new TestCase("retention keeps manual points and caps automatic history", TestRetention),
            new TestCase("cleanup keeps the newest three preparations and undo points per profile", TestRecentCleanup),
            new TestCase("automatic cleanup keeps snapshots that are in use", TestRecentCleanupKeepsPinned),
            new TestCase("restore applies nr.bin last", TestMarkerAppliedLast),
            new TestCase("warm reload accepts an exact target generation", TestWarmReloadExactTarget),
            new TestCase("warm reload recognizes a closer target descendant", TestWarmReloadTargetDescendant),
            new TestCase("warm reload rejects cached original reassertion", TestWarmReloadCachedOriginal),
            new TestCase("warm reload rejects an original-generation counter", TestWarmReloadOriginalCounter),
            new TestCase("warm reload rejects unexpected identity and encounter state", TestWarmReloadSemanticMismatch),
            new TestCase("warm reload leaves equal-distance evidence inconclusive", TestWarmReloadAmbiguous),
            new TestCase("warm restore stages only after process suspension and preserves exact undo", TestWarmRestoreStageAndRecover),
            new TestCase("warm restore accepts unknown live icon through target surrogate", TestWarmRestoreUnknownLiveIcon),
            new TestCase("warm restore resumes across checkpoint, hideout, and trading-station phases", TestWarmRestoreRestartFlow),
            new TestCase("warm restore rejects unverified build and partial source without writes", TestWarmRestoreStageGates),
            new TestCase("warm restore rejects cached original and encounter re-entry", TestWarmRestoreRejectionEvidence),
            new TestCase("warm navigation follows verified menu order and confirms T2R media", TestWarmNavigationRoute),
            new TestCase("warm navigation input matches the native Windows ABI", TestWarmNavigationInputAbi),
            new TestCase("warm navigation focus loss stops before any menu input", TestWarmNavigationFocusGate),
            new TestCase("warm navigation dry run has no save-controller path", TestWarmNavigationDryRun),
            new TestCase("warm automation preflight failure sends no navigation input", TestWarmAutomationPreflightGate),
            new TestCase("warm automation never stages when credits arrival is unproven", TestWarmAutomationArrivalGate),
            new TestCase("warm automation reuses preflight stability evidence", TestWarmAutomationReusesPreflightEvidence),
            new TestCase("warm automation stages returns and checkpoints in one operation", TestWarmAutomationCompleteRoute),
            new TestCase("warm credits exit handles confirmation without stray main-menu input", TestWarmNavigationCreditsExit),
            new TestCase("Windows credits probe identifies the exact resource owner", TestWindowsCreditsResourceProbe),
            new TestCase("encounter exit raises suspected end without creating a snapshot", TestMonitorEndDetection),
            new TestCase("running unknown and hideout transitions never mimic run end", TestMonitorRunningTransitionNotEnd),
            new TestCase("monitor ignores transient incomplete state", TestMonitorTransientLoss)
        };

        private static int Main()
        {
            int passed = 0;
            List<string> failures = new List<string>();
            foreach (TestCase test in Tests)
            {
                try
                {
                    test.Body();
                    passed++;
                    Console.WriteLine("[PASS] " + test.Name);
                }
                catch (Exception exception)
                {
                    failures.Add(test.Name + ": " + exception.Message);
                    Console.WriteLine("[FAIL] " + test.Name);
                    Console.WriteLine("       " + exception);
                }
            }

            Console.WriteLine();
            Console.WriteLine("Result: " + passed + "/" + Tests.Count + " passed");
            if (failures.Count == 0)
            {
                return 0;
            }

            Console.WriteLine("Failures:");
            foreach (string failure in failures)
            {
                Console.WriteLine("- " + failure);
            }

            return 1;
        }

        private static void TestProfileDiscovery()
        {
            using (TestWorkspace workspace = new TestWorkspace())
            {
                string gameRoot = Path.Combine(
                    workspace.Documents,
                    "The Last of Us Part II");
                string older = Path.Combine(gameRoot, "11111111111111111");
                string newer = Path.Combine(gameRoot, "22222222222222222");
                SyntheticSave.WriteComplete(older, "100:1:0", 1);
                SyntheticSave.WriteComplete(newer, "200:1:0", 2);
                File.SetLastWriteTimeUtc(
                    SaveLayout.CombineUnderProfile(older, SaveLayout.RunDataPath),
                    DateTime.UtcNow.AddHours(-2));
                File.SetLastWriteTimeUtc(
                    SaveLayout.CombineUnderProfile(newer, SaveLayout.RunDataPath),
                    DateTime.UtcNow.AddHours(-1));

                SaveLocator locator = new SaveLocator(workspace.Documents);
                Result<string> resolved = locator.ResolveProfile(null);
                Assert.True(resolved.Success, resolved.Message);
                Assert.Equal(Path.GetFullPath(newer), resolved.Value);
                Assert.Equal(2, locator.DiscoverProfiles().Count);
            }
        }

        private static void TestSnapshotCapture()
        {
            using (TestWorkspace workspace = TestWorkspace.WithCompleteSave())
            {
                SnapshotStore store = workspace.CreateStore();
                Result<SnapshotRecord> capture = store.Capture(
                    workspace.Profile,
                    new CaptureOptions
                    {
                        Kind = SnapshotKinds.Manual,
                        Purpose = SnapshotPurposes.Preparation,
                        UserConfirmedPreparation = true,
                        Label = "safehouse",
                        SkipDeduplication = true
                    });

                Assert.True(capture.Success, capture.Message);
                Assert.True(capture.Value.Manifest.Complete, "snapshot should be complete");
                Assert.True(
                    SnapshotPolicy.IsConfirmedPreparation(capture.Value),
                    "manual capture should be a confirmed preparation");
                Assert.Equal(SaveLayout.RequiredFiles.Length, capture.Value.Manifest.Files.Count);
                Assert.Equal("20244:1:0", capture.Value.Manifest.Marker);
                Result<SnapshotRecord> verified = store.VerifySnapshot(
                    capture.Value.Manifest.Id,
                    true);
                Assert.True(verified.Success, verified.Message);
            }
        }

        private static void TestAutomaticCaptureBlocked()
        {
            using (TestWorkspace workspace = TestWorkspace.WithCompleteSave())
            {
                SnapshotStore store = workspace.CreateStore();
                Result<SnapshotRecord> capture = store.Capture(
                    workspace.Profile,
                    new CaptureOptions
                    {
                        Kind = SnapshotKinds.Automatic,
                        Purpose = SnapshotPurposes.Preparation
                    });

                Assert.False(capture.Success, "automatic preparation capture escaped the store gate");
                Assert.Equal("automatic_capture_disabled", capture.Code);
                Assert.Equal(0, store.ListSnapshots().Count);
            }
        }

        private static void TestPreparationConfirmationRequired()
        {
            using (TestWorkspace workspace = TestWorkspace.WithCompleteSave())
            {
                SnapshotStore store = workspace.CreateStore();
                Result<SnapshotRecord> capture = store.Capture(
                    workspace.Profile,
                    new CaptureOptions
                    {
                        Kind = SnapshotKinds.Manual,
                        Purpose = SnapshotPurposes.Preparation,
                        UserConfirmedPreparation = false
                    });

                Assert.False(capture.Success, "an unconfirmed preparation was captured");
                Assert.Equal("preparation_confirmation_required", capture.Code);
                Assert.Equal(0, store.ListSnapshots().Count);
            }
        }

        private static void TestCaptureMutationRejected()
        {
            using (TestWorkspace workspace = TestWorkspace.WithCompleteSave())
            {
                SnapshotStore store = workspace.CreateStore();
                store.CaptureHooks = new SnapshotHooks
                {
                    AfterPayloadCopied = () =>
                    {
                        string data = SaveLayout.CombineUnderProfile(
                            workspace.Profile,
                            SaveLayout.RunDataPath);
                        byte[] bytes = File.ReadAllBytes(data);
                        bytes[128] ^= 0x7F;
                        File.WriteAllBytes(data, bytes);
                        File.SetLastWriteTimeUtc(data, DateTime.UtcNow.AddSeconds(2));
                    }
                };

                Result<SnapshotRecord> capture = store.Capture(
                    workspace.Profile,
                    new CaptureOptions
                    {
                        Kind = SnapshotKinds.Manual,
                        Purpose = SnapshotPurposes.Preparation,
                        UserConfirmedPreparation = true
                    });
                Assert.False(capture.Success, "mutating capture must fail");
                Assert.True(
                    capture.Code == "source_changed" || capture.Code == "source_changed_after_hash",
                    "unexpected failure code: " + capture.Code);
                Assert.Equal(0, store.ListSnapshots().Count);
                Assert.False(
                    Directory.GetDirectories(store.SnapshotsRoot).Any(
                        path => Path.GetFileName(path).StartsWith(".pending-")),
                    "pending snapshot should be cleaned");
            }
        }

        private static void TestBackupEvidenceMutationRejected()
        {
            using (TestWorkspace workspace = TestWorkspace.WithCompleteSave())
            {
                SnapshotStore store = workspace.CreateStore();
                store.CaptureHooks = new SnapshotHooks
                {
                    AfterPayloadCopied = () => SyntheticSave.TouchBackupWitness(
                        workspace.Profile,
                        0,
                        TimeSpan.FromSeconds(1))
                };

                Result<SnapshotRecord> capture = store.Capture(
                    workspace.Profile,
                    new CaptureOptions
                    {
                        Kind = SnapshotKinds.Manual,
                        Purpose = SnapshotPurposes.Preparation,
                        UserConfirmedPreparation = true
                    });
                Assert.False(capture.Success, "changing preparation evidence must fail capture");
                Assert.Equal("source_changed", capture.Code);
                Assert.Equal(0, store.ListSnapshots().Count);
            }
        }

        private static void TestIncompleteCaptureRejected()
        {
            using (TestWorkspace workspace = TestWorkspace.WithCompleteSave())
            {
                File.Delete(SaveLayout.CombineUnderProfile(
                    workspace.Profile,
                    SaveLayout.MarkerPath));
                Result<SnapshotRecord> capture = workspace.CreateStore().Capture(
                    workspace.Profile,
                    new CaptureOptions
                    {
                        Kind = SnapshotKinds.Manual,
                        Purpose = SnapshotPurposes.Preparation,
                        UserConfirmedPreparation = true
                    });
                Assert.False(capture.Success, "incomplete run cannot be a preparation point");
                Assert.Equal("run_incomplete", capture.Code);
            }
        }

        private static void TestHideoutWorkingStateWithStaleEmptySlotAccepted()
        {
            using (TestWorkspace workspace = TestWorkspace.WithCompleteSave())
            {
                SyntheticSave.MakeSlotExportStaleAndEmpty(workspace.Profile, 5);
                SnapshotStore store = workspace.CreateStore();
                Result<SnapshotRecord> capture = store.Capture(
                    workspace.Profile,
                    new CaptureOptions
                    {
                        Kind = SnapshotKinds.Manual,
                        Purpose = SnapshotPurposes.Preparation,
                        UserConfirmedPreparation = true
                    });

                Assert.True(capture.Success, capture.Message);
                Assert.Equal(5, capture.Value.Manifest.SchemaVersion);
                Assert.Equal(
                    SnapshotCaptureBases.HotSynthesizedState,
                    capture.Value.Manifest.CaptureBasis);
                Assert.Equal(
                    capture.Value.WorkingRunStateCode,
                    capture.Value.Manifest.RunStateCode);
                Assert.Equal(
                    capture.Value.Manifest.WorkingRunDataLength + SaveLayout.SlotEnvelopeLength,
                    capture.Value.Manifest.RunDataLength);
                Assert.True(
                    SnapshotPolicy.IsConfirmedPreparation(capture.Value),
                    "the stale export slot overruled the coherent hideout working state");
                Result<RunStateDescriptor> live = RunStateProbe.Inspect(workspace.Profile, false);
                Assert.True(live.Success, live.Message);
                Assert.Equal(
                    SaveLayout.EmptyRunStateCode,
                    live.Value.RunStateCode);
                Assert.Equal(1, store.ListSnapshots().Count);
            }
        }

        private static void TestHideoutCaptureWithUnknownLiveSlotIcon()
        {
            using (TestWorkspace workspace = TestWorkspace.WithCompleteSave())
            {
                string liveIconPath = SaveLayout.CombineUnderProfile(
                    workspace.Profile,
                    SaveLayout.RunIconPath);
                byte[] unknownLiveIcon = { 0x02, 0x82, 0x9C, 0x01 };
                DateTime liveIconUtc = File.GetLastWriteTimeUtc(liveIconPath);
                File.WriteAllBytes(liveIconPath, unknownLiveIcon);
                File.SetLastWriteTimeUtc(liveIconPath, liveIconUtc);

                SnapshotStore store = workspace.CreateStore();
                Result<SnapshotRecord> capture = store.Capture(
                    workspace.Profile,
                    new CaptureOptions
                    {
                        Kind = SnapshotKinds.Manual,
                        Purpose = SnapshotPurposes.Preparation,
                        UserConfirmedPreparation = true
                    });

                Assert.True(capture.Success, capture.Message);
                Assert.BytesEqual(unknownLiveIcon, File.ReadAllBytes(liveIconPath));
                Assert.BytesEqual(
                    new byte[] { 0x01, 0x82, 0x9C, 0x01 },
                    File.ReadAllBytes(SnapshotPayload(capture.Value, SaveLayout.RunIconPath)));
                Result<SnapshotRecord> verification = store.VerifySnapshot(
                    capture.Value.Manifest.Id,
                    true);
                Assert.True(verification.Success, verification.Message);
            }
        }

        private static void TestMixedGenerationCaptureRejected()
        {
            using (TestWorkspace workspace = TestWorkspace.WithCompleteSave())
            {
                SyntheticSave.MakeMarkerGenerationOlder(workspace.Profile, TimeSpan.FromMinutes(2));
                SnapshotStore store = workspace.CreateStore();
                Result<SnapshotRecord> capture = store.Capture(
                    workspace.Profile,
                    new CaptureOptions
                    {
                        Kind = SnapshotKinds.Manual,
                        Purpose = SnapshotPurposes.Preparation,
                        UserConfirmedPreparation = true
                    });

                Assert.False(capture.Success, "mixed generations must not be captured");
                Assert.Equal("run_generation_mixed", capture.Code);
                Assert.Equal(0, store.ListSnapshots().Count);
            }
        }

        private static void TestWorkingCopyMismatchRejected()
        {
            using (TestWorkspace workspace = TestWorkspace.WithCompleteSave())
            {
                SyntheticSave.MakeWorkingCopyShapeMismatch(workspace.Profile);
                SnapshotStore store = workspace.CreateStore();
                Result<SnapshotRecord> capture = store.Capture(
                    workspace.Profile,
                    new CaptureOptions
                    {
                        Kind = SnapshotKinds.Manual,
                        Purpose = SnapshotPurposes.Preparation,
                        UserConfirmedPreparation = true
                    });

                Assert.False(capture.Success, "mismatched R0A working copy must not be captured");
                Assert.Equal("run_working_mirror_mismatch", capture.Code);
                Assert.Equal(0, store.ListSnapshots().Count);
            }
        }

        private static void TestWorkingMirrorContentMismatchRejected()
        {
            using (TestWorkspace workspace = TestWorkspace.WithCompleteSave())
            {
                SyntheticSave.MakeWorkingMirrorContentMismatch(workspace.Profile);
                SnapshotStore store = workspace.CreateStore();
                Result<SnapshotRecord> capture = store.Capture(
                    workspace.Profile,
                    new CaptureOptions
                    {
                        Kind = SnapshotKinds.Manual,
                        Purpose = SnapshotPurposes.Preparation,
                        UserConfirmedPreparation = true
                    });

                Assert.False(capture.Success, "same-length divergent mirrors must be rejected");
                Assert.Equal("run_working_mirror_mismatch", capture.Code);
                Assert.Equal(0, store.ListSnapshots().Count);
            }
        }

        private static void TestWorkingStateWordObservation()
        {
            using (TestWorkspace workspace = TestWorkspace.WithCompleteSave())
            {
                Result<RunStateDescriptor> word0 = RunStateProbe.Inspect(
                    workspace.Profile,
                    false);
                Assert.True(word0.Success, word0.Message);
                Assert.Equal(
                    WorkingRunStateSignals.ObservedWord0,
                    word0.Value.WorkingRunStateSignal);
                Assert.Equal("00800000", word0.Value.WorkingRunStateWord);
                Assert.True(word0.Value.ManualCaptureReady, word0.Value.ManualCaptureStatus);

                SyntheticSave.WriteEncounter(workspace.Profile, "20244:2:0", 19);
                Result<RunStateDescriptor> word1 = RunStateProbe.Inspect(
                    workspace.Profile,
                    false);
                Assert.True(word1.Success, word1.Message);
                Assert.Equal(
                    WorkingRunStateSignals.ObservedWord1,
                    word1.Value.WorkingRunStateSignal);
                Assert.Equal("00800001", word1.Value.WorkingRunStateWord);
                Assert.True(word1.Value.ManualCaptureReady, "recognized word 1 was rejected despite stable mirrors");
                Assert.Equal("manual_capture_ready", word1.Value.ManualCaptureStatus);
            }
        }

        private static void TestUnknownWorkingStateWordRejected()
        {
            using (TestWorkspace workspace = TestWorkspace.WithCompleteSave())
            {
                SyntheticSave.MakeWorkingStateWordUnknown(workspace.Profile);
                Result<RunStateDescriptor> probe = RunStateProbe.Inspect(
                    workspace.Profile,
                    false);
                Assert.True(probe.Success, probe.Message);
                Assert.Equal(
                    WorkingRunStateSignals.Unknown,
                    probe.Value.WorkingRunStateSignal);
                Assert.False(probe.Value.ManualCaptureReady, "unknown state word passed capture gate");
                Assert.Equal("run_state_word_unrecognized", probe.Value.ManualCaptureStatus);

                Result<SnapshotRecord> capture = workspace.CreateStore().Capture(
                    workspace.Profile,
                    new CaptureOptions
                    {
                        Kind = SnapshotKinds.Manual,
                        Purpose = SnapshotPurposes.Preparation,
                        UserConfirmedPreparation = true
                    });
                Assert.False(capture.Success, "manual intent bypassed an unknown state word");
                Assert.Equal("run_state_word_unrecognized", capture.Code);
            }
        }

        private static void TestSingleWitnessManualCapture()
        {
            using (TestWorkspace workspace = TestWorkspace.WithCompleteSave())
            {
                SyntheticSave.RewriteBackupStateWord(workspace.Profile, 0, "00800001");
                SyntheticSave.RewriteBackupStateWord(workspace.Profile, 1, "00800001");
                Result<RunStateDescriptor> probe = RunStateProbe.Inspect(
                    workspace.Profile,
                    false);
                Assert.True(probe.Success, probe.Message);
                Assert.Equal(1, probe.Value.MatchingBackupCount);
                Assert.True(probe.Value.ManualCaptureReady, probe.Value.ManualCaptureStatus);
                Assert.Equal("manual_capture_ready", probe.Value.ManualCaptureStatus);

                SnapshotStore store = workspace.CreateStore();
                Result<SnapshotRecord> capture = store.Capture(
                    workspace.Profile,
                    new CaptureOptions
                    {
                        Kind = SnapshotKinds.Manual,
                        Purpose = SnapshotPurposes.Preparation,
                        UserConfirmedPreparation = true,
                        Label = "single-witness-hideout",
                        SkipDeduplication = true
                    });
                Assert.True(capture.Success, capture.Message);
                Assert.Equal(5, capture.Value.Manifest.SchemaVersion);
                Assert.Equal(1, capture.Value.Manifest.MatchingBackupCount);
                Assert.True(
                    SnapshotPolicy.IsConfirmedPreparation(capture.Value),
                    "schema-5 single-witness snapshot was not restorable");
                Result<SnapshotRecord> verification = store.VerifySnapshot(
                    capture.Value.Manifest.Id,
                    true);
                Assert.True(verification.Success, verification.Message);

                SnapshotManifest legacy = FileTools.ReadJson<SnapshotManifest>(
                    Path.Combine(capture.Value.DirectoryPath, "manifest.json"));
                legacy.SchemaVersion = 4;
                legacy.ManifestSha256 = FileTools.ManifestHash(legacy);
                FileTools.WriteJsonAtomic(
                    Path.Combine(capture.Value.DirectoryPath, "manifest.json"),
                    legacy);
                Result<SnapshotRecord> legacyLookup = store.GetSnapshot(
                    capture.Value.Manifest.Id);
                Assert.False(
                    legacyLookup.Success,
                    "schema-4 snapshot bypassed its historical witness requirement");
                Assert.Equal("snapshot_invalid", legacyLookup.Code);
            }
        }

        private static void TestNativeExportCapture()
        {
            using (TestWorkspace workspace = TestWorkspace.WithCompleteSave())
            {
                SyntheticSave.MakeNativeExportOnly(workspace.Profile);
                Dictionary<string, byte[]> sourceBefore = SyntheticSave.ReadNativeSources(
                    workspace.Profile);
                Result<RunStateDescriptor> probe = RunStateProbe.Inspect(
                    workspace.Profile,
                    true);
                Assert.True(probe.Success, probe.Message);
                Assert.False(probe.Value.Complete, "missing working files appeared complete");
                Assert.True(probe.Value.NativeExportCaptureReady, probe.Value.ManualCaptureStatus);
                Assert.True(probe.Value.ManualCaptureReady, probe.Value.ManualCaptureStatus);
                Assert.Equal("native_export_capture_ready", probe.Value.ManualCaptureStatus);
                Assert.Equal(
                    SnapshotCaptureBases.NativeExportedState,
                    probe.Value.ManualCaptureBasis);

                SnapshotStore store = workspace.CreateStore();
                Result<SnapshotRecord> capture = store.Capture(
                    workspace.Profile,
                    new CaptureOptions
                    {
                        Kind = SnapshotKinds.Manual,
                        Purpose = SnapshotPurposes.Preparation,
                        UserConfirmedPreparation = true,
                        Label = "native-export-hideout"
                    });
                Assert.True(capture.Success, capture.Message);
                Assert.Equal("native_export_snapshot_created", capture.Code);
                Assert.Equal(6, capture.Value.Manifest.SchemaVersion);
                Assert.Equal(
                    SnapshotCaptureBases.NativeExportedState,
                    capture.Value.Manifest.CaptureBasis);
                Assert.Equal(8, capture.Value.Manifest.Files.Count);
                Assert.True(
                    SnapshotPolicy.IsConfirmedPreparation(capture.Value),
                    "reconstructed native export was not restorable");
                Assert.True(
                    !string.IsNullOrWhiteSpace(
                        capture.Value.Manifest.ProfileSlotExportSha256),
                    "0P export relationship was not persisted");
                Assert.BytesEqual(
                    File.ReadAllBytes(SnapshotPayload(capture.Value, SaveLayout.GameRunPath)),
                    File.ReadAllBytes(SnapshotPayload(
                        capture.Value,
                        SaveLayout.GameRunBackupPath)));
                Assert.BytesEqual(
                    File.ReadAllBytes(SnapshotPayload(capture.Value, SaveLayout.GameProfilePath)),
                    File.ReadAllBytes(SnapshotPayload(
                        capture.Value,
                        SaveLayout.GameProfileBackupPath)));
                Result<SnapshotRecord> verification = store.VerifySnapshot(
                    capture.Value.Manifest.Id,
                    true);
                Assert.True(verification.Success, verification.Message);
                SyntheticSave.AssertNativeSourcesEqual(workspace.Profile, sourceBefore);
                Assert.False(
                    File.Exists(SaveLayout.CombineUnderProfile(
                        workspace.Profile,
                        SaveLayout.GameRunPath)),
                    "native capture wrote reconstructed files into the live game profile");
            }
        }

        private static void TestNativeRunCrcRejected()
        {
            using (TestWorkspace workspace = TestWorkspace.WithCompleteSave())
            {
                SyntheticSave.MakeNativeExportOnly(workspace.Profile);
                SyntheticSave.CorruptNativeRunPayload(workspace.Profile);
                Result<RunStateDescriptor> probe = RunStateProbe.Inspect(
                    workspace.Profile,
                    true);
                Assert.True(probe.Success, probe.Message);
                Assert.False(probe.Value.ManualCaptureReady, "bad R0A CRC passed capture");
                Assert.Equal("native_run_export_invalid", probe.Value.ManualCaptureStatus);
                Assert.Equal(0, workspace.CreateStore().ListSnapshots().Count);
            }
        }

        private static void TestNativeLegacyRunFooterCapture()
        {
            using (TestWorkspace workspace = TestWorkspace.WithCompleteSave())
            {
                SyntheticSave.UseNativeLegacyRunFooter(workspace.Profile);
                SyntheticSave.MakeNativeExportOnly(workspace.Profile);
                Result<RunStateDescriptor> probe = RunStateProbe.Inspect(
                    workspace.Profile,
                    true);
                Assert.True(probe.Success, probe.Message);
                Assert.True(probe.Value.NativeExportCaptureReady, probe.Value.ManualCaptureStatus);

                SnapshotStore store = workspace.CreateStore();
                SnapshotRecord snapshot = CaptureManual(store, workspace.Profile);
                Assert.Equal(6, snapshot.Manifest.SchemaVersion);
                Assert.Equal(
                    SnapshotCaptureBases.NativeExportedState,
                    snapshot.Manifest.CaptureBasis);
                Result<SnapshotRecord> verification = store.VerifySnapshot(
                    snapshot.Manifest.Id,
                    true);
                Assert.True(verification.Success, verification.Message);
            }
        }

        private static void TestNativeRunFooterRejected()
        {
            using (TestWorkspace workspace = TestWorkspace.WithCompleteSave())
            {
                SyntheticSave.MakeNativeExportOnly(workspace.Profile);
                SyntheticSave.CorruptNativeRunFooter(workspace.Profile);
                Result<RunStateDescriptor> probe = RunStateProbe.Inspect(
                    workspace.Profile,
                    true);
                Assert.True(probe.Success, probe.Message);
                Assert.False(probe.Value.ManualCaptureReady, "unknown R0A footer passed");
                Assert.Equal("native_run_export_invalid", probe.Value.ManualCaptureStatus);
            }
        }

        private static void TestNativeProfileFooterRejected()
        {
            using (TestWorkspace workspace = TestWorkspace.WithCompleteSave())
            {
                SyntheticSave.MakeNativeExportOnly(workspace.Profile);
                SyntheticSave.CorruptNativeProfileFooter(workspace.Profile);
                Result<RunStateDescriptor> probe = RunStateProbe.Inspect(
                    workspace.Profile,
                    true);
                Assert.True(probe.Success, probe.Message);
                Assert.False(probe.Value.ManualCaptureReady, "bad 0P footer passed capture");
                Assert.Equal("native_profile_export_invalid", probe.Value.ManualCaptureStatus);
            }
        }

        private static void TestNativeExportGenerationRejected()
        {
            using (TestWorkspace workspace = TestWorkspace.WithCompleteSave())
            {
                SyntheticSave.MakeNativeExportOnly(workspace.Profile);
                SyntheticSave.MakeNativeProfileGenerationOlder(
                    workspace.Profile,
                    TimeSpan.FromMinutes(2));
                Result<RunStateDescriptor> probe = RunStateProbe.Inspect(
                    workspace.Profile,
                    true);
                Assert.True(probe.Success, probe.Message);
                Assert.False(probe.Value.ManualCaptureReady, "mixed native generations passed");
                Assert.Equal("run_generation_mixed", probe.Value.ManualCaptureStatus);
            }
        }

        private static void TestPartialWorkingEnvelopeRejected()
        {
            using (TestWorkspace workspace = TestWorkspace.WithCompleteSave())
            {
                SyntheticSave.MakeNativeExportOnly(workspace.Profile);
                SyntheticSave.RestoreOneWorkingRunFile(workspace.Profile);
                Result<RunStateDescriptor> probe = RunStateProbe.Inspect(
                    workspace.Profile,
                    true);
                Assert.True(probe.Success, probe.Message);
                Assert.False(
                    probe.Value.ManualCaptureReady,
                    "partial working files fell back to the native slot");
                Assert.Equal("run_working_envelope_partial", probe.Value.ManualCaptureStatus);
            }
        }

        private static void TestNativeEncounterExportRejected()
        {
            using (TestWorkspace workspace = new TestWorkspace())
            {
                SyntheticSave.WriteEncounter(workspace.Profile, "20244:2:0", 19);
                SyntheticSave.MakeNativeExportOnly(workspace.Profile);
                Result<RunStateDescriptor> probe = RunStateProbe.Inspect(
                    workspace.Profile,
                    true);
                Assert.True(probe.Success, probe.Message);
                Assert.True(probe.Value.ManualCaptureReady, "recognized native word 1 was rejected");
                Assert.Equal("native_export_capture_ready", probe.Value.ManualCaptureStatus);
            }
        }

        private static void TestNativeExportMutationRejected()
        {
            using (TestWorkspace workspace = TestWorkspace.WithCompleteSave())
            {
                SyntheticSave.MakeNativeExportOnly(workspace.Profile);
                SnapshotStore store = workspace.CreateStore();
                store.CaptureHooks = new SnapshotHooks
                {
                    AfterPayloadCopied = () =>
                        SyntheticSave.CorruptNativeProfileFooter(workspace.Profile)
                };
                Result<SnapshotRecord> capture = store.Capture(
                    workspace.Profile,
                    new CaptureOptions
                    {
                        Kind = SnapshotKinds.Manual,
                        Purpose = SnapshotPurposes.Preparation,
                        UserConfirmedPreparation = true
                    });
                Assert.False(capture.Success, "mutating native export was accepted");
                Assert.Equal("source_changed", capture.Code);
                Assert.Equal(0, store.ListSnapshots().Count);
            }
        }

        private static void TestNativeProfileTamperingRejected()
        {
            using (TestWorkspace workspace = TestWorkspace.WithCompleteSave())
            {
                SyntheticSave.MakeNativeExportOnly(workspace.Profile);
                SnapshotStore store = workspace.CreateStore();
                SnapshotRecord snapshot = CaptureManual(store, workspace.Profile);
                foreach (string relativePath in new[]
                {
                    SaveLayout.GameProfilePath,
                    SaveLayout.GameProfileBackupPath
                })
                {
                    string payload = SnapshotPayload(snapshot, relativePath);
                    byte[] changed = File.ReadAllBytes(payload);
                    changed[changed.Length / 2] ^= 0x47;
                    File.WriteAllBytes(payload, changed);
                    RehashSnapshotFile(snapshot, relativePath);
                }

                Result<SnapshotRecord> verification = store.VerifySnapshot(
                    snapshot.Manifest.Id,
                    true);
                Assert.False(
                    verification.Success,
                    "rehashed 0P working state escaped native export verification");
                Assert.Equal("profile_export_hash_mismatch", verification.Code);
            }
        }

        private static void TestNativeBasisDowngradeRejected()
        {
            using (TestWorkspace workspace = TestWorkspace.WithCompleteSave())
            {
                SyntheticSave.MakeNativeExportOnly(workspace.Profile);
                SnapshotStore store = workspace.CreateStore();
                SnapshotRecord snapshot = CaptureManual(store, workspace.Profile);
                string manifestPath = Path.Combine(snapshot.DirectoryPath, "manifest.json");
                SnapshotManifest manifest = FileTools.ReadJson<SnapshotManifest>(manifestPath);
                manifest.CaptureBasis = SnapshotCaptureBases.HotSynthesizedState;
                manifest.ManifestSha256 = FileTools.ManifestHash(manifest);
                FileTools.WriteJsonAtomic(manifestPath, manifest);

                Result<SnapshotRecord> lookup = store.GetSnapshot(snapshot.Manifest.Id);
                Assert.False(lookup.Success, "native evidence was downgraded to hot synthesis");
                Assert.Equal("snapshot_invalid", lookup.Code);
            }
        }

        private static void TestMonitorNeverCapturesObservedWord1Writes()
        {
            using (TestWorkspace workspace = TestWorkspace.WithCompleteSave())
            {
                SyntheticSave.MakeSlotExportStaleAndEmpty(workspace.Profile, 5);
                SnapshotStore store = workspace.CreateStore();
                GuardianMonitor monitor = new GuardianMonitor(
                    store,
                    new FakeGameProbe { Running = true },
                    workspace.Clock)
                {
                    StabilityDelay = TimeSpan.FromSeconds(2),
                    AutoMonitor = true
                };
                monitor.SetProfile(workspace.Profile);

                MonitorStatus first = monitor.Poll();
                Assert.True(first.ManualCaptureReady, "stale export slot blocked the coherent working state");
                workspace.Clock.Advance(TimeSpan.FromSeconds(3));
                MonitorStatus stable = monitor.Poll();

                Assert.True(stable.ManualCaptureReady, stable.Message);
                Assert.Equal(0, store.ListSnapshots().Count);

                for (int index = 0; index < 6; index++)
                {
                    SyntheticSave.WriteEncounter(
                        workspace.Profile,
                        "20244:" + (index + 2) + ":0",
                        (byte)(20 + index));
                    monitor.Poll();
                    workspace.Clock.Advance(TimeSpan.FromSeconds(3));
                    MonitorStatus observedWrite = monitor.Poll();
                    Assert.True(observedWrite.ManualCaptureReady, "recognized word 1 was rejected");
                    Assert.Equal("manual_capture_ready", observedWrite.ManualCaptureStatus);
                    Assert.True(
                        store.ListSnapshots().Count == 0,
                        "a coherent encounter write created an automatic snapshot");
                }
            }
        }

        private static void TestCorruptPayloadRejected()
        {
            using (TestWorkspace workspace = TestWorkspace.WithCompleteSave())
            {
                SnapshotStore store = workspace.CreateStore();
                SnapshotRecord snapshot = CaptureManual(store, workspace.Profile);
                string payload = SnapshotPayload(
                    snapshot,
                    SaveLayout.RunDataPath);
                byte[] bytes = File.ReadAllBytes(payload);
                bytes[2048] ^= 0x42;
                File.WriteAllBytes(payload, bytes);

                Result<SnapshotRecord> verify = store.VerifySnapshot(
                    snapshot.Manifest.Id,
                    true);
                Assert.False(verify.Success, "corrupt payload must fail verification");
                Assert.Equal("snapshot_hash_mismatch", verify.Code);
            }
        }

        private static void TestManifestTamperingRejected()
        {
            using (TestWorkspace workspace = TestWorkspace.WithCompleteSave())
            {
                SnapshotStore store = workspace.CreateStore();
                SnapshotRecord snapshot = CaptureManual(store, workspace.Profile);
                string manifestPath = Path.Combine(snapshot.DirectoryPath, "manifest.json");
                SnapshotManifest manifest = FileTools.ReadJson<SnapshotManifest>(manifestPath);
                manifest.WorkingRunMirrorsMatch = false;
                FileTools.WriteJsonAtomic(manifestPath, manifest);

                Result<SnapshotRecord> lookup = store.GetSnapshot(snapshot.Manifest.Id);
                Assert.False(lookup.Success, "tampered control evidence must invalidate manifest");
                Assert.Equal("snapshot_invalid", lookup.Code);
            }
        }

        private static void TestRehashedMirrorDivergenceRejected()
        {
            using (TestWorkspace workspace = TestWorkspace.WithCompleteSave())
            {
                SnapshotStore store = workspace.CreateStore();
                SnapshotRecord snapshot = CaptureManual(store, workspace.Profile);
                string mirrorPayload = SnapshotPayload(
                    snapshot,
                    SaveLayout.GameRunBackupPath);
                byte[] changed = File.ReadAllBytes(mirrorPayload);
                changed[changed.Length / 2] ^= 0x35;
                File.WriteAllBytes(mirrorPayload, changed);

                string manifestPath = Path.Combine(snapshot.DirectoryPath, "manifest.json");
                SnapshotManifest manifest = FileTools.ReadJson<SnapshotManifest>(manifestPath);
                SnapshotFileEntry mirrorEntry = manifest.Files.First(entry => string.Equals(
                    entry.RelativePath,
                    SaveLayout.GameRunBackupPath,
                    StringComparison.OrdinalIgnoreCase));
                mirrorEntry.Sha256 = FileTools.Sha256(mirrorPayload, false);
                manifest.CompositeSha256 = FileTools.CompositeHash(manifest.Files);
                manifest.ManifestSha256 = FileTools.ManifestHash(manifest);
                FileTools.WriteJsonAtomic(manifestPath, manifest);

                Result<SnapshotRecord> lookup = store.GetSnapshot(snapshot.Manifest.Id);
                Assert.False(
                    lookup.Success,
                    "a rehashed package with divergent working mirrors was accepted");
                Assert.Equal("snapshot_invalid", lookup.Code);
            }
        }

        private static void TestRehashedSynthesizedSlotRejected()
        {
            using (TestWorkspace workspace = TestWorkspace.WithCompleteSave())
            {
                SnapshotStore store = workspace.CreateStore();
                SnapshotRecord snapshot = CaptureManual(store, workspace.Profile);
                string payload = SnapshotPayload(snapshot, SaveLayout.RunDataPath);
                byte[] changed = File.ReadAllBytes(payload);
                changed[changed.Length / 2] ^= 0x6A;
                File.WriteAllBytes(payload, changed);
                RehashSnapshotFile(snapshot, SaveLayout.RunDataPath);

                Result<SnapshotRecord> verify = store.VerifySnapshot(
                    snapshot.Manifest.Id,
                    true);
                Assert.False(
                    verify.Success,
                    "a rehashed run slot unrelated to the working R0A was accepted");
                Assert.Equal("run_slot_payload_mismatch", verify.Code);
            }
        }

        private static void TestRehashedSlotMetadataRejected()
        {
            using (TestWorkspace workspace = TestWorkspace.WithCompleteSave())
            {
                SnapshotStore store = workspace.CreateStore();
                SnapshotRecord snapshot = CaptureManual(store, workspace.Profile);
                string paramsPath = SnapshotPayload(snapshot, SaveLayout.RunParamsPath);
                RunSlotMetadata metadata = FileTools.ReadJson<RunSlotMetadata>(paramsPath);
                metadata.detail = metadata.detail.Replace(
                    snapshot.Manifest.RunStateCode,
                    SaveLayout.EmptyRunStateCode);
                FileTools.WriteJsonAtomic(paramsPath, metadata);
                RehashSnapshotFile(snapshot, SaveLayout.RunParamsPath);

                Result<SnapshotRecord> verify = store.VerifySnapshot(
                    snapshot.Manifest.Id,
                    true);
                Assert.False(
                    verify.Success,
                    "rehashed slot metadata for another run state was accepted");
                Assert.Equal("run_slot_metadata_mismatch", verify.Code);
            }
        }

        private static void TestUnknownWorkingHeaderRejected()
        {
            using (TestWorkspace workspace = TestWorkspace.WithCompleteSave())
            {
                SyntheticSave.MakeWorkingHeaderUnknown(workspace.Profile);
                SnapshotStore store = workspace.CreateStore();
                Result<SnapshotRecord> capture = store.Capture(
                    workspace.Profile,
                    new CaptureOptions
                    {
                        Kind = SnapshotKinds.Manual,
                        Purpose = SnapshotPurposes.Preparation,
                        UserConfirmedPreparation = true
                    });

                Assert.False(capture.Success, "an unknown R0A header was hot-synthesized");
                Assert.Equal("run_slot_synthesis_unsupported", capture.Code);
                Assert.Equal(0, store.ListSnapshots().Count);
            }
        }

        private static void TestHistoricalAutomaticRestoreRejected()
        {
            using (TestWorkspace workspace = TestWorkspace.WithCompleteSave())
            {
                SnapshotStore store = workspace.CreateStore();
                SnapshotRecord captured = CaptureManual(store, workspace.Profile);
                RewriteSnapshotKind(captured, SnapshotKinds.Automatic);
                Result<SnapshotRecord> automatic = store.GetSnapshot(captured.Manifest.Id);
                Assert.True(automatic.Success, automatic.Message);
                Assert.False(
                    SnapshotPolicy.IsConfirmedPreparation(automatic.Value),
                    "a historical automatic capture remained restorable");

                SyntheticSave.WriteComplete(workspace.Profile, "20244:1:0", 9);
                Dictionary<string, byte[]> live = SyntheticSave.ReadScope(workspace.Profile);
                Result<RestoreOutcome> restore = store.Restore(
                    captured.Manifest.Id,
                    workspace.Profile,
                    new FakeGameProbe(),
                    null);

                Assert.False(restore.Success, "historical automatic snapshot was restored");
                Assert.Equal("snapshot_unconfirmed", restore.Code);
                SyntheticSave.AssertScopeEquals(workspace.Profile, live);
                Assert.False(store.HasPendingRestore(), "rejected restore left a journal behind");
            }
        }

        private static void TestHistoricalManualEncounterRestoreRejected()
        {
            using (TestWorkspace workspace = TestWorkspace.WithCompleteSave())
            {
                SnapshotStore store = workspace.CreateStore();
                SnapshotRecord captured = CaptureManual(store, workspace.Profile);
                RewriteSnapshotWorkingStateWord(captured, "00800001");
                Result<SnapshotRecord> historical = store.GetSnapshot(captured.Manifest.Id);
                Assert.True(historical.Success, historical.Message);
                Assert.False(
                    SnapshotPolicy.IsConfirmedPreparation(historical.Value),
                    "a historical manual encounter capture remained restorable");

                SyntheticSave.WriteComplete(workspace.Profile, "20244:9:0", 31);
                Dictionary<string, byte[]> live = SyntheticSave.ReadScope(workspace.Profile);
                Result<RestoreOutcome> restore = store.Restore(
                    captured.Manifest.Id,
                    workspace.Profile,
                    new FakeGameProbe(),
                    null);
                Assert.False(restore.Success, "historical encounter snapshot was restored");
                Assert.Equal("run_slot_synthesis_unsupported", restore.Code);
                SyntheticSave.AssertScopeEquals(workspace.Profile, live);
            }
        }

        private static void TestSchema3HotSnapshotRejected()
        {
            using (TestWorkspace workspace = TestWorkspace.WithCompleteSave())
            {
                SnapshotStore store = workspace.CreateStore();
                SnapshotRecord captured = CaptureManual(store, workspace.Profile);
                string manifestPath = Path.Combine(captured.DirectoryPath, "manifest.json");
                SnapshotManifest manifest = FileTools.ReadJson<SnapshotManifest>(manifestPath);
                manifest.SchemaVersion = 3;
                manifest.CaptureBasis = SnapshotCaptureBases.GamedataWorkingState;
                manifest.PreparationConfirmation = null;
                manifest.ManifestSha256 = FileTools.ManifestHash(manifest);
                FileTools.WriteJsonAtomic(manifestPath, manifest);

                SyntheticSave.WriteComplete(workspace.Profile, "20244:7:0", 27);
                Dictionary<string, byte[]> live = SyntheticSave.ReadScope(workspace.Profile);
                Result<RestoreOutcome> restore = store.Restore(
                    captured.Manifest.Id,
                    workspace.Profile,
                    new FakeGameProbe(),
                    null);

                Assert.False(restore.Success, "a schema-3 hot snapshot was restored");
                Assert.True(
                    restore.Code == "snapshot_invalid" || restore.Code == "snapshot_unconfirmed",
                    "unexpected schema-3 rejection code: " + restore.Code);
                SyntheticSave.AssertScopeEquals(workspace.Profile, live);

                Result<bool> deleted = store.DeleteSnapshot(captured.Manifest.Id);
                Assert.True(deleted.Success, deleted.Message);
            }
        }

        private static void TestLegacyRestoreRejected()
        {
            using (TestWorkspace workspace = TestWorkspace.WithCompleteSave())
            {
                SnapshotStore store = workspace.CreateStore();
                SnapshotRecord captured = CaptureManual(store, workspace.Profile);
                SnapshotManifest manifest = FileTools.ReadJson<SnapshotManifest>(
                    Path.Combine(captured.DirectoryPath, "manifest.json"));
                manifest.SchemaVersion = 1;
                manifest.Purpose = null;
                FileTools.WriteJsonAtomic(
                    Path.Combine(captured.DirectoryPath, "manifest.json"),
                    manifest);

                SyntheticSave.WriteComplete(workspace.Profile, "20244:2:0", 12);
                Dictionary<string, byte[]> live = SyntheticSave.ReadScope(workspace.Profile);
                Result<RestoreOutcome> restore = store.Restore(
                    captured.Manifest.Id,
                    workspace.Profile,
                    new FakeGameProbe(),
                    null);

                Assert.False(restore.Success, "legacy snapshot must not be restored");
                Assert.Equal("snapshot_unconfirmed", restore.Code);
                SyntheticSave.AssertScopeEquals(workspace.Profile, live);
                Assert.False(store.HasPendingRestore(), "rejected legacy snapshot opened a journal");
            }
        }

        private static void TestRestoreBlockedWhileRunning()
        {
            using (TestWorkspace workspace = TestWorkspace.WithCompleteSave())
            {
                SnapshotStore store = workspace.CreateStore();
                SnapshotRecord snapshot = CaptureManual(store, workspace.Profile);
                SyntheticSave.WriteComplete(workspace.Profile, "20244:2:0", 9);
                Dictionary<string, byte[]> before = SyntheticSave.ReadScope(workspace.Profile);
                FakeGameProbe probe = new FakeGameProbe { Running = true };

                Result<RestoreOutcome> restore = store.Restore(
                    snapshot.Manifest.Id,
                    workspace.Profile,
                    probe,
                    null);
                Assert.False(restore.Success, "running game must block restore");
                Assert.Equal("game_running", restore.Code);
                SyntheticSave.AssertScopeEquals(workspace.Profile, before);
            }
        }

        private static void TestRestoreBlockedAcrossProfiles()
        {
            using (TestWorkspace workspace = TestWorkspace.WithCompleteSave())
            {
                SnapshotStore store = workspace.CreateStore();
                SnapshotRecord snapshot = CaptureManual(store, workspace.Profile);
                string otherProfile = Path.Combine(
                    workspace.Documents,
                    "The Last of Us Part II",
                    "11111111111111111");
                SyntheticSave.WriteComplete(otherProfile, "20244:8:0", 8);
                Dictionary<string, byte[]> before = SyntheticSave.ReadScope(otherProfile);

                Result<RestoreOutcome> restore = store.Restore(
                    snapshot.Manifest.Id,
                    otherProfile,
                    new FakeGameProbe(),
                    null);

                Assert.False(restore.Success, "cross-profile restore must be rejected");
                Assert.Equal("snapshot_profile_mismatch", restore.Code);
                SyntheticSave.AssertScopeEquals(otherProfile, before);
                Assert.False(store.HasPendingRestore(), "profile rejection opened a restore journal");
            }
        }

        private static void TestRestorePreservesOutOfScopeFiles()
        {
            using (TestWorkspace workspace = TestWorkspace.WithCompleteSave())
            {
                string profileData = Path.Combine(
                    workspace.Profile,
                    "savedata",
                    "SAVEFILE0P",
                    "USR-DATA");
                Directory.CreateDirectory(Path.GetDirectoryName(profileData));
                byte[] permanent = Encoding.UTF8.GetBytes("permanent-profile-progress");
                File.WriteAllBytes(profileData, permanent);

                SnapshotStore store = workspace.CreateStore();
                Dictionary<string, byte[]> expected = SyntheticSave.ReadScope(workspace.Profile);
                SnapshotRecord snapshot = CaptureManual(store, workspace.Profile);
                SyntheticSave.WriteComplete(workspace.Profile, "20244:3:0", 7);
                string builtInBackup = Path.Combine(
                    workspace.Profile,
                    "savedata",
                    "backup",
                    "USR-DATA.R0A_bak0");
                byte[] builtIn = Encoding.UTF8.GetBytes("game-owned-backup");
                File.WriteAllBytes(builtInBackup, builtIn);

                Result<RestoreOutcome> restore = store.Restore(
                    snapshot.Manifest.Id,
                    workspace.Profile,
                    new FakeGameProbe(),
                    null);
                Assert.True(restore.Success, restore.Message);
                SyntheticSave.AssertScopeEquals(workspace.Profile, expected);
                Assert.BytesEqual(permanent, File.ReadAllBytes(profileData));
                Assert.BytesEqual(builtIn, File.ReadAllBytes(builtInBackup));
            }
        }

        private static void TestRestoreRefreshesTimestamps()
        {
            using (TestWorkspace workspace = TestWorkspace.WithCompleteSave())
            {
                SnapshotStore store = workspace.CreateStore();
                SnapshotRecord snapshot = CaptureManual(store, workspace.Profile);
                SyntheticSave.WriteComplete(workspace.Profile, "20244:4:0", 10);
                workspace.Clock.Advance(TimeSpan.FromMinutes(7));
                DateTime restoreTime = workspace.Clock.UtcNow;

                Result<RestoreOutcome> restore = store.Restore(
                    snapshot.Manifest.Id,
                    workspace.Profile,
                    new FakeGameProbe(),
                    null);
                Assert.True(restore.Success, restore.Message);

                foreach (string relativePath in SaveLayout.RequiredFiles)
                {
                    DateTime actual = File.GetLastWriteTimeUtc(
                        SaveLayout.CombineUnderProfile(workspace.Profile, relativePath));
                    Assert.True(
                        Math.Abs((actual - restoreTime).TotalSeconds) < 1,
                        relativePath + " retained a stale timestamp");
                }
            }
        }

        private static void TestMissingMarkerRestoreAndUndo()
        {
            using (TestWorkspace workspace = TestWorkspace.WithCompleteSave())
            {
                SnapshotStore store = workspace.CreateStore();
                Dictionary<string, byte[]> preparation = SyntheticSave.ReadScope(workspace.Profile);
                SnapshotRecord snapshot = CaptureManual(store, workspace.Profile);

                SyntheticSave.WriteComplete(workspace.Profile, "20244:9:9", 8);
                File.Delete(SaveLayout.CombineUnderProfile(
                    workspace.Profile,
                    SaveLayout.MarkerPath));
                Dictionary<string, byte[]> deathState = SyntheticSave.ReadScope(workspace.Profile);

                Result<RestoreOutcome> restore = store.Restore(
                    snapshot.Manifest.Id,
                    workspace.Profile,
                    new FakeGameProbe(),
                    null);
                Assert.True(restore.Success, restore.Message);
                SyntheticSave.AssertScopeEquals(workspace.Profile, preparation);
                Assert.True(!string.IsNullOrWhiteSpace(restore.Value.UndoSnapshotId), "undo point missing");

                Result<RestoreOutcome> undo = store.Undo(
                    restore.Value.UndoSnapshotId,
                    workspace.Profile,
                    new FakeGameProbe());
                Assert.True(undo.Success, undo.Message);
                SyntheticSave.AssertScopeEquals(workspace.Profile, deathState);
                Assert.False(File.Exists(SaveLayout.CombineUnderProfile(
                    workspace.Profile,
                    SaveLayout.MarkerPath)), "undo should restore marker absence");
            }
        }

        private static void TestCorruptRestoreDoesNotWrite()
        {
            using (TestWorkspace workspace = TestWorkspace.WithCompleteSave())
            {
                SnapshotStore store = workspace.CreateStore();
                SnapshotRecord snapshot = CaptureManual(store, workspace.Profile);
                string payload = SnapshotPayload(snapshot, SaveLayout.RunParamsPath);
                File.AppendAllText(payload, "corruption");

                SyntheticSave.WriteComplete(workspace.Profile, "20244:4:0", 4);
                Dictionary<string, byte[]> live = SyntheticSave.ReadScope(workspace.Profile);
                Result<RestoreOutcome> restore = store.Restore(
                    snapshot.Manifest.Id,
                    workspace.Profile,
                    new FakeGameProbe(),
                    null);
                Assert.False(restore.Success, "corrupt snapshot restore must fail");
                SyntheticSave.AssertScopeEquals(workspace.Profile, live);
                Assert.False(store.HasPendingRestore(), "verification failure must not create a journal");
            }
        }

        private static void TestMidRestoreRollback()
        {
            using (TestWorkspace workspace = TestWorkspace.WithCompleteSave())
            {
                SnapshotStore store = workspace.CreateStore();
                SnapshotRecord snapshot = CaptureManual(store, workspace.Profile);
                SyntheticSave.WriteComplete(workspace.Profile, "20244:5:0", 5);
                Dictionary<string, byte[]> live = SyntheticSave.ReadScope(workspace.Profile);

                Result<RestoreOutcome> restore = store.Restore(
                    snapshot.Manifest.Id,
                    workspace.Profile,
                    new FakeGameProbe(),
                    new RestoreHooks
                    {
                        AfterFileApplied = (path, count) =>
                        {
                            if (count == 2)
                            {
                                throw new IOException("Injected restore failure");
                            }
                        }
                    });

                Assert.False(restore.Success, "injected restore must fail");
                Assert.Equal("restore_failed_rolled_back", restore.Code);
                SyntheticSave.AssertScopeEquals(workspace.Profile, live);
                Assert.False(store.HasPendingRestore(), "successful rollback should close journal");
            }
        }

        private static void TestInterruptedRestoreRecovery()
        {
            using (TestWorkspace workspace = TestWorkspace.WithCompleteSave())
            {
                SnapshotStore store = workspace.CreateStore();
                Dictionary<string, byte[]> original = SyntheticSave.ReadScope(workspace.Profile);
                Result<SnapshotRecord> undo = store.Capture(
                    workspace.Profile,
                    new CaptureOptions
                    {
                        Kind = SnapshotKinds.PreRestore,
                        Purpose = SnapshotPurposes.LiveState,
                        Label = "journal-undo",
                        AllowIncomplete = true,
                        SkipDeduplication = true
                    });
                Assert.True(undo.Success, undo.Message);

                SyntheticSave.WriteComplete(workspace.Profile, "20244:6:0", 6);
                FileTools.WriteJsonAtomic(
                    store.JournalPath,
                    new RestoreJournal
                    {
                        SchemaVersion = 1,
                        StartedUtc = workspace.Clock.UtcNow.ToString("O"),
                        ProfilePath = workspace.Profile,
                        TargetSnapshotId = "interrupted-target",
                        UndoSnapshotId = undo.Value.Manifest.Id,
                        State = "applying"
                    });

                Result<RestoreOutcome> recovery = store.RecoverInterruptedRestore(
                    new FakeGameProbe());
                Assert.True(recovery.Success, recovery.Message);
                Assert.Equal("interrupted_restore_recovered", recovery.Code);
                SyntheticSave.AssertScopeEquals(workspace.Profile, original);
                Assert.False(store.HasPendingRestore(), "recovery should close journal");
            }
        }

        private static void TestPathTraversalRejected()
        {
            using (TestWorkspace workspace = TestWorkspace.WithCompleteSave())
            {
                SnapshotStore store = workspace.CreateStore();
                SnapshotRecord snapshot = CaptureManual(store, workspace.Profile);
                SnapshotManifest manifest = FileTools.ReadJson<SnapshotManifest>(
                    Path.Combine(snapshot.DirectoryPath, "manifest.json"));
                manifest.Files[0].RelativePath = "../../outside.bin";
                FileTools.WriteJsonAtomic(
                    Path.Combine(snapshot.DirectoryPath, "manifest.json"),
                    manifest);

                Result<SnapshotRecord> lookup = store.GetSnapshot(snapshot.Manifest.Id);
                Assert.False(lookup.Success, "unsafe path must invalidate manifest");
                Assert.Equal("snapshot_invalid", lookup.Code);
            }
        }

        /// <summary>
        /// What the departure recorder leaves behind: the profile's files frozen, timestamps and all, in the
        /// storage's staging directory before the departure; then the event, observed after the newest save.
        /// </summary>
        private static string StageOf(SnapshotStore store, string profile)
        {
            string staged = Path.Combine(
                store.StorageRoot,
                DepartureEvidence.StagingDirectoryName,
                "recorder-" + Guid.NewGuid().ToString("N"));
            foreach (string relativePath in SaveLayout.RequiredFiles)
            {
                string source = SaveLayout.CombineUnderProfile(profile, relativePath);
                string target = SaveLayout.CombineUnderProfile(staged, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(source, target);
                File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(source));
            }

            return staged;
        }

        private static DepartureEvidence DepartureOf(SnapshotStore store, string profile)
        {
            string staged = StageOf(store, profile);
            DateTime newest = SaveLayout.WorkingProtectionFiles.Max(path =>
                File.GetLastWriteTimeUtc(SaveLayout.CombineUnderProfile(staged, path)));
            return new DepartureEvidence
            {
                RouteIndex = 3,
                ObservedUtc = newest.AddMilliseconds(200),
                RunSha256 = FileTools.Sha256(SaveLayout.CombineUnderProfile(staged, SaveLayout.GameRunPath), true),
                StagedPath = staged
            };
        }

        private static void TestDepartureCapture()
        {
            using (TestWorkspace workspace = TestWorkspace.WithCompleteSave())
            {
                SnapshotStore store = workspace.CreateStore();
                Dictionary<string, byte[]> expected = SyntheticSave.ReadScope(workspace.Profile);
                DepartureEvidence departure = DepartureOf(store, workspace.Profile);
                // The game's departure save has replaced the live files by the time the capture runs.
                SyntheticSave.WriteComplete(workspace.Profile, "20244:9:0", 9);
                Result<SnapshotRecord> capture = store.CaptureDeparturePreparation(workspace.Profile, departure);
                Assert.True(capture.Success, capture.Message);
                Assert.Equal("departure_snapshot_created", capture.Code);
                SnapshotManifest manifest = capture.Value.Manifest;
                Assert.Equal(SnapshotKinds.Departure, manifest.Kind);
                Assert.Equal(PreparationConfirmations.NativeDepartureObserved, manifest.PreparationConfirmation);
                Assert.Equal(DepartureEvidence.PreDepartureSave, manifest.DepartureBasis);
                Assert.Equal(SnapshotCaptureBases.HotSynthesizedState, manifest.CaptureBasis);
                Assert.Equal(5, manifest.SchemaVersion);
                Assert.Equal(DepartureEvidence.RouteBoardEventSid, manifest.DepartureEventSid);
                Assert.Equal(3, manifest.DepartureRouteIndex.Value);
                Assert.Equal(Path.GetFullPath(workspace.Profile), manifest.SourceProfilePath);
                Assert.True(manifest.Id.Contains("-departure-"), "departure snapshots carry their own kind in the id");
                Assert.True(
                    string.Equals(manifest.DepartureRunSha256, departure.RunSha256, StringComparison.OrdinalIgnoreCase),
                    "the bound hideout-save hash must be recorded");
                Assert.True(SnapshotPolicy.IsConfirmedPreparation(capture.Value), "departure capture must be restorable");
                Assert.False(SnapshotPolicy.IsPostDepartureSave(capture.Value), "a pre-departure capture restores its hideout");
                Assert.Equal("hideout", SnapshotPolicy.Use(capture.Value));
                Assert.True(store.VerifySnapshot(manifest.Id, true).Success, "departure capture must verify");

                Result<RestoreOutcome> restore = store.Restore(manifest.Id, workspace.Profile, new FakeGameProbe(), null);
                Assert.True(restore.Success, restore.Message);
                SyntheticSave.AssertScopeEquals(workspace.Profile, expected);
            }
        }

        private static void TestDepartureCaptureRejected()
        {
            using (TestWorkspace workspace = TestWorkspace.WithCompleteSave())
            {
                SnapshotStore store = workspace.CreateStore();
                DepartureEvidence departure = DepartureOf(store, workspace.Profile);

                // A file written at or after the event is not the hideout save from before it.
                DepartureEvidence early = DepartureOf(store, workspace.Profile);
                early.ObservedUtc = departure.ObservedUtc.AddMilliseconds(-200);
                Result<SnapshotRecord> stale = store.CaptureDeparturePreparation(workspace.Profile, early);
                Assert.Equal("departure_save_not_before_event", stale.Code);

                DepartureEvidence other = DepartureOf(store, workspace.Profile);
                other.RunSha256 = new string('0', 64);
                Result<SnapshotRecord> changed = store.CaptureDeparturePreparation(workspace.Profile, other);
                Assert.Equal("departure_save_changed", changed.Code);

                foreach (DepartureEvidence invalid in new[]
                {
                    null,
                    new DepartureEvidence { RouteIndex = 64, ObservedUtc = departure.ObservedUtc, RunSha256 = departure.RunSha256, StagedPath = departure.StagedPath },
                    new DepartureEvidence { RouteIndex = -1, ObservedUtc = departure.ObservedUtc, RunSha256 = departure.RunSha256, StagedPath = departure.StagedPath },
                    new DepartureEvidence { RouteIndex = 3, RunSha256 = departure.RunSha256, StagedPath = departure.StagedPath },
                    new DepartureEvidence { RouteIndex = 3, ObservedUtc = departure.ObservedUtc, RunSha256 = "abc", StagedPath = departure.StagedPath }
                })
                {
                    Assert.Equal(
                        "departure_evidence_invalid",
                        store.CaptureDeparturePreparation(workspace.Profile, invalid).Code);
                }

                // Only the recorder's frozen copy in this storage: never the live profile or any other folder.
                foreach (string staged in new[] { null, workspace.Profile, Path.Combine(workspace.Root, "elsewhere"),
                    Path.Combine(store.StorageRoot, DepartureEvidence.StagingDirectoryName, "..", "snapshots") })
                {
                    DepartureEvidence misplaced = DepartureOf(store, workspace.Profile);
                    misplaced.StagedPath = staged;
                    Assert.Equal("departure_stage_missing", store.CaptureDeparturePreparation(workspace.Profile, misplaced).Code);
                }

                DepartureEvidence mixed = DepartureOf(store, workspace.Profile);
                SyntheticSave.MakeMarkerGenerationOlder(mixed.StagedPath, TimeSpan.FromMinutes(2));
                Assert.Equal("run_generation_mixed", store.CaptureDeparturePreparation(workspace.Profile, mixed).Code);
                Assert.Equal(0, store.ListSnapshots().Count);
                Assert.False(Directory.EnumerateDirectories(store.SnapshotsRoot, ".pending-*").Any(), "pending capture leaked");
            }
        }

        /// <summary>
        /// The first departure captures bound the save the game writes while it handles the departure. Its hideout
        /// shows the route board past this node and a refilled lockbox, so it never restores a hideout; it only
        /// starts its own encounter again, and cleanup still counts it as a departure preparation.
        /// </summary>
        private static void TestPostDepartureSaveOnlyRestarts()
        {
            using (TestWorkspace workspace = TestWorkspace.WithCompleteSave())
            {
                SnapshotStore store = workspace.CreateStore();
                SnapshotRecord snapshot = store.CaptureDeparturePreparation(
                    workspace.Profile,
                    DepartureOf(store, workspace.Profile)).Value;
                string manifestPath = Path.Combine(snapshot.DirectoryPath, "manifest.json");
                SnapshotManifest early = FileTools.ReadJson<SnapshotManifest>(manifestPath);
                early.DepartureBasis = null;
                early.ManifestSha256 = FileTools.ManifestHash(early);
                FileTools.WriteJsonAtomic(manifestPath, early);

                SnapshotRecord record = store.GetSnapshot(snapshot.Manifest.Id).Value;
                Assert.True(store.VerifySnapshot(snapshot.Manifest.Id, true).Success, "an early capture keeps verifying");
                Assert.False(SnapshotPolicy.IsConfirmedPreparation(record), "an early capture must not restore its hideout");
                Assert.True(SnapshotPolicy.IsPostDepartureSave(record), "an early capture is recognized as such");
                Assert.True(SnapshotPolicy.IsRedeployablePreparation(record), "an early capture restarts its encounter");
                Assert.Equal("restart", SnapshotPolicy.Use(record));
                Assert.Equal(
                    "snapshot_unconfirmed",
                    store.Restore(snapshot.Manifest.Id, workspace.Profile, new FakeGameProbe(), null).Code);
                SnapshotCleanupPlan plan = store.PlanRecentCleanup(workspace.Profile, 1).Value;
                Assert.True(
                    plan.KeptPreparations.Any(item => item.Manifest.Id == snapshot.Manifest.Id),
                    "cleanup must keep an early capture like any other departure preparation");

                early.DepartureBasis = "unknown_basis";
                early.ManifestSha256 = FileTools.ManifestHash(early);
                FileTools.WriteJsonAtomic(manifestPath, early);
                record = store.GetSnapshot(snapshot.Manifest.Id).Value;
                Assert.False(
                    record != null && SnapshotPolicy.IsRedeployablePreparation(record),
                    "an unknown departure basis is neither restorable nor restartable");
                Assert.Equal("none", SnapshotPolicy.Use(record));
            }
        }

        private static void TestDepartureEvidenceTamperingRejected()
        {
            using (TestWorkspace workspace = TestWorkspace.WithCompleteSave())
            {
                SnapshotStore store = workspace.CreateStore();
                SnapshotRecord snapshot = store.CaptureDeparturePreparation(
                    workspace.Profile,
                    DepartureOf(store, workspace.Profile)).Value;
                string manifestPath = Path.Combine(snapshot.DirectoryPath, "manifest.json");
                string original = File.ReadAllText(manifestPath, Encoding.UTF8);

                foreach (Action<SnapshotManifest> unhashedChange in new Action<SnapshotManifest>[]
                {
                    manifest => manifest.DepartureRouteIndex = 4,
                    manifest => manifest.DepartureBasis = null
                })
                {
                    File.WriteAllText(manifestPath, original, Encoding.UTF8);
                    SnapshotManifest unhashed = FileTools.ReadJson<SnapshotManifest>(manifestPath);
                    unhashedChange(unhashed);
                    FileTools.WriteJsonAtomic(manifestPath, unhashed);
                    Assert.Equal("snapshot_invalid", store.GetSnapshot(snapshot.Manifest.Id).Code);
                }

                foreach (Action<SnapshotManifest> tamper in new Action<SnapshotManifest>[]
                {
                    manifest => manifest.DepartureRunSha256 = new string('0', 64),
                    manifest => manifest.DepartureEventSid = "0000000000000000",
                    manifest => manifest.DepartureRouteIndex = null,
                    manifest => manifest.DepartureObservedUtc = "unknown",
                    manifest => manifest.DepartureBasis = null,
                    manifest => manifest.Kind = SnapshotKinds.Manual,
                    manifest => manifest.PreparationConfirmation = PreparationConfirmations.UserConfirmedHideout
                })
                {
                    File.WriteAllText(manifestPath, original, Encoding.UTF8);
                    SnapshotManifest manifest = FileTools.ReadJson<SnapshotManifest>(manifestPath);
                    tamper(manifest);
                    manifest.ManifestSha256 = FileTools.ManifestHash(manifest);
                    FileTools.WriteJsonAtomic(manifestPath, manifest);
                    Result<SnapshotRecord> lookup = store.GetSnapshot(snapshot.Manifest.Id);
                    Assert.False(
                        lookup.Success && SnapshotPolicy.IsConfirmedPreparation(lookup.Value),
                        "rehashed departure evidence must not stay restorable");
                }

                File.WriteAllText(manifestPath, original, Encoding.UTF8);
                Assert.True(
                    SnapshotPolicy.IsConfirmedPreparation(store.GetSnapshot(snapshot.Manifest.Id).Value),
                    "the untouched departure capture must stay restorable");
            }
        }

        private static void TestDepartureRetention()
        {
            using (TestWorkspace workspace = TestWorkspace.WithCompleteSave())
            {
                SnapshotStore store = workspace.CreateStore();
                SnapshotRecord manual = CaptureManual(store, workspace.Profile);
                List<SnapshotRecord> departures = new List<SnapshotRecord>();
                Directory.CreateDirectory(store.DeparturesRoot);
                for (int index = 0; index < 4; index++)
                {
                    SyntheticSave.WriteComplete(workspace.Profile, "20244:" + index + ":0", (byte)(60 + index));
                    workspace.Clock.Advance(TimeSpan.FromSeconds(1));
                    Result<SnapshotRecord> capture = store.CaptureDeparturePreparation(
                        workspace.Profile,
                        DepartureOf(store, workspace.Profile));
                    Assert.True(capture.Success, capture.Message);
                    departures.Add(capture.Value);
                    File.WriteAllText(Path.Combine(store.DeparturesRoot, capture.Value.Manifest.Id + ".json"), "{}");
                }

                File.WriteAllText(Path.Combine(store.DeparturesRoot, manual.Manifest.Id + ".json"), "{}");
                Result<int> pruned = store.PruneDepartureSnapshots(Path.Combine(workspace.Root, "other-profile"), 1);
                Assert.Equal(0, pruned.Value);
                pruned = store.PruneDepartureSnapshots(workspace.Profile, 2);
                Assert.True(pruned.Success, pruned.Message);
                Assert.Equal(2, pruned.Value);
                HashSet<string> remaining = new HashSet<string>(store.ListSnapshots().Select(item => item.Manifest.Id));
                Assert.True(
                    remaining.SetEquals(new[] { manual.Manifest.Id, departures[3].Manifest.Id, departures[2].Manifest.Id }),
                    "retention kept the wrong snapshots");
                HashSet<string> records = new HashSet<string>(
                    Directory.EnumerateFiles(store.DeparturesRoot).Select(Path.GetFileNameWithoutExtension));
                Assert.True(records.SetEquals(remaining), "departure records must follow their snapshots");

                SnapshotCleanupPlan plan = store.PlanRecentCleanup(workspace.Profile, 1).Value;
                Assert.True(
                    plan.KeptPreparations.Select(item => item.Manifest.Id)
                        .SequenceEqual(new[] { manual.Manifest.Id, departures[3].Manifest.Id }),
                    "cleanup keeps the newest manual and departure preparation separately");
                Assert.Equal(1, plan.RemovedPreparations);
                Assert.True(store.DeleteSnapshot(manual.Manifest.Id).Success, "manual deletion failed");
                Assert.False(
                    File.Exists(Path.Combine(store.DeparturesRoot, manual.Manifest.Id + ".json")),
                    "a deleted snapshot left its departure record behind");

                FileTools.WriteJsonAtomic(
                    store.JournalPath,
                    new RestoreJournal
                    {
                        SchemaVersion = 1,
                        StartedUtc = workspace.Clock.UtcNow.ToString("O"),
                        ProfilePath = workspace.Profile,
                        TargetSnapshotId = departures[3].Manifest.Id,
                        UndoSnapshotId = departures[3].Manifest.Id,
                        State = "prepared"
                    });
                Assert.Equal(
                    "departure_retention_blocked_restore_pending",
                    store.PruneDepartureSnapshots(workspace.Profile, 1).Code);
            }
        }

        private static void TestSnapshotDeletion()
        {
            using (TestWorkspace workspace = TestWorkspace.WithCompleteSave())
            {
                SnapshotStore store = workspace.CreateStore();
                SnapshotRecord manual = CaptureManual(store, workspace.Profile);
                SyntheticSave.WriteComplete(workspace.Profile, "20244:1:0", 12);
                SnapshotRecord historical = CaptureManual(store, workspace.Profile);
                RewriteSnapshotKind(historical, SnapshotKinds.Automatic);
                Result<SnapshotRecord> automatic = store.GetSnapshot(historical.Manifest.Id);
                Assert.True(automatic.Success, automatic.Message);

                Result<bool> deleted = store.DeleteSnapshot(automatic.Value.Manifest.Id);
                Assert.True(deleted.Success, deleted.Message);
                Assert.Equal("snapshot_deleted", deleted.Code);
                Assert.False(
                    store.GetSnapshot(automatic.Value.Manifest.Id).Success,
                    "deleted snapshot should be absent");

                FileTools.WriteJsonAtomic(
                    store.JournalPath,
                    new RestoreJournal
                    {
                        SchemaVersion = 1,
                        StartedUtc = workspace.Clock.UtcNow.ToString("O"),
                        ProfilePath = workspace.Profile,
                        TargetSnapshotId = manual.Manifest.Id,
                        UndoSnapshotId = manual.Manifest.Id,
                        State = "prepared"
                    });
                Result<bool> blocked = store.DeleteSnapshot(manual.Manifest.Id);
                Assert.False(blocked.Success, "pending restore must block snapshot deletion");
                Assert.Equal("snapshot_delete_blocked_restore_pending", blocked.Code);
                Assert.True(store.GetSnapshot(manual.Manifest.Id).Success, "blocked deletion removed data");

                File.Delete(store.JournalPath);
                Result<bool> manualDeleted = store.DeleteSnapshot(manual.Manifest.Id);
                Assert.True(manualDeleted.Success, manualDeleted.Message);
                Assert.False(store.GetSnapshot(manual.Manifest.Id).Success, "manual snapshot was not deleted");
            }
        }

        private static void TestRecentCleanup()
        {
            using (TestWorkspace workspace = TestWorkspace.WithCompleteSave())
            {
                SnapshotStore store = workspace.CreateStore();
                store.AutomaticRetention = 500;
                List<SnapshotRecord> manual = new List<SnapshotRecord>();
                List<SnapshotRecord> undo = new List<SnapshotRecord>();
                for (int index = 0; index < 5; index++)
                {
                    SyntheticSave.WriteComplete(workspace.Profile, "20244:" + index + ":0", (byte)(40 + index));
                    workspace.Clock.Advance(TimeSpan.FromSeconds(1));
                    manual.Add(CaptureManual(store, workspace.Profile));
                    workspace.Clock.Advance(TimeSpan.FromSeconds(1));
                    Result<SnapshotRecord> live = store.Capture(
                        workspace.Profile,
                        new CaptureOptions
                        {
                            Kind = SnapshotKinds.PreRestore,
                            Purpose = SnapshotPurposes.LiveState,
                            Label = "undo-" + index,
                            AllowIncomplete = true,
                            SkipDeduplication = true
                        });
                    Assert.True(live.Success, live.Message);
                    undo.Add(live.Value);
                }

                RewriteSnapshotKind(manual[0], SnapshotKinds.Automatic);
                SnapshotRecord foreign = manual[1];
                string foreignManifest = Path.Combine(foreign.DirectoryPath, "manifest.json");
                SnapshotManifest rewritten = FileTools.ReadJson<SnapshotManifest>(foreignManifest);
                rewritten.SourceProfilePath = Path.Combine(workspace.Root, "other-profile");
                rewritten.ManifestSha256 = FileTools.ManifestHash(rewritten);
                FileTools.WriteJsonAtomic(foreignManifest, rewritten);

                Result<SnapshotCleanupPlan> planned = store.PlanRecentCleanup(workspace.Profile, 3);
                Assert.True(planned.Success, planned.Message);
                SnapshotCleanupPlan plan = planned.Value;
                Assert.True(
                    plan.KeptPreparations.Select(item => item.Manifest.Id)
                        .SequenceEqual(new[] { manual[4], manual[3], manual[2] }.Select(item => item.Manifest.Id)),
                    "the newest three restorable preparations must be kept");
                Assert.True(
                    plan.KeptLiveStates.Select(item => item.Manifest.Id)
                        .SequenceEqual(new[] { undo[4], undo[3], undo[2] }.Select(item => item.Manifest.Id)),
                    "the newest three undo points, including the latest, must be kept");
                Assert.Equal(3, plan.Removals.Count);
                Assert.Equal(0, plan.RemovedPreparations);
                Assert.Equal(2, plan.RemovedLiveStates);
                Assert.Equal(1, plan.RemovedOther);
                Assert.True(plan.RemovalBytes > 0, "removal size must be reported");
                Assert.False(
                    plan.Removals.Any(item => item.Manifest.Id == foreign.Manifest.Id),
                    "another profile's snapshot must stay outside the cleanup");

                FileTools.WriteJsonAtomic(
                    store.JournalPath,
                    new RestoreJournal
                    {
                        SchemaVersion = 1,
                        StartedUtc = workspace.Clock.UtcNow.ToString("O"),
                        ProfilePath = workspace.Profile,
                        TargetSnapshotId = manual[4].Manifest.Id,
                        UndoSnapshotId = undo[4].Manifest.Id,
                        State = "prepared"
                    });
                Result<SnapshotCleanupOutcome> blocked = store.CleanupToRecent(plan);
                Assert.Equal("snapshot_cleanup_blocked_restore_pending", blocked.Code);
                File.Delete(store.JournalPath);

                SyntheticSave.WriteComplete(workspace.Profile, "20244:9:0", 49);
                workspace.Clock.Advance(TimeSpan.FromSeconds(1));
                SnapshotRecord newest = CaptureManual(store, workspace.Profile);
                Result<SnapshotCleanupOutcome> stale = store.CleanupToRecent(plan);
                Assert.Equal("snapshot_cleanup_plan_changed", stale.Code);
                Assert.Equal(11, store.ListSnapshots().Count);

                SnapshotCleanupPlan fresh = store.PlanRecentCleanup(workspace.Profile, 3).Value;
                Result<SnapshotCleanupOutcome> cleaned = store.CleanupToRecent(fresh);
                Assert.True(cleaned.Success, cleaned.Message);
                Assert.Equal(4, cleaned.Value.Removed);
                Assert.Equal(0, cleaned.Value.Failed);
                HashSet<string> remaining = new HashSet<string>(
                    store.ListSnapshots().Select(item => item.Manifest.Id));
                string[] expected =
                {
                    newest.Manifest.Id, manual[4].Manifest.Id, manual[3].Manifest.Id,
                    undo[4].Manifest.Id, undo[3].Manifest.Id, undo[2].Manifest.Id, foreign.Manifest.Id
                };
                Assert.True(remaining.SetEquals(expected), "cleanup kept the wrong snapshots");
                Assert.False(
                    store.PlanRecentCleanup(workspace.Profile, 0).Success,
                    "cleanup must always keep at least one snapshot per kind");
            }
        }

        private static void TestRecentCleanupKeepsPinned()
        {
            using (TestWorkspace workspace = TestWorkspace.WithCompleteSave())
            {
                SnapshotStore store = workspace.CreateStore();
                store.AutomaticRetention = 500;
                List<SnapshotRecord> manual = new List<SnapshotRecord>();
                List<SnapshotRecord> undo = new List<SnapshotRecord>();
                for (int index = 0; index < 5; index++)
                {
                    SyntheticSave.WriteComplete(workspace.Profile, "20244:" + index + ":0", (byte)(40 + index));
                    workspace.Clock.Advance(TimeSpan.FromSeconds(1));
                    manual.Add(CaptureManual(store, workspace.Profile));
                    workspace.Clock.Advance(TimeSpan.FromSeconds(1));
                    Result<SnapshotRecord> live = store.Capture(
                        workspace.Profile,
                        new CaptureOptions
                        {
                            Kind = SnapshotKinds.PreRestore,
                            Purpose = SnapshotPurposes.LiveState,
                            Label = "undo-" + index,
                            AllowIncomplete = true,
                            SkipDeduplication = true
                        });
                    Assert.True(live.Success, live.Message);
                    undo.Add(live.Value);
                }

                // 刚恢复过的旧战备、记录器正替它记录的战备，都不在最新三份里，也必须留下。
                string[] pinned = { manual[0].Manifest.Id, undo[1].Manifest.Id, null, "missing-id" };
                SnapshotCleanupPlan plan = store.PlanRecentCleanup(workspace.Profile, 3, pinned).Value;
                Assert.False(
                    plan.Removals.Any(item => item.Manifest.Id == manual[0].Manifest.Id || item.Manifest.Id == undo[1].Manifest.Id),
                    "a pinned snapshot was planned for removal");
                Assert.Equal(2, plan.Removals.Count);
                Assert.True(
                    plan.KeptPinned.Select(item => item.Manifest.Id)
                        .SequenceEqual(new[] { undo[1].Manifest.Id, manual[0].Manifest.Id }),
                    "pinned snapshots must be listed as kept");

                SnapshotCleanupPlan unpinned = store.PlanRecentCleanup(workspace.Profile, 3).Value;
                Assert.False(plan.SameRemovals(unpinned), "plans with different pins must not compare equal");
                Assert.Equal(4, unpinned.Removals.Count);

                Result<SnapshotCleanupOutcome> cleaned = store.CleanupToRecent(plan);
                Assert.True(cleaned.Success, cleaned.Message);
                Assert.Equal(2, cleaned.Value.Removed);
                HashSet<string> remaining = new HashSet<string>(
                    store.ListSnapshots().Select(item => item.Manifest.Id));
                Assert.True(remaining.Contains(manual[0].Manifest.Id), "the pinned preparation was deleted");
                Assert.True(remaining.Contains(undo[1].Manifest.Id), "the pinned undo point was deleted");
                Assert.False(remaining.Contains(manual[1].Manifest.Id), "an unpinned old preparation survived");
                Assert.Equal(8, remaining.Count);
            }
        }

        private static void TestRetention()
        {
            using (TestWorkspace workspace = TestWorkspace.WithCompleteSave())
            {
                SnapshotStore store = workspace.CreateStore();
                store.AutomaticRetention = 500;
                SnapshotRecord manual = CaptureManual(store, workspace.Profile);

                for (int index = 0; index < 12; index++)
                {
                    SyntheticSave.WriteComplete(
                        workspace.Profile,
                        "20244:" + index + ":0",
                        (byte)(20 + index));
                    workspace.Clock.Advance(TimeSpan.FromSeconds(1));
                    SnapshotRecord historical = CaptureManual(store, workspace.Profile);
                    RewriteSnapshotKind(historical, SnapshotKinds.Automatic);
                }

                Result<int> pruned = store.PruneAutomaticSnapshots(10);
                Assert.True(pruned.Success, pruned.Message);
                Assert.Equal(2, pruned.Value);

                IList<SnapshotRecord> records = store.ListSnapshots();
                Assert.Equal(10, records.Count(item => item.Manifest.Kind == SnapshotKinds.Automatic));
                Assert.True(records.Any(item => item.Manifest.Id == manual.Manifest.Id), "manual point was pruned");
            }
        }

        private static void TestMarkerAppliedLast()
        {
            using (TestWorkspace workspace = TestWorkspace.WithCompleteSave())
            {
                SnapshotStore store = workspace.CreateStore();
                SnapshotRecord snapshot = CaptureManual(store, workspace.Profile);
                SyntheticSave.WriteComplete(workspace.Profile, "20244:7:0", 7);
                List<string> order = new List<string>();

                Result<RestoreOutcome> restore = store.Restore(
                    snapshot.Manifest.Id,
                    workspace.Profile,
                    new FakeGameProbe(),
                    new RestoreHooks
                    {
                        AfterFileApplied = (path, count) => order.Add(path)
                    });
                Assert.True(restore.Success, restore.Message);
                Assert.Equal(SaveLayout.RestoreOrder.Length, order.Count);
                Assert.Equal(SaveLayout.MarkerPath, order.Last());
            }
        }

        private static void TestMonitorEndDetection()
        {
            using (TestWorkspace workspace = TestWorkspace.WithCompleteSave())
            {
                SnapshotStore store = workspace.CreateStore();
                FakeGameProbe process = new FakeGameProbe { Running = true };
                GuardianMonitor monitor = new GuardianMonitor(store, process, workspace.Clock)
                {
                    StabilityDelay = TimeSpan.FromSeconds(2),
                    AutoMonitor = true
                };
                monitor.SetProfile(workspace.Profile);
                bool notified = false;
                monitor.SuspectedEndDetected += (sender, args) => notified = true;

                MonitorStatus first = monitor.Poll();
                Assert.True(first.RunStateComplete, "initial run should be complete");
                Assert.True(first.ManualCaptureReady, "initial run should pass the manual capture gate");
                workspace.Clock.Advance(TimeSpan.FromSeconds(3));
                Result<SnapshotRecord> protectedResult = monitor.ProtectNow(
                    "confirmed-safehouse",
                    true);
                Assert.True(protectedResult.Success, protectedResult.Message);
                string protectedId = protectedResult.Value.Manifest.Id;
                Assert.Equal(1, store.ListSnapshots().Count);

                SyntheticSave.WriteEncounter(workspace.Profile, "20244:2:0", 44);
                MonitorStatus encounter = monitor.Poll();
                Assert.Equal(
                    WorkingRunStateSignals.ObservedWord1,
                    encounter.WorkingRunStateSignal);
                Assert.False(encounter.SuspectedRunEnded, "a running encounter was called ended");

                SyntheticSave.MakeWorkingStateWordUnknown(workspace.Profile);
                monitor.Poll();
                workspace.Clock.Advance(TimeSpan.FromSeconds(3));
                MonitorStatus settlement = monitor.Poll();
                Assert.False(settlement.ManualCaptureReady, "unknown transition passed the manual gate");
                Assert.Equal("run_state_word_unrecognized", settlement.ManualCaptureStatus);
                Assert.False(
                    settlement.SuspectedRunEnded,
                    "a running unknown transition was called ended");

                process.Running = false;
                settlement = monitor.Poll();
                Assert.False(settlement.SuspectedRunEnded, "an unclassified state word was treated as run end");
                Assert.Equal(protectedId, settlement.LastProtectedSnapshotId);
                Assert.Equal(protectedId, settlement.PreferredRestoreSnapshotId);
                Assert.False(notified, "an unclassified state word raised a suspected end event");
                Assert.Equal(1, store.ListSnapshots().Count);

                Result<SnapshotRecord> manual = monitor.ProtectNow("death-must-not-save", true);
                Assert.False(manual.Success, "manual intent bypassed the death-state gate");
                Assert.Equal("run_state_word_unrecognized", manual.Code);
                Assert.Equal(1, store.ListSnapshots().Count);
            }
        }

        private static void TestMonitorRunningTransitionNotEnd()
        {
            using (TestWorkspace workspace = TestWorkspace.WithCompleteSave())
            {
                SnapshotStore store = workspace.CreateStore();
                FakeGameProbe process = new FakeGameProbe { Running = true };
                GuardianMonitor monitor = new GuardianMonitor(store, process, workspace.Clock)
                {
                    StabilityDelay = TimeSpan.FromSeconds(2)
                };
                monitor.SetProfile(workspace.Profile);

                monitor.Poll();
                workspace.Clock.Advance(TimeSpan.FromSeconds(3));
                Result<SnapshotRecord> protectedResult = monitor.ProtectNow(
                    "confirmed-safehouse",
                    true);
                Assert.True(protectedResult.Success, protectedResult.Message);

                SyntheticSave.WriteEncounter(workspace.Profile, "20244:2:0", 45);
                monitor.Poll();
                SyntheticSave.MakeWorkingStateWordUnknown(workspace.Profile);
                monitor.Poll();
                workspace.Clock.Advance(TimeSpan.FromSeconds(10));
                MonitorStatus unknown = monitor.Poll();
                Assert.False(unknown.SuspectedRunEnded, "running word-2-like state raised end");

                SyntheticSave.WriteHideoutTransition(workspace.Profile, "20244:2:0", 46);
                MonitorStatus hideout = monitor.Poll();
                Assert.Equal(
                    WorkingRunStateSignals.ObservedWord0,
                    hideout.WorkingRunStateSignal);
                Assert.True(hideout.ManualCaptureReady, hideout.ManualCaptureStatus);
                Assert.Equal("manual_capture_ready", hideout.ManualCaptureStatus);
                Assert.False(
                    hideout.SuspectedRunEnded,
                    "observed hideout word did not clear encounter lifecycle state");

                process.Running = false;
                MonitorStatus stopped = monitor.Poll();
                Assert.False(
                    stopped.SuspectedRunEnded,
                    "exit after an observed hideout return was called an encounter end");
                Assert.Equal(1, store.ListSnapshots().Count);
            }
        }

        private static void TestMonitorTransientLoss()
        {
            using (TestWorkspace workspace = TestWorkspace.WithCompleteSave())
            {
                SnapshotStore store = workspace.CreateStore();
                GuardianMonitor monitor = new GuardianMonitor(
                    store,
                    new FakeGameProbe { Running = true },
                    workspace.Clock)
                {
                    StabilityDelay = TimeSpan.FromSeconds(3)
                };
                monitor.SetProfile(workspace.Profile);
                monitor.Poll();
                workspace.Clock.Advance(TimeSpan.FromSeconds(4));
                monitor.Poll();

                string markerPath = SaveLayout.CombineUnderProfile(
                    workspace.Profile,
                    SaveLayout.MarkerPath);
                byte[] marker = File.ReadAllBytes(markerPath);
                DateTime markerWriteUtc = File.GetLastWriteTimeUtc(markerPath);
                File.Delete(markerPath);
                monitor.Poll();
                workspace.Clock.Advance(TimeSpan.FromSeconds(1));
                Directory.CreateDirectory(Path.GetDirectoryName(markerPath));
                File.WriteAllBytes(markerPath, marker);
                File.SetLastWriteTimeUtc(markerPath, markerWriteUtc);
                MonitorStatus recovered = monitor.Poll();
                Assert.False(recovered.SuspectedRunEnded, "transient loss must not be classified as end");
            }
        }

        private static void TestWarmReloadExactTarget()
        {
            byte[] target = Enumerable.Repeat((byte)0x11, 64).ToArray();
            byte[] original = Enumerable.Repeat((byte)0x77, 64).ToArray();
            Result<WarmReloadLineageEvidence> result = WarmReloadLineageAnalyzer.Analyze(
                "042EFF030080000000000010",
                "042EFF030080000000000020",
                "042EFF030080000000000010",
                target,
                original,
                (byte[])target.Clone());

            Assert.True(result.Success, result.Message);
            Assert.Equal(WarmReloadClassifications.TargetGenerationExact, result.Value.Classification);
            Assert.True(result.Value.TargetLineageSupported, "exact target was not accepted");
            Assert.True(result.Value.ExactTargetObserved, "exact target flag was not set");
            Assert.Equal(0L, result.Value.TargetByteDistance);
        }

        private static void TestWarmReloadTargetDescendant()
        {
            byte[] target = Enumerable.Repeat((byte)0x11, 64).ToArray();
            byte[] original = Enumerable.Repeat((byte)0x77, 64).ToArray();
            byte[] observed = (byte[])target.Clone();
            observed[5] ^= 0x01;
            observed[43] ^= 0x02;
            Result<WarmReloadLineageEvidence> result = WarmReloadLineageAnalyzer.Analyze(
                "042EFF030080000000000010",
                "042EFF030080000000000020",
                "042EFF030080000000000012",
                target,
                original,
                observed);

            Assert.True(result.Success, result.Message);
            Assert.Equal(WarmReloadClassifications.TargetLineageCloser, result.Value.Classification);
            Assert.True(result.Value.TargetLineageSupported, "closer target descendant was not recognized");
            Assert.False(result.Value.ExactTargetObserved, "mutated descendant was called exact");
            Assert.Equal(2L, result.Value.TargetByteDistance);
            Assert.True(
                result.Value.OriginalByteDistance > result.Value.TargetByteDistance,
                "target descendant was not closer to target bytes");
        }

        private static void TestWarmReloadCachedOriginal()
        {
            byte[] target = Enumerable.Repeat((byte)0x11, 64).ToArray();
            byte[] original = Enumerable.Repeat((byte)0x77, 64).ToArray();
            Result<WarmReloadLineageEvidence> result = WarmReloadLineageAnalyzer.Analyze(
                "042EFF030080000000000010",
                "042EFF030080000000000020",
                "042EFF030080000000000020",
                target,
                original,
                (byte[])original.Clone());

            Assert.True(result.Success, result.Message);
            Assert.Equal(WarmReloadClassifications.CachedOriginalReasserted, result.Value.Classification);
            Assert.False(result.Value.TargetLineageSupported, "cached original was accepted as target");
        }

        private static void TestWarmReloadOriginalCounter()
        {
            byte[] target = Enumerable.Repeat((byte)0x11, 64).ToArray();
            byte[] original = Enumerable.Repeat((byte)0x77, 64).ToArray();
            byte[] observed = (byte[])target.Clone();
            observed[5] ^= 0x01;
            Result<WarmReloadLineageEvidence> result = WarmReloadLineageAnalyzer.Analyze(
                "042EFF030080000000000010",
                "042EFF030080000000000020",
                "042EFF030080000000000020",
                target,
                original,
                observed);

            Assert.True(result.Success, result.Message);
            Assert.Equal(WarmReloadClassifications.TargetCounterNotRestored, result.Value.Classification);
            Assert.False(result.Value.TargetLineageSupported, "original-generation counter was accepted");
        }

        private static void TestWarmReloadSemanticMismatch()
        {
            byte[] target = Enumerable.Repeat((byte)0x11, 64).ToArray();
            byte[] original = Enumerable.Repeat((byte)0x77, 64).ToArray();
            Result<WarmReloadLineageEvidence> identity = WarmReloadLineageAnalyzer.Analyze(
                "042EFF030080000000000010",
                "042EFF030080000000000020",
                "142EFF030080000000000011",
                target,
                original,
                (byte[])target.Clone());
            Result<WarmReloadLineageEvidence> encounter = WarmReloadLineageAnalyzer.Analyze(
                "042EFF030080000000000010",
                "042EFF030080000000000020",
                "042EFF030080000100000011",
                target,
                original,
                (byte[])target.Clone());

            Assert.Equal(WarmReloadClassifications.UnexpectedRunIdentity, identity.Value.Classification);
            Assert.False(identity.Value.TargetLineageSupported, "different run identity was accepted");
            Assert.Equal(WarmReloadClassifications.NonHideoutState, encounter.Value.Classification);
            Assert.False(encounter.Value.TargetLineageSupported, "encounter state was accepted");
        }

        private static void TestWarmReloadAmbiguous()
        {
            byte[] target = Enumerable.Repeat((byte)0x00, 8).ToArray();
            byte[] original = Enumerable.Repeat((byte)0xFF, 8).ToArray();
            byte[] observed = { 0x00, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF };
            Result<WarmReloadLineageEvidence> result = WarmReloadLineageAnalyzer.Analyze(
                "042EFF030080000000000010",
                "042EFF030080000000000020",
                "042EFF030080000000000012",
                target,
                original,
                observed);

            Assert.True(result.Success, result.Message);
            Assert.Equal(WarmReloadClassifications.AmbiguousGeneration, result.Value.Classification);
            Assert.False(result.Value.TargetLineageSupported, "ambiguous payload was accepted");
            Assert.Equal(result.Value.TargetByteDistance, result.Value.OriginalByteDistance);
        }

        private static void TestWarmRestoreStageAndRecover()
        {
            using (TestWorkspace workspace = TestWorkspace.WithCompleteSave())
            {
                SnapshotStore store = workspace.CreateStore();
                SnapshotRecord target = CaptureManual(store, workspace.Profile);
                SyntheticSave.WriteComplete(workspace.Profile, "20244:9:0", 9);
                SyntheticSave.MakeNativeExportOnly(workspace.Profile);
                Dictionary<string, byte[]> original = SyntheticSave.ReadScope(workspace.Profile);
                FakeWarmProcessRuntime runtime = new FakeWarmProcessRuntime();
                bool writeWasSuspended = false;
                WarmRestoreController controller = new WarmRestoreController(
                    store,
                    workspace.Clock,
                    runtime,
                    new NoWaitWarmRestoreDelay())
                {
                    StabilityDelay = TimeSpan.Zero,
                    Hooks = new WarmRestoreHooks
                    {
                        BeforeTargetWrite = () => writeWasSuspended = runtime.LastSession != null
                            && runtime.LastSession.Suspended
                    }
                };

                Result<WarmRestoreOutcome> staged = controller.Stage(
                    workspace.Profile,
                    target.Manifest.Id);
                Assert.True(staged.Success, staged.Message);
                Assert.True(writeWasSuspended, "target write began before process suspension");
                Assert.True(controller.HasActiveOperation, "warm journal was not retained");

                runtime.Running = false;
                Result<WarmRestoreOutcome> recovered = controller.RecoverAfterFullExit(
                    workspace.Profile);
                Assert.True(recovered.Success, recovered.Message);
                SyntheticSave.AssertScopeEquals(workspace.Profile, original);
                Assert.False(controller.HasActiveOperation, "recovery left an active journal");
            }
        }

        private static void TestWarmRestoreUnknownLiveIcon()
        {
            using (TestWorkspace workspace = TestWorkspace.WithCompleteSave())
            {
                SnapshotStore store = workspace.CreateStore();
                SnapshotRecord target = CaptureManual(store, workspace.Profile);
                SyntheticSave.WriteComplete(workspace.Profile, "20244:9:0", 9);
                SyntheticSave.MakeNativeExportOnly(workspace.Profile);
                byte[] unknownIcon = { 0xD0, 0x15, 0xCC, 0x83 };
                File.WriteAllBytes(
                    SaveLayout.CombineUnderProfile(workspace.Profile, SaveLayout.RunIconPath),
                    unknownIcon);

                FakeWarmProcessRuntime runtime = new FakeWarmProcessRuntime();
                WarmRestoreController controller = NewWarmRestoreController(
                    workspace,
                    store,
                    runtime);
                Result<WarmRestoreOutcome> staged = controller.Stage(
                    workspace.Profile,
                    target.Manifest.Id);
                Assert.True(staged.Success, staged.Message);

                runtime.Running = false;
                Result<WarmRestoreOutcome> recovered = controller.RecoverAfterFullExit(
                    workspace.Profile);
                Assert.True(recovered.Success, recovered.Message);
                Assert.BytesEqual(
                    unknownIcon,
                    File.ReadAllBytes(SaveLayout.CombineUnderProfile(
                        workspace.Profile,
                        SaveLayout.RunIconPath)));
            }
        }

        private static void TestWarmRestoreRestartFlow()
        {
            using (TestWorkspace workspace = TestWorkspace.WithCompleteSave())
            {
                SnapshotStore store = workspace.CreateStore();
                SnapshotRecord target = CaptureManual(store, workspace.Profile);
                SyntheticSave.WriteComplete(workspace.Profile, "20244:9:0", 9);
                SyntheticSave.MakeNativeExportOnly(workspace.Profile);
                FakeWarmProcessRuntime runtime = new FakeWarmProcessRuntime();
                WarmRestoreController first = NewWarmRestoreController(
                    workspace,
                    store,
                    runtime);
                Assert.True(
                    first.Stage(workspace.Profile, target.Manifest.Id).Success,
                    "stage failed before restart");

                WarmRestoreController afterStage = NewWarmRestoreController(
                    workspace,
                    store,
                    runtime);
                Result<WarmRestoreOutcome> checkpoint = afterStage.CheckpointMainMenu(
                    workspace.Profile);
                Assert.True(checkpoint.Success, checkpoint.Message);

                WarmRestoreController afterCheckpoint = NewWarmRestoreController(
                    workspace,
                    store,
                    runtime);
                Result<WarmRestoreOutcome> verified = afterCheckpoint.VerifyHideout(
                    workspace.Profile);
                Assert.True(verified.Success, verified.Message);

                SyntheticSave.WriteComplete(workspace.Profile, "20244:5:0", 5);
                WarmRestoreController afterHideout = NewWarmRestoreController(
                    workspace,
                    store,
                    runtime);
                Result<WarmRestoreOutcome> accepted = afterHideout.AcceptTradingStation(
                    workspace.Profile);
                Assert.True(accepted.Success, accepted.Message);
                Assert.False(afterHideout.HasActiveOperation, "accepted warm restore remained active");
                Assert.Equal(
                    WarmRestorePhases.Accepted,
                    accepted.Value.Journal.State);
            }
        }

        private static void TestWarmRestoreStageGates()
        {
            using (TestWorkspace workspace = TestWorkspace.WithCompleteSave())
            {
                SnapshotStore store = workspace.CreateStore();
                SnapshotRecord target = CaptureManual(store, workspace.Profile);
                SyntheticSave.WriteComplete(workspace.Profile, "20244:9:0", 9);
                SyntheticSave.MakeNativeExportOnly(workspace.Profile);
                Dictionary<string, byte[]> original = SyntheticSave.ReadScope(workspace.Profile);
                FakeWarmProcessRuntime wrongBuild = new FakeWarmProcessRuntime
                {
                    BuildHash = "BAD-BUILD"
                };
                WarmRestoreController controller = NewWarmRestoreController(
                    workspace,
                    store,
                    wrongBuild);
                Result<WarmRestoreOutcome> result = controller.Stage(
                    workspace.Profile,
                    target.Manifest.Id);
                Assert.False(result.Success, "unverified build passed warm staging");
                Assert.Equal("game_build_unrecognized", result.Code);
                SyntheticSave.AssertScopeEquals(workspace.Profile, original);

                SyntheticSave.RestoreOneWorkingRunFile(workspace.Profile);
                FakeWarmProcessRuntime runtime = new FakeWarmProcessRuntime();
                controller = NewWarmRestoreController(workspace, store, runtime);
                result = controller.Stage(workspace.Profile, target.Manifest.Id);
                Assert.False(result.Success, "partial working envelope passed warm staging");
                Assert.Equal("warm_restore_source_partial_working_envelope", result.Code);
            }
        }

        private static void TestWarmRestoreRejectionEvidence()
        {
            using (TestWorkspace workspace = TestWorkspace.WithCompleteSave())
            {
                SnapshotStore store = workspace.CreateStore();
                SnapshotRecord target = CaptureManual(store, workspace.Profile);
                SyntheticSave.WriteComplete(workspace.Profile, "20244:9:0", 9);
                SyntheticSave.MakeNativeExportOnly(workspace.Profile);
                FakeWarmProcessRuntime runtime = new FakeWarmProcessRuntime();
                WarmRestoreController controller = NewWarmRestoreController(
                    workspace,
                    store,
                    runtime);
                Assert.True(
                    controller.Stage(workspace.Profile, target.Manifest.Id).Success,
                    "stage failed before cached-original rejection");
                Assert.True(
                    controller.CheckpointMainMenu(workspace.Profile).Success,
                    "checkpoint failed before cached-original rejection");
                SyntheticSave.WriteComplete(workspace.Profile, "20244:9:0", 9);
                Result<WarmRestoreOutcome> rejected = controller.VerifyHideout(workspace.Profile);
                Assert.False(rejected.Success, "cached original was accepted as target lineage");
                Assert.Equal("warm_restore_rejected", rejected.Code);
                Result<WarmRestoreJournal> journal = controller.GetActiveOperation(
                    workspace.Profile);
                Assert.True(journal.Success, journal.Message);
                Assert.Equal(WarmRestorePhases.Rejected, journal.Value.State);
            }

            using (TestWorkspace workspace = TestWorkspace.WithCompleteSave())
            {
                SnapshotStore store = workspace.CreateStore();
                SnapshotRecord target = CaptureManual(store, workspace.Profile);
                SyntheticSave.WriteComplete(workspace.Profile, "20244:9:0", 9);
                SyntheticSave.MakeNativeExportOnly(workspace.Profile);
                FakeWarmProcessRuntime runtime = new FakeWarmProcessRuntime();
                WarmRestoreController controller = NewWarmRestoreController(
                    workspace,
                    store,
                    runtime);
                Assert.True(
                    controller.Stage(workspace.Profile, target.Manifest.Id).Success,
                    "stage failed before encounter rejection");
                Assert.True(
                    controller.CheckpointMainMenu(workspace.Profile).Success,
                    "checkpoint failed before encounter rejection");
                SyntheticSave.WriteEncounter(workspace.Profile, "20244:5:0", 5);
                Result<WarmRestoreOutcome> rejected = controller.VerifyHideout(workspace.Profile);
                Assert.False(rejected.Success, "encounter state passed hideout verification");
                Assert.Equal("warm_restore_rejected", rejected.Code);
            }
        }

        private static void TestWarmNavigationRoute()
        {
            FakeWarmProcessRuntime runtime = new FakeWarmProcessRuntime();
            FakeWarmNavigationInput input = new FakeWarmNavigationInput();
            FakeWarmCreditsProbe credits = new FakeWarmCreditsProbe(false, true);
            WarmRestoreNavigator navigator = NewWarmNavigator(runtime, input, credits);

            Result<bool> result = navigator.OpenT2RCredits();
            Assert.True(result.Success, result.Message);
            Assert.Equal("warm_navigation_credits_reached", result.Code);
            Assert.Equal(1, input.FocusCalls);

            List<WarmRestoreNavigationKey> expected = new List<WarmRestoreNavigationKey>();
            expected.AddRange(Enumerable.Repeat(WarmRestoreNavigationKey.Up, 12));
            expected.AddRange(Enumerable.Repeat(WarmRestoreNavigationKey.Down, 3));
            expected.Add(WarmRestoreNavigationKey.Enter);
            expected.AddRange(Enumerable.Repeat(WarmRestoreNavigationKey.Down, 16));
            expected.Add(WarmRestoreNavigationKey.Enter);
            expected.AddRange(Enumerable.Repeat(WarmRestoreNavigationKey.Up, 4));
            expected.Add(WarmRestoreNavigationKey.Enter);
            Assert.True(
                expected.SequenceEqual(input.Keys),
                "warm navigation input route changed from the verified widget order");
        }

        private static void TestWarmNavigationInputAbi()
        {
            int expectedInputSize = IntPtr.Size == 8 ? 40 : 28;
            Assert.Equal(
                expectedInputSize,
                Marshal.SizeOf(typeof(WarmNavigationNative.INPUT)));
        }

        private static void TestWarmNavigationFocusGate()
        {
            FakeWarmProcessRuntime runtime = new FakeWarmProcessRuntime();
            FakeWarmNavigationInput input = new FakeWarmNavigationInput
            {
                FocusResult = Result<bool>.Fail(
                    "warm_navigation_focus_failed",
                    "focus denied")
            };
            FakeWarmCreditsProbe credits = new FakeWarmCreditsProbe(false);
            WarmRestoreNavigator navigator = NewWarmNavigator(runtime, input, credits);

            Result<bool> result = navigator.OpenT2RCredits();
            Assert.False(result.Success, "focus failure still navigated the game menu");
            Assert.Equal("warm_navigation_focus_failed", result.Code);
            Assert.Equal(0, input.Keys.Count);
        }

        private static void TestWarmNavigationDryRun()
        {
            FakeWarmNavigator navigator = new FakeWarmNavigator();
            WarmRestoreNavigationDryRun dryRun =
                new WarmRestoreNavigationDryRun(navigator);

            Result<bool> result = dryRun.RunRoundTrip();
            Assert.True(result.Success, result.Message);
            Assert.Equal("warm_navigation_round_trip_complete", result.Code);
            Assert.Equal("open,return", string.Join(",", navigator.Calls));
        }

        private static void TestWarmAutomationPreflightGate()
        {
            using (TestWorkspace workspace = TestWorkspace.WithCompleteSave())
            {
                SnapshotStore store = workspace.CreateStore();
                SnapshotRecord target = CaptureManual(store, workspace.Profile);
                SyntheticSave.WriteComplete(workspace.Profile, "20244:9:0", 9);
                Dictionary<string, byte[]> original = SyntheticSave.ReadScope(workspace.Profile);
                FakeWarmProcessRuntime runtime = new FakeWarmProcessRuntime();
                WarmRestoreController controller = NewWarmRestoreController(
                    workspace,
                    store,
                    runtime);
                FakeWarmNavigator navigator = new FakeWarmNavigator();
                WarmRestoreAutomationController automation =
                    new WarmRestoreAutomationController(controller, navigator);

                Result<WarmRestoreOutcome> result = automation.StartFromMainMenu(
                    workspace.Profile,
                    target.Manifest.Id);
                Assert.False(result.Success, "invalid preflight still navigated the game menu");
                Assert.Equal(0, navigator.Calls.Count);
                Assert.False(controller.HasActiveOperation, "preflight failure created a warm journal");
                SyntheticSave.AssertScopeEquals(workspace.Profile, original);
            }
        }

        private static void TestWarmAutomationArrivalGate()
        {
            using (TestWorkspace workspace = TestWorkspace.WithCompleteSave())
            {
                SnapshotStore store = workspace.CreateStore();
                SnapshotRecord target = CaptureManual(store, workspace.Profile);
                SyntheticSave.WriteComplete(workspace.Profile, "20244:9:0", 9);
                SyntheticSave.MakeNativeExportOnly(workspace.Profile);
                Dictionary<string, byte[]> original = SyntheticSave.ReadScope(workspace.Profile);
                FakeWarmProcessRuntime runtime = new FakeWarmProcessRuntime();
                WarmRestoreController controller = NewWarmRestoreController(
                    workspace,
                    store,
                    runtime);
                FakeWarmNavigator navigator = new FakeWarmNavigator
                {
                    OpenResult = Result<bool>.Fail(
                        "warm_navigation_credits_not_reached",
                        "credits not proven")
                };
                WarmRestoreAutomationController automation =
                    new WarmRestoreAutomationController(controller, navigator);

                Result<WarmRestoreOutcome> result = automation.StartFromMainMenu(
                    workspace.Profile,
                    target.Manifest.Id);
                Assert.False(result.Success, "unproven credits arrival staged a target");
                Assert.Equal("warm_navigation_credits_not_reached", result.Code);
                Assert.False(controller.HasActiveOperation, "arrival failure created a warm journal");
                SyntheticSave.AssertScopeEquals(workspace.Profile, original);
            }
        }

        private static void TestWarmAutomationCompleteRoute()
        {
            using (TestWorkspace workspace = TestWorkspace.WithCompleteSave())
            {
                SnapshotStore store = workspace.CreateStore();
                SnapshotRecord target = CaptureManual(store, workspace.Profile);
                SyntheticSave.WriteComplete(workspace.Profile, "20244:9:0", 9);
                SyntheticSave.MakeNativeExportOnly(workspace.Profile);
                FakeWarmProcessRuntime runtime = new FakeWarmProcessRuntime();
                WarmRestoreController controller = NewWarmRestoreController(
                    workspace,
                    store,
                    runtime);
                FakeWarmNavigator navigator = new FakeWarmNavigator();
                WarmRestoreAutomationController automation =
                    new WarmRestoreAutomationController(controller, navigator);

                Result<WarmRestoreOutcome> result = automation.StartFromMainMenu(
                    workspace.Profile,
                    target.Manifest.Id);
                Assert.True(result.Success, result.Message);
                Assert.Equal("open,return", string.Join(",", navigator.Calls));
                Assert.Equal(WarmRestorePhases.MainMenuConfirmed, result.Value.Journal.State);
                Assert.True(controller.HasActiveOperation, "checkpoint lost the active warm workflow");
            }
        }

        private static void TestWarmAutomationReusesPreflightEvidence()
        {
            using (TestWorkspace workspace = TestWorkspace.WithCompleteSave())
            {
                SnapshotStore store = workspace.CreateStore();
                SnapshotRecord target = CaptureManual(store, workspace.Profile);
                SyntheticSave.WriteComplete(workspace.Profile, "20244:9:0", 9);
                SyntheticSave.MakeNativeExportOnly(workspace.Profile);
                FakeWarmProcessRuntime runtime = new FakeWarmProcessRuntime();
                RecordingWarmRestoreDelay delay = new RecordingWarmRestoreDelay();
                WarmRestoreController controller = new WarmRestoreController(
                    store,
                    workspace.Clock,
                    runtime,
                    delay)
                {
                    StabilityDelay = TimeSpan.FromMilliseconds(2500)
                };
                WarmRestoreAutomationController automation =
                    new WarmRestoreAutomationController(
                        controller,
                        new FakeWarmNavigator());

                Result<WarmRestoreOutcome> result = automation.StartFromMainMenu(
                    workspace.Profile,
                    target.Manifest.Id);
                Assert.True(result.Success, result.Message);
                Assert.Equal(
                    2,
                    delay.Durations.Count(duration =>
                        duration == TimeSpan.FromMilliseconds(2500)));
                Assert.Equal(
                    1,
                    delay.Durations.Count(duration =>
                        duration == TimeSpan.FromMilliseconds(2000)));
            }
        }

        private static void TestWarmNavigationCreditsExit()
        {
            FakeWarmProcessRuntime runtime = new FakeWarmProcessRuntime();
            FakeWarmNavigationInput input = new FakeWarmNavigationInput();
            FakeWarmCreditsProbe confirmation = new FakeWarmCreditsProbe(true, true, false);
            WarmRestoreNavigator navigator = NewWarmNavigator(runtime, input, confirmation);

            Result<bool> result = navigator.ReturnFromT2RCredits();
            Assert.True(result.Success, result.Message);
            WarmRestoreNavigationKey[] expected =
            {
                WarmRestoreNavigationKey.Escape,
                WarmRestoreNavigationKey.Left,
                WarmRestoreNavigationKey.Left,
                WarmRestoreNavigationKey.Left,
                WarmRestoreNavigationKey.Left,
                WarmRestoreNavigationKey.Enter
            };
            Assert.True(
                expected.SequenceEqual(input.Keys),
                "credits confirmation route sent unexpected input");

            input = new FakeWarmNavigationInput();
            FakeWarmCreditsProbe direct = new FakeWarmCreditsProbe(true, false);
            navigator = NewWarmNavigator(runtime, input, direct);
            result = navigator.ReturnFromT2RCredits();
            Assert.True(result.Success, result.Message);
            Assert.True(
                new[] { WarmRestoreNavigationKey.Escape }.SequenceEqual(input.Keys),
                "direct credits exit sent stray input at the main menu");
        }

        private static void TestWindowsCreditsResourceProbe()
        {
            using (TestWorkspace workspace = new TestWorkspace())
            {
                string executable = Path.Combine(workspace.Root, "tlou-ii.exe");
                string movie = Path.Combine(
                    workspace.Root,
                    "build",
                    "pc",
                    "main",
                    "movie1",
                    "cin-end-credits-t2r.bk2");
                Directory.CreateDirectory(Path.GetDirectoryName(movie));
                File.WriteAllBytes(executable, new byte[] { 1 });
                File.WriteAllBytes(movie, new byte[] { 2, 3, 4 });
                WindowsWarmRestoreCreditsProbe probe =
                    new WindowsWarmRestoreCreditsProbe();

                using (FileStream stream = new FileStream(
                    movie,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read))
                {
                    Result<bool> open = probe.IsT2RCreditsOpen(
                        executable,
                        new List<int> { Process.GetCurrentProcess().Id });
                    Assert.True(open.Success, open.Message);
                    Assert.True(open.Value, "Restart Manager missed the process owning the credits resource");
                }

                Result<bool> closed = probe.IsT2RCreditsOpen(
                    executable,
                    new List<int> { Process.GetCurrentProcess().Id });
                Assert.True(closed.Success, closed.Message);
                Assert.False(closed.Value, "released credits resource remained attributed to the process");
            }
        }

        private static WarmRestoreNavigator NewWarmNavigator(
            FakeWarmProcessRuntime runtime,
            FakeWarmNavigationInput input,
            FakeWarmCreditsProbe credits)
        {
            return new WarmRestoreNavigator(
                runtime,
                input,
                credits,
                new NoWaitWarmRestoreDelay())
            {
                KeyDelay = TimeSpan.Zero,
                FocusDelay = TimeSpan.Zero,
                PageDelay = TimeSpan.Zero,
                ConfirmationDelay = TimeSpan.Zero,
                ProbeDelay = TimeSpan.Zero,
                ArrivalTimeout = TimeSpan.Zero,
                ExitTimeout = TimeSpan.Zero
            };
        }

        private static WarmRestoreController NewWarmRestoreController(
            TestWorkspace workspace,
            SnapshotStore store,
            FakeWarmProcessRuntime runtime)
        {
            return new WarmRestoreController(
                store,
                workspace.Clock,
                runtime,
                new NoWaitWarmRestoreDelay())
            {
                StabilityDelay = TimeSpan.Zero
            };
        }

        private static SnapshotRecord CaptureManual(SnapshotStore store, string profile)
        {
            Result<SnapshotRecord> result = store.Capture(
                profile,
                new CaptureOptions
                {
                    Kind = SnapshotKinds.Manual,
                    Purpose = SnapshotPurposes.Preparation,
                    UserConfirmedPreparation = true,
                    Label = "manual",
                    SkipDeduplication = true
                });
            Assert.True(result.Success, result.Message);
            return result.Value;
        }

        private static void RewriteSnapshotKind(SnapshotRecord record, string kind)
        {
            string manifestPath = Path.Combine(record.DirectoryPath, "manifest.json");
            SnapshotManifest manifest = FileTools.ReadJson<SnapshotManifest>(manifestPath);
            manifest.Kind = kind;
            manifest.ManifestSha256 = FileTools.ManifestHash(manifest);
            FileTools.WriteJsonAtomic(manifestPath, manifest);
        }

        private static void RehashSnapshotFile(
            SnapshotRecord record,
            string relativePath)
        {
            string payload = SnapshotPayload(record, relativePath);
            string manifestPath = Path.Combine(record.DirectoryPath, "manifest.json");
            SnapshotManifest manifest = FileTools.ReadJson<SnapshotManifest>(manifestPath);
            SnapshotFileEntry entry = manifest.Files.First(item => string.Equals(
                item.RelativePath,
                relativePath,
                StringComparison.OrdinalIgnoreCase));
            entry.Length = new FileInfo(payload).Length;
            entry.Sha256 = FileTools.Sha256(payload, false);
            manifest.CompositeSha256 = FileTools.CompositeHash(manifest.Files);
            manifest.ManifestSha256 = FileTools.ManifestHash(manifest);
            FileTools.WriteJsonAtomic(manifestPath, manifest);
        }

        private static void RewriteSnapshotWorkingStateWord(
            SnapshotRecord record,
            string stateWord)
        {
            foreach (string relativePath in new[]
            {
                SaveLayout.GameRunPath,
                SaveLayout.GameRunBackupPath
            })
            {
                SyntheticSave.RewriteWorkingStateWordFile(
                    SnapshotPayload(record, relativePath),
                    stateWord);
            }

            string manifestPath = Path.Combine(record.DirectoryPath, "manifest.json");
            SnapshotManifest manifest = FileTools.ReadJson<SnapshotManifest>(manifestPath);
            foreach (SnapshotFileEntry entry in manifest.Files.Where(item =>
                string.Equals(item.RelativePath, SaveLayout.GameRunPath, StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    item.RelativePath,
                    SaveLayout.GameRunBackupPath,
                    StringComparison.OrdinalIgnoreCase)))
            {
                entry.Sha256 = FileTools.Sha256(
                    SnapshotPayload(record, entry.RelativePath),
                    false);
            }

            manifest.CompositeSha256 = FileTools.CompositeHash(manifest.Files);
            manifest.ManifestSha256 = FileTools.ManifestHash(manifest);
            FileTools.WriteJsonAtomic(manifestPath, manifest);
        }

        private static string SnapshotPayload(SnapshotRecord record, string relativePath)
        {
            return Path.Combine(
                record.DirectoryPath,
                "payload",
                relativePath.Replace('/', Path.DirectorySeparatorChar));
        }

        private sealed class TestCase
        {
            public TestCase(string name, Action body)
            {
                Name = name;
                Body = body;
            }

            public string Name { get; private set; }
            public Action Body { get; private set; }
        }
    }

    internal sealed class NoWaitWarmRestoreDelay : IWarmRestoreDelay
    {
        public void Wait(TimeSpan duration)
        {
        }
    }

    internal sealed class RecordingWarmRestoreDelay : IWarmRestoreDelay
    {
        public RecordingWarmRestoreDelay()
        {
            Durations = new List<TimeSpan>();
        }

        public List<TimeSpan> Durations { get; private set; }

        public void Wait(TimeSpan duration)
        {
            Durations.Add(duration);
        }
    }

    internal sealed class FakeWarmProcessRuntime : IWarmRestoreProcessRuntime
    {
        public bool Running { get; set; } = true;
        public string BuildHash { get; set; } = WindowsWarmRestoreProcessRuntime.VerifiedGameSha256;
        public FakeWarmProcessSession LastSession { get; private set; }

        public bool IsGameRunning()
        {
            return Running;
        }

        public Result<IWarmRestoreProcessSession> OpenValidatedSession()
        {
            if (!Running)
            {
                return Result<IWarmRestoreProcessSession>.Fail(
                    "game_not_running",
                    "The fake game process is not running.");
            }

            if (!string.Equals(
                BuildHash,
                WindowsWarmRestoreProcessRuntime.VerifiedGameSha256,
                StringComparison.OrdinalIgnoreCase))
            {
                return Result<IWarmRestoreProcessSession>.Fail(
                    "game_build_unrecognized",
                    "The fake game build is not verified.");
            }

            LastSession = new FakeWarmProcessSession(BuildHash);
            return Result<IWarmRestoreProcessSession>.Ok(
                LastSession,
                "game_build_verified",
                "Fake warm reload process verified.");
        }
    }

    internal sealed class FakeWarmNavigationInput : IWarmRestoreNavigationInput
    {
        public FakeWarmNavigationInput()
        {
            FocusResult = Result<bool>.Ok(
                true,
                "warm_navigation_game_focused",
                "fake focus");
            Keys = new List<WarmRestoreNavigationKey>();
        }

        public Result<bool> FocusResult { get; set; }
        public int FocusCalls { get; private set; }
        public List<WarmRestoreNavigationKey> Keys { get; private set; }

        public Result<bool> FocusGame(IList<int> processIds)
        {
            FocusCalls++;
            return FocusResult;
        }

        public Result<bool> SendKey(
            IList<int> processIds,
            WarmRestoreNavigationKey key)
        {
            Keys.Add(key);
            return Result<bool>.Ok(true, "warm_navigation_input_sent", "fake input");
        }
    }

    internal sealed class FakeWarmCreditsProbe : IWarmRestoreCreditsProbe
    {
        private readonly Queue<bool> _states;
        private bool _last;

        public FakeWarmCreditsProbe(params bool[] states)
        {
            _states = new Queue<bool>(states ?? new bool[0]);
            _last = _states.Count > 0 ? _states.Peek() : false;
        }

        public Result<bool> IsT2RCreditsOpen(
            string gameExecutablePath,
            IList<int> processIds)
        {
            if (_states.Count > 0)
            {
                _last = _states.Dequeue();
            }

            return Result<bool>.Ok(
                _last,
                _last ? "warm_navigation_credits_open" : "warm_navigation_credits_closed",
                "fake credits state");
        }
    }

    internal sealed class FakeWarmNavigator : IWarmRestoreNavigator
    {
        public FakeWarmNavigator()
        {
            OpenResult = Result<bool>.Ok(
                true,
                "warm_navigation_credits_reached",
                "fake credits reached");
            ReturnResult = Result<bool>.Ok(
                true,
                "warm_navigation_main_menu_reached",
                "fake main menu reached");
            Calls = new List<string>();
        }

        public Result<bool> OpenResult { get; set; }
        public Result<bool> ReturnResult { get; set; }
        public List<string> Calls { get; private set; }

        public Result<bool> OpenT2RCredits()
        {
            Calls.Add("open");
            return OpenResult;
        }

        public Result<bool> ReturnFromT2RCredits()
        {
            Calls.Add("return");
            return ReturnResult;
        }
    }

    internal sealed class FakeWarmProcessSession : IWarmRestoreProcessSession
    {
        public FakeWarmProcessSession(string executableSha256)
        {
            ExecutablePath = "fake-tlou-ii.exe";
            ExecutableSha256 = executableSha256;
            ProcessIds = new List<int> { 101, 202 };
        }

        public string ExecutablePath { get; private set; }
        public string ExecutableSha256 { get; private set; }
        public IList<int> ProcessIds { get; private set; }
        public bool Suspended { get; private set; }

        public Result<bool> Suspend()
        {
            Suspended = true;
            return Result<bool>.Ok(true, "game_suspended", "Fake game suspended.");
        }

        public Result<bool> Resume()
        {
            Suspended = false;
            return Result<bool>.Ok(true, "game_resumed", "Fake game resumed.");
        }

        public void Dispose()
        {
        }
    }

    internal sealed class TestWorkspace : IDisposable
    {
        public TestWorkspace()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "NoReturnGuardianTests",
                Guid.NewGuid().ToString("N"));
            Documents = Path.Combine(Root, "Documents");
            Profile = Path.Combine(
                Documents,
                "The Last of Us Part II",
                "76561198000000000");
            Storage = Path.Combine(Root, "GuardianStorage");
            Directory.CreateDirectory(Profile);
            Clock = new FakeClock(new DateTime(2026, 7, 29, 12, 0, 0, DateTimeKind.Utc));
        }

        public string Root { get; private set; }
        public string Documents { get; private set; }
        public string Profile { get; private set; }
        public string Storage { get; private set; }
        public FakeClock Clock { get; private set; }

        public static TestWorkspace WithCompleteSave()
        {
            TestWorkspace workspace = new TestWorkspace();
            SyntheticSave.WriteComplete(workspace.Profile, "20244:1:0", 3);
            return workspace;
        }

        public SnapshotStore CreateStore()
        {
            return new SnapshotStore(Storage, Clock)
            {
                AutomaticRetention = 60
            };
        }

        public void Dispose()
        {
            string fullRoot = Path.GetFullPath(Root);
            string expectedParent = Path.GetFullPath(Path.Combine(
                Path.GetTempPath(),
                "NoReturnGuardianTests"));
            if (fullRoot.StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(fullRoot))
            {
                Directory.Delete(fullRoot, true);
            }
        }
    }

    internal static class SyntheticSave
    {
        private const int WorkingStateOffset = 1526;

        public static void WriteComplete(string profile, string marker, byte seed)
        {
            WriteState(profile, marker, seed, 3, false);
        }

        public static void WriteEncounter(string profile, string marker, byte seed)
        {
            WriteState(profile, marker, seed, 3, true);
        }

        public static void WriteHideoutTransition(string profile, string marker, byte seed)
        {
            WriteState(profile, marker, seed, 1, false);
        }

        public static void MakeNativeExportOnly(string profile)
        {
            foreach (string relativePath in new[]
            {
                SaveLayout.GameRunPath,
                SaveLayout.GameRunBackupPath,
                SaveLayout.GameProfilePath,
                SaveLayout.GameProfileBackupPath
            })
            {
                File.Delete(SaveLayout.CombineUnderProfile(profile, relativePath));
            }
        }

        public static void CorruptNativeRunPayload(string profile)
        {
            CorruptFileByte(
                SaveLayout.CombineUnderProfile(profile, SaveLayout.RunDataPath),
                2048);
        }

        public static void CorruptNativeRunFooter(string profile)
        {
            string path = SaveLayout.CombineUnderProfile(profile, SaveLayout.RunDataPath);
            CorruptFileByte(path, (int)new FileInfo(path).Length - 1);
        }

        public static void UseNativeLegacyRunFooter(string profile)
        {
            string dataPath = SaveLayout.CombineUnderProfile(profile, SaveLayout.RunDataPath);
            Result<DecodedSlotPayload> decoded = RunSlotSynthesizer.DecodeNativeExport(
                dataPath,
                SaveLayout.CombineUnderProfile(profile, SaveLayout.RunParamsPath),
                SaveLayout.CombineUnderProfile(profile, SaveLayout.RunIconPath));
            if (!decoded.Success)
            {
                throw new InvalidOperationException(decoded.Message);
            }

            byte[] footer =
            {
                0x01, 0x01, 0x01, 0x01, 0x05, 0x00, 0x00, 0x00,
                0x05, 0x02, 0x00, 0x00, 0x06, 0x00, 0x00, 0x00,
                0x00, 0xC0, 0x44, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x30, 0xC0, 0x44, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00
            };
            byte[] exported;
            string detail;
            string error;
            if (!RunSlotSynthesizer.TryBuildObservedExport(
                decoded.Value.RawBytes,
                footer,
                out exported,
                out detail,
                out error))
            {
                throw new InvalidOperationException(error);
            }

            DateTime writeUtc = File.GetLastWriteTimeUtc(dataPath);
            File.WriteAllBytes(dataPath, exported);
            File.SetLastWriteTimeUtc(dataPath, writeUtc);
        }

        public static void CorruptNativeProfileFooter(string profile)
        {
            string path = SaveLayout.CombineUnderProfile(
                profile,
                SaveLayout.ProfileDataPath);
            CorruptFileByte(path, (int)new FileInfo(path).Length - 1);
        }

        public static void MakeNativeProfileGenerationOlder(
            string profile,
            TimeSpan age)
        {
            DateTime reference = File.GetLastWriteTimeUtc(
                SaveLayout.CombineUnderProfile(profile, SaveLayout.RunDataPath));
            foreach (string relativePath in new[]
            {
                SaveLayout.ProfileDataPath,
                SaveLayout.ProfileParamsPath
            })
            {
                File.SetLastWriteTimeUtc(
                    SaveLayout.CombineUnderProfile(profile, relativePath),
                    reference - age);
            }
        }

        public static void RestoreOneWorkingRunFile(string profile)
        {
            Result<DecodedSlotPayload> decoded = RunSlotSynthesizer.DecodeNativeExport(
                SaveLayout.CombineUnderProfile(profile, SaveLayout.RunDataPath),
                SaveLayout.CombineUnderProfile(profile, SaveLayout.RunParamsPath),
                SaveLayout.CombineUnderProfile(profile, SaveLayout.RunIconPath));
            if (!decoded.Success)
            {
                throw new InvalidOperationException(decoded.Message);
            }

            WriteFile(profile, SaveLayout.GameRunPath, decoded.Value.RawBytes);
        }

        public static Dictionary<string, byte[]> ReadNativeSources(string profile)
        {
            Dictionary<string, byte[]> result = new Dictionary<string, byte[]>(
                StringComparer.OrdinalIgnoreCase);
            foreach (string relativePath in SaveLayout.NativeExportSourceFiles)
            {
                result[relativePath] = File.ReadAllBytes(
                    SaveLayout.CombineUnderProfile(profile, relativePath));
            }

            return result;
        }

        public static void AssertNativeSourcesEqual(
            string profile,
            IDictionary<string, byte[]> expected)
        {
            foreach (string relativePath in SaveLayout.NativeExportSourceFiles)
            {
                Assert.True(expected.ContainsKey(relativePath), relativePath + " missing");
                Assert.BytesEqual(
                    expected[relativePath],
                    File.ReadAllBytes(SaveLayout.CombineUnderProfile(profile, relativePath)));
            }
        }

        public static void WriteEmptySlot(string profile, string marker, byte seed)
        {
            WriteState(profile, marker, seed, 3, false);
            MakeSlotExportStaleAndEmpty(profile, seed);
        }

        public static void MakeWorkingStateWordUnknown(string profile)
        {
            foreach (string relativePath in new[]
            {
                SaveLayout.GameRunPath,
                SaveLayout.GameRunBackupPath
            })
            {
                RewriteWorkingStateWordFile(
                    SaveLayout.CombineUnderProfile(profile, relativePath),
                    "DEADBEEF");
            }
        }

        public static void MakeWorkingHeaderUnknown(string profile)
        {
            foreach (string relativePath in new[]
            {
                SaveLayout.GameRunPath,
                SaveLayout.GameRunBackupPath
            })
            {
                string path = SaveLayout.CombineUnderProfile(profile, relativePath);
                DateTime writeUtc = File.GetLastWriteTimeUtc(path);
                byte[] bytes = File.ReadAllBytes(path);
                bytes[0] = 0x95;
                File.WriteAllBytes(path, bytes);
                File.SetLastWriteTimeUtc(path, writeUtc);
            }
        }

        public static void RewriteWorkingStateWordFile(string path, string stateWord)
        {
            if (string.IsNullOrWhiteSpace(stateWord) || stateWord.Length != 8)
            {
                throw new ArgumentException("Working state word must contain eight characters.");
            }

            DateTime writeUtc = File.GetLastWriteTimeUtc(path);
            byte[] bytes = File.ReadAllBytes(path);
            byte[] state = Encoding.ASCII.GetBytes(stateWord);
            Array.Copy(state, 0, bytes, WorkingStateOffset + 9, state.Length);
            File.WriteAllBytes(path, bytes);
            File.SetLastWriteTimeUtc(path, writeUtc);
        }

        public static void MakeSlotExportStaleAndEmpty(string profile, byte seed)
        {
            int staleRawLength = 7600 + seed * 16;
            DateTime staleUtc = File.GetLastWriteTimeUtc(
                SaveLayout.CombineUnderProfile(profile, SaveLayout.GameRunPath))
                .AddMinutes(-20);
            WriteFile(
                profile,
                SaveLayout.RunDataPath,
                Pattern(staleRawLength + SaveLayout.SlotEnvelopeLength, seed, 31));
            WriteFile(
                profile,
                SaveLayout.RunParamsPath,
                Encoding.UTF8.GetBytes(
                    "{\"title\":\"The Last of Us Part II\",\"subTitle\":\"Auto Save\","
                    + "\"detail\":\"new run data\\n\\n["
                    + SaveLayout.EmptyRunStateCode
                    + "]\",\"userParam\":1,\"mtime\":0}"));
            WriteFile(profile, SaveLayout.RunIconPath, new byte[] { 0x01, 0x82, 0x9C, 0x01 });

            foreach (string relativePath in new[]
            {
                SaveLayout.RunDataPath,
                SaveLayout.RunParamsPath,
                SaveLayout.RunIconPath
            })
            {
                File.SetLastWriteTimeUtc(
                    SaveLayout.CombineUnderProfile(profile, relativePath),
                    staleUtc);
            }
        }

        public static void MakeMarkerGenerationOlder(string profile, TimeSpan age)
        {
            string dataPath = SaveLayout.CombineUnderProfile(profile, SaveLayout.RunDataPath);
            string markerPath = SaveLayout.CombineUnderProfile(profile, SaveLayout.MarkerPath);
            File.SetLastWriteTimeUtc(markerPath, File.GetLastWriteTimeUtc(dataPath) - age);
        }

        public static void MakeWorkingCopyShapeMismatch(string profile)
        {
            string path = SaveLayout.CombineUnderProfile(profile, SaveLayout.GameRunPath);
            byte[] current = File.ReadAllBytes(path);
            byte[] changed = new byte[current.Length + 113];
            Array.Copy(current, changed, current.Length);
            File.WriteAllBytes(path, changed);
            File.SetLastWriteTimeUtc(
                path,
                File.GetLastWriteTimeUtc(
                    SaveLayout.CombineUnderProfile(profile, SaveLayout.RunDataPath)));
        }

        public static void MakeWorkingMirrorContentMismatch(string profile)
        {
            string path = SaveLayout.CombineUnderProfile(
                profile,
                SaveLayout.GameRunBackupPath);
            byte[] changed = File.ReadAllBytes(path);
            changed[changed.Length / 2] ^= 0x5A;
            DateTime writeUtc = File.GetLastWriteTimeUtc(path);
            File.WriteAllBytes(path, changed);
            File.SetLastWriteTimeUtc(path, writeUtc);
        }

        public static void TouchBackupWitness(
            string profile,
            int index,
            TimeSpan offset)
        {
            string path = Path.Combine(
                profile,
                "savedata",
                "backup",
                "USR-DATA.R0A_bak" + index);
            File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path) + offset);
        }

        public static void RewriteBackupStateWord(
            string profile,
            int index,
            string stateWord)
        {
            RewriteWorkingStateWordFile(
                Path.Combine(
                    profile,
                    "savedata",
                    "backup",
                    "USR-DATA.R0A_bak" + index),
                stateWord);
        }

        public static Dictionary<string, byte[]> ReadScope(string profile)
        {
            Dictionary<string, byte[]> result = new Dictionary<string, byte[]>(
                StringComparer.OrdinalIgnoreCase);
            foreach (string relativePath in SaveLayout.RequiredFiles)
            {
                string fullPath = SaveLayout.CombineUnderProfile(profile, relativePath);
                if (File.Exists(fullPath))
                {
                    result[relativePath] = File.ReadAllBytes(fullPath);
                }
            }

            return result;
        }

        public static void AssertScopeEquals(
            string profile,
            Dictionary<string, byte[]> expected)
        {
            foreach (string relativePath in SaveLayout.RequiredFiles)
            {
                string fullPath = SaveLayout.CombineUnderProfile(profile, relativePath);
                byte[] bytes;
                if (!expected.TryGetValue(relativePath, out bytes))
                {
                    Assert.False(File.Exists(fullPath), relativePath + " should be absent");
                    continue;
                }

                Assert.True(File.Exists(fullPath), relativePath + " should exist");
                Assert.BytesEqual(bytes, File.ReadAllBytes(fullPath));
            }
        }

        private static void WriteFile(string profile, string relativePath, byte[] bytes)
        {
            string path = SaveLayout.CombineUnderProfile(profile, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, bytes);
        }

        private static void WriteState(
            string profile,
            string marker,
            byte seed,
            int matchingBackupCount,
            bool encounter)
        {
            int rawLength = 8192 + seed * 16;
            byte[] rawRun = Pattern(rawLength, seed, 17);
            EmbedWorkingState(rawRun, seed, encounter);
            byte[] profileData = Pattern(2048 + seed, (byte)(seed + 3), 11);
            string profileDetail = "\n[0F0000000000000000000000]";
            EmbedSlotDetail(profileData, profileDetail);
            DateTime commitUtc = new DateTime(2026, 7, 29, 8, 0, 0, DateTimeKind.Utc)
                .AddMinutes(seed);

            WriteFile(profile, SaveLayout.MarkerPath, Encoding.ASCII.GetBytes(marker + "\0"));
            WriteFile(
                profile,
                SaveLayout.RunParamsPath,
                Encoding.UTF8.GetBytes(
                    "{\"title\":\"The Last of Us Part II\",\"subTitle\":\"Auto Save\","
                    + "\"detail\":\"template\","
                    + "\"userParam\":1,\"mtime\":0}"));
            WriteFile(profile, SaveLayout.RunIconPath, new byte[] { 0x01, 0x82, 0x9C, 0x01 });
            WriteFile(profile, SaveLayout.GameRunPath, rawRun);
            WriteFile(profile, SaveLayout.GameRunBackupPath, rawRun);
            WriteFile(profile, SaveLayout.GameProfilePath, profileData);
            WriteFile(profile, SaveLayout.GameProfileBackupPath, profileData);

            string generatedIcon = SaveLayout.CombineUnderProfile(
                profile,
                SaveLayout.RunIconPath) + ".generated";
            string runDataPath = SaveLayout.CombineUnderProfile(profile, SaveLayout.RunDataPath);
            if (File.Exists(runDataPath))
            {
                File.Delete(runDataPath);
            }

            Result<RunSlotMetadata> synthesis = RunSlotSynthesizer.Synthesize(
                SaveLayout.CombineUnderProfile(profile, SaveLayout.GameRunPath),
                SaveLayout.CombineUnderProfile(profile, SaveLayout.RunParamsPath),
                runDataPath,
                SaveLayout.CombineUnderProfile(profile, SaveLayout.RunParamsPath),
                generatedIcon,
                WorkingStateCode(seed, encounter));
            if (!synthesis.Success)
            {
                throw new InvalidOperationException(synthesis.Message);
            }

            File.Delete(SaveLayout.CombineUnderProfile(profile, SaveLayout.RunIconPath));
            File.Move(generatedIcon, SaveLayout.CombineUnderProfile(profile, SaveLayout.RunIconPath));

            bool createdProfileSlot = !File.Exists(
                SaveLayout.CombineUnderProfile(profile, SaveLayout.ProfileDataPath));
            if (createdProfileSlot)
            {
                Result<byte[]> profileExport = ProfileSlotCodec.BuildExport(profileData);
                if (!profileExport.Success)
                {
                    throw new InvalidOperationException(profileExport.Message);
                }

                WriteFile(profile, SaveLayout.ProfileDataPath, profileExport.Value);
                WriteFile(
                    profile,
                    SaveLayout.ProfileParamsPath,
                    Encoding.UTF8.GetBytes(
                        "{\"title\":\"The Last of Us Part II\",\"subTitle\":\"Profile\"," +
                        "\"detail\":\"\\n[0F0000000000000000000000]\"," +
                        "\"userParam\":1,\"mtime\":0}"));
                WriteFile(
                    profile,
                    SaveLayout.ProfileIconPath,
                    new byte[] { 0x14, 0x4D, 0xB7, 0x5B });
            }

            foreach (string relativePath in SaveLayout.RequiredFiles)
            {
                File.SetLastWriteTimeUtc(
                    SaveLayout.CombineUnderProfile(profile, relativePath),
                    commitUtc);
            }

            foreach (string relativePath in createdProfileSlot
                ? SaveLayout.ProfileSlotFiles
                : new string[0])
            {
                File.SetLastWriteTimeUtc(
                    SaveLayout.CombineUnderProfile(profile, relativePath),
                    commitUtc);
            }

            string backupRoot = Path.Combine(profile, "savedata", "backup");
            Directory.CreateDirectory(backupRoot);
            foreach (string existing in Directory.GetFiles(
                backupRoot,
                "USR-DATA.R0A_bak*",
                SearchOption.TopDirectoryOnly))
            {
                File.Delete(existing);
            }

            for (int index = 0; index < matchingBackupCount; index++)
            {
                string path = Path.Combine(backupRoot, "USR-DATA.R0A_bak" + index);
                File.WriteAllBytes(path, rawRun);
                File.SetLastWriteTimeUtc(path, commitUtc.AddSeconds(-index * 5));
            }

            for (int index = matchingBackupCount; index < 3; index++)
            {
                string path = Path.Combine(backupRoot, "USR-DATA.R0A_bak" + index);
                File.WriteAllBytes(path, Pattern(rawLength + 257, (byte)(seed + index), 7));
                File.SetLastWriteTimeUtc(path, commitUtc.AddSeconds(-index * 5));
            }
        }

        private static string WorkingStateCode(byte seed, bool encounter)
        {
            return "032EFF03"
                + (encounter ? "00800001" : "00800000")
                + ((uint)seed).ToString("X8");
        }

        private static void EmbedWorkingState(byte[] bytes, byte seed, bool encounter)
        {
            bytes[0] = 0x93;
            bytes[1] = 0xEA;
            for (int index = 1424; index < 1444; index++)
            {
                bytes[index] = 0;
            }

            byte[] payloadLength = BitConverter.GetBytes((uint)(bytes.Length - 1424));
            Array.Copy(payloadLength, 0, bytes, 1428, payloadLength.Length);

            string prefix = "synthetic preparation ".PadRight(WorkingStateOffset - 1444, '.');
            string detail = prefix + "[" + WorkingStateCode(seed, encounter) + "]";
            byte[] encoded = Encoding.UTF8.GetBytes(detail);
            Array.Copy(encoded, 0, bytes, 1444, encoded.Length);
            bytes[1444 + encoded.Length] = 0;
        }

        private static void EmbedSlotDetail(byte[] bytes, string detail)
        {
            bytes[0] = 0x93;
            bytes[1] = 0xEA;
            for (int index = 1424; index < 1444; index++)
            {
                bytes[index] = 0;
            }

            byte[] payloadLength = BitConverter.GetBytes((uint)(bytes.Length - 1424));
            Array.Copy(payloadLength, 0, bytes, 1428, payloadLength.Length);
            byte[] encoded = Encoding.UTF8.GetBytes(detail);
            Array.Copy(encoded, 0, bytes, 1444, encoded.Length);
            bytes[1444 + encoded.Length] = 0;
        }

        private static void CorruptFileByte(string path, int offset)
        {
            DateTime writeUtc = File.GetLastWriteTimeUtc(path);
            byte[] bytes = File.ReadAllBytes(path);
            bytes[offset] ^= 0x5A;
            File.WriteAllBytes(path, bytes);
            File.SetLastWriteTimeUtc(path, writeUtc.AddSeconds(1));
        }

        private static byte[] Pattern(int length, byte seed, int stride)
        {
            byte[] bytes = new byte[length];
            for (int index = 0; index < bytes.Length; index++)
            {
                bytes[index] = (byte)(seed + index * stride);
            }

            return bytes;
        }
    }

    internal sealed class FakeClock : IClock
    {
        public FakeClock(DateTime utcNow)
        {
            UtcNow = utcNow;
        }

        public DateTime UtcNow { get; private set; }

        public void Advance(TimeSpan value)
        {
            UtcNow = UtcNow.Add(value);
        }
    }

    internal sealed class FakeGameProbe : IGameProcessProbe
    {
        public bool Running { get; set; }
        public string ExecutablePath { get; set; }

        public bool IsGameRunning()
        {
            return Running;
        }

        public string FindGameExecutablePath()
        {
            return Running ? ExecutablePath : null;
        }
    }

    internal static class Assert
    {
        public static void True(bool value, string message)
        {
            if (!value)
            {
                throw new InvalidOperationException(message);
            }
        }

        public static void False(bool value, string message)
        {
            True(!value, message);
        }

        public static void Equal<T>(T expected, T actual)
        {
            if (!object.Equals(expected, actual))
            {
                throw new InvalidOperationException(
                    "Expected [" + expected + "] but found [" + actual + "].");
            }
        }

        public static void BytesEqual(byte[] expected, byte[] actual)
        {
            if (expected == null || actual == null || !expected.SequenceEqual(actual))
            {
                throw new InvalidOperationException("Byte sequences differ.");
            }
        }
    }
}
