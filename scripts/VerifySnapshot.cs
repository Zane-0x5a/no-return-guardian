using System;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using NoReturnGuardian.Core;

/// <summary>
/// The native scripts' formal check of a snapshot. Prints one JSON receipt of what it verified; the scripts
/// take the snapshot's use and every payload hash from this receipt and never read the manifest themselves,
/// so the bytes they load are bound to this verification.
/// </summary>
internal static class VerifySnapshot
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        if (args.Length != 2)
        {
            Console.Error.WriteLine(
                "Usage: VerifySnapshot <storage-root> <snapshot-id>");
            return 2;
        }

        SnapshotStore store = new SnapshotStore(args[0], new SystemClock());
        Result<SnapshotRecord> verification = store.VerifySnapshot(args[1], true);
        if (!verification.Success)
        {
            Console.Error.WriteLine(
                "VERIFY_FAILED code={0} message={1}",
                verification.Code,
                verification.Message);
            return 3;
        }

        SnapshotManifest manifest = verification.Value.Manifest;
        Console.WriteLine(new JavaScriptSerializer().Serialize(new
        {
            verified = true,
            id = manifest.Id,
            schema = manifest.SchemaVersion,
            purpose = manifest.Purpose,
            captureBasis = manifest.CaptureBasis,
            kind = manifest.Kind,
            runStateCode = manifest.RunStateCode,
            sourceProfilePath = manifest.SourceProfilePath,
            compositeSha256 = manifest.CompositeSha256,
            manifestSha256 = manifest.ManifestSha256,
            use = SnapshotPolicy.Use(verification.Value),
            files = manifest.Files.ToDictionary(
                file => SaveLayout.NormalizeRelativePath(file.RelativePath),
                file => new { length = file.Length, sha256 = file.Sha256 })
        }));
        return 0;
    }
}
