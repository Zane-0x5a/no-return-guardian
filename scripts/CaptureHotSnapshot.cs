using System;
using System.Threading;
using NoReturnGuardian.Core;

internal static class CaptureHotSnapshot
{
    private static int Main(string[] args)
    {
        if (args.Length != 3)
        {
            Console.Error.WriteLine(
                "Usage: CaptureHotSnapshot <storage-root> <profile-path> <label>");
            return 2;
        }

        Result<RunStateDescriptor> first = RunStateProbe.Inspect(args[1], true);
        if (!Ready(first, "FIRST"))
        {
            return 3;
        }

        Thread.Sleep(2500);
        Result<RunStateDescriptor> second = RunStateProbe.Inspect(args[1], true);
        if (!Ready(second, "SECOND"))
        {
            return 4;
        }

        if (!first.Value.SameCaptureState(second.Value))
        {
            Console.Error.WriteLine("UNSTABLE live state changed between probes");
            return 5;
        }

        SnapshotStore store = new SnapshotStore(args[0], new SystemClock());
        Result<SnapshotRecord> capture = store.CaptureHotPreparation(args[1], args[2], true);
        if (!capture.Success)
        {
            Console.Error.WriteLine(
                "CAPTURE_FAILED code={0} message={1}",
                capture.Code,
                capture.Message);
            return 6;
        }

        Result<SnapshotRecord> verification = store.VerifySnapshot(
            capture.Value.Manifest.Id,
            true);
        if (!verification.Success)
        {
            Console.Error.WriteLine(
                "VERIFY_FAILED code={0} message={1}",
                verification.Code,
                verification.Message);
            return 7;
        }

        SnapshotManifest manifest = verification.Value.Manifest;
        Console.WriteLine(
            "CAPTURED id={0} schema={1} state={2} witnesses={3} files={4} composite={5}",
            manifest.Id,
            manifest.SchemaVersion,
            manifest.RunStateCode,
            manifest.MatchingBackupCount,
            manifest.Files.Count,
            manifest.CompositeSha256);
        return 0;
    }

    private static bool Ready(Result<RunStateDescriptor> result, string label)
    {
        if (!result.Success || !result.Value.ManualCaptureReady)
        {
            Console.Error.WriteLine(
                "{0}_NOT_READY code={1} status={2} message={3}",
                label,
                result.Code,
                result.Success ? result.Value.ManualCaptureStatus : null,
                result.Message);
            return false;
        }

        Console.WriteLine(
            "{0}_READY state={1} basis={2} witnesses={3} skew_ms={4}",
            label,
            result.Value.WorkingRunStateCode,
            result.Value.ManualCaptureBasis,
            result.Value.MatchingBackupCount,
            result.Value.NativeExportCaptureReady
                ? result.Value.NativeExportWriteSkewMs
                : result.Value.CoreWriteSkewMs);
        return true;
    }
}
