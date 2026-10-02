using System;
using System.Text;
using System.Web.Script.Serialization;
using NoReturnGuardian.Core;

internal static class UndoSnapshot
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        if (args.Length != 3)
        {
            Console.Error.WriteLine("Usage: UndoSnapshot <storage-root> <profile-path> <pre-restore-snapshot-id>");
            return 2;
        }

        SnapshotStore store = new SnapshotStore(args[0], new SystemClock());
        if (store.HasPendingRestore())
        {
            Console.Error.WriteLine("An existing restore journal must be recovered first.");
            return 3;
        }
        Result<RestoreOutcome> result = store.Undo(args[2], args[1], new GameProcessProbe());
        if (!result.Success)
        {
            Console.Error.WriteLine("UNDO_FAILED code={0} message={1}", result.Code, result.Message);
            return 4;
        }
        Console.WriteLine(new JavaScriptSerializer().Serialize(new
        {
            restoredSnapshotId = result.Value.RestoredSnapshotId,
            undoSnapshotId = result.Value.UndoSnapshotId,
            diskRollbackVerified = true,
            inGameAcceptance = false
        }));
        return 0;
    }
}
