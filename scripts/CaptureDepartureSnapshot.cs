using System;
using System.Globalization;
using System.Text;
using System.Web.Script.Serialization;
using NoReturnGuardian.Core;

/// <summary>
/// Called by the departure recorder once it has proved that its frozen copy is the last hideout save the
/// game finished before the route board's departure. Publishes that copy as a verified preparation only
/// through the ordinary hot-capture gates.
/// </summary>
internal static class CaptureDepartureSnapshot
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        int routeIndex;
        long observedMs;
        if (args.Length != 6
            || !int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out routeIndex)
            || !long.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out observedMs))
        {
            Console.Error.WriteLine(
                "Usage: CaptureDepartureSnapshot <storage-root> <profile-path> <route-index> <observed-unix-ms> <r0a-sha256> <staged-directory>");
            return 2;
        }

        SnapshotStore store = new SnapshotStore(args[0], new SystemClock());
        if (store.HasPendingRestore())
        {
            Console.Error.WriteLine("An existing restore journal must be resolved first.");
            return 3;
        }

        Result<SnapshotRecord> result = store.CaptureDeparturePreparation(args[1], new DepartureEvidence
        {
            RouteIndex = routeIndex,
            ObservedUtc = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(observedMs),
            RunSha256 = args[4],
            StagedPath = args[5]
        });
        if (!result.Success)
        {
            Console.Error.WriteLine("CAPTURE_FAILED code={0} message={1}", result.Code, result.Message);
            return 4;
        }

        Result<SnapshotRecord> verified = store.VerifySnapshot(result.Value.Manifest.Id, true);
        if (!verified.Success || !SnapshotPolicy.IsConfirmedPreparation(verified.Value))
        {
            Console.Error.WriteLine("VERIFY_FAILED code={0} message={1}", verified.Code, verified.Message);
            return 5;
        }

        Console.WriteLine(new JavaScriptSerializer().Serialize(new
        {
            id = result.Value.Manifest.Id,
            kind = result.Value.Manifest.Kind,
            basis = result.Value.Manifest.DepartureBasis,
            runSha256 = result.Value.Manifest.DepartureRunSha256,
            routeIndex = result.Value.Manifest.DepartureRouteIndex,
            compositeSha256 = result.Value.Manifest.CompositeSha256,
            verified = true
        }));
        return 0;
    }
}
