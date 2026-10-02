using System;
using NoReturnGuardian.Core;

internal static class RestoreSnapshot
{
    private static int Main(string[] args)
    {
        if (args.Length != 3)
        {
            Console.Error.WriteLine(
                "Usage: RestoreSnapshot <storage-root> <profile-path> <snapshot-id>");
            return 2;
        }

        string storageRoot = args[0];
        string profilePath = args[1];
        string snapshotId = args[2];
        SnapshotStore store = new SnapshotStore(storageRoot, new SystemClock());
        GameProcessProbe gameProbe = new GameProcessProbe();

        Result<SnapshotRecord> verification = store.VerifySnapshot(snapshotId, true);
        if (!verification.Success)
        {
            Console.Error.WriteLine(
                "VERIFY_FAILED code={0} message={1}",
                verification.Code,
                verification.Message);
            return 3;
        }

        SnapshotManifest manifest = verification.Value.Manifest;
        Console.WriteLine(
            "VERIFIED id={0} files={1} composite={2}",
            manifest.Id,
            manifest.Files.Count,
            manifest.CompositeSha256);

        Result<RestoreOutcome> restore = store.Restore(
            snapshotId,
            profilePath,
            gameProbe,
            new RestoreHooks
            {
                AfterFileApplied = (path, count) => Console.WriteLine(
                    "APPLIED index={0} path={1}",
                    count,
                    path)
            });
        if (!restore.Success)
        {
            Console.Error.WriteLine(
                "RESTORE_FAILED code={0} message={1}",
                restore.Code,
                restore.Message);
            return 4;
        }

        Console.WriteLine(
            "RESTORED id={0} undo={1} rolled_back={2}",
            restore.Value.RestoredSnapshotId,
            restore.Value.UndoSnapshotId,
            restore.Value.RolledBackAfterFailure);
        return 0;
    }
}
