using System;
using System.Text;
using System.Web.Script.Serialization;
using NoReturnGuardian.Core;

internal static class CaptureLiveSnapshot
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: CaptureLiveSnapshot <storage-root> <profile-path>");
            return 2;
        }

        SnapshotStore store = new SnapshotStore(args[0], new SystemClock());
        if (store.HasPendingRestore())
        {
            Console.Error.WriteLine("An existing restore journal must be resolved first.");
            return 3;
        }

        Result<SnapshotRecord> result = store.Capture(args[1], new CaptureOptions
        {
            Kind = SnapshotKinds.PreRestore,
            Purpose = SnapshotPurposes.LiveState,
            Label = "原生恢复前",
            AllowIncomplete = true,
            SkipDeduplication = true,
            SkipRetention = true
        });
        if (!result.Success)
        {
            Console.Error.WriteLine("CAPTURE_FAILED code={0} message={1}", result.Code, result.Message);
            return 4;
        }

        Result<SnapshotRecord> verified = store.VerifySnapshot(result.Value.Manifest.Id, false);
        if (!verified.Success)
        {
            Console.Error.WriteLine("VERIFY_FAILED code={0} message={1}", verified.Code, verified.Message);
            return 5;
        }

        Console.WriteLine(new JavaScriptSerializer().Serialize(new
        {
            id = result.Value.Manifest.Id,
            purpose = result.Value.Manifest.Purpose,
            compositeSha256 = result.Value.Manifest.CompositeSha256,
            verified = true,
            processMemoryUndo = false
        }));
        return 0;
    }
}
