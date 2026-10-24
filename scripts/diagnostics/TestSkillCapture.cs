using System;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using NoReturnGuardian.Core;

// Offline evidence harness. It writes only the caller's disposable copy and Guardian test storage.
internal static class TestSkillCapture
{
    static int Main(string[] args)
    {
        try { return Run(args); }
        catch (Exception error) { Console.Error.WriteLine(error.GetType().FullName + ": " + error.Message); return 3; }
    }

    static int Run(string[] args)
    {
        string profile = Path.GetFullPath(args[0]);
        string storage = Path.GetFullPath(args[1]);
        var json = new JavaScriptSerializer();
        byte[] original = File.ReadAllBytes(SaveLayout.CombineUnderProfile(profile, SaveLayout.GameRunPath));
        if (args[3] == "native")
        {
            string data = SaveLayout.CombineUnderProfile(profile, SaveLayout.ProfileDataPath);
            byte[] raw = File.ReadAllBytes(SaveLayout.CombineUnderProfile(profile, SaveLayout.GameProfilePath));
            var exported = ProfileSlotCodec.BuildExport(raw);
            if (!exported.Success) throw new Exception(exported.Message);
            File.WriteAllBytes(data, exported.Value);
            var state = WorkingRunStateReader.Inspect(SaveLayout.CombineUnderProfile(profile, SaveLayout.GameRunPath));
            var synthesis = RunSlotSynthesizer.Synthesize(
                SaveLayout.CombineUnderProfile(profile, SaveLayout.GameRunPath),
                SaveLayout.CombineUnderProfile(profile, SaveLayout.RunParamsPath),
                SaveLayout.CombineUnderProfile(profile, SaveLayout.RunDataPath) + ".generated",
                SaveLayout.CombineUnderProfile(profile, SaveLayout.RunParamsPath) + ".generated",
                SaveLayout.CombineUnderProfile(profile, SaveLayout.RunIconPath) + ".generated", state.Value.Code);
            if (!synthesis.Success) throw new Exception(synthesis.Message);
            foreach (string path in SaveLayout.CommittedSlotFiles)
            {
                string target = SaveLayout.CombineUnderProfile(profile, path);
                File.Delete(target);
                File.Move(target + ".generated", target);
            }
            foreach (string path in new[] {SaveLayout.GameRunPath, SaveLayout.GameRunBackupPath,
                SaveLayout.GameProfilePath, SaveLayout.GameProfileBackupPath})
                File.Delete(SaveLayout.CombineUnderProfile(profile, path));
            foreach (string path in SaveLayout.NativeExportSourceFiles)
                File.SetLastWriteTimeUtc(SaveLayout.CombineUnderProfile(profile, path), DateTime.UtcNow.AddSeconds(-10));
        }
        var store = new SnapshotStore(storage, new SystemClock());
        store.SkillObservationReader = (selected, raw, boundary) => {
            var observation = json.Deserialize<SkillObservation>(File.ReadAllText(args[2]));
            observation.ProfilePath = selected;
            observation.OriginalRunSha256 = FileTools.Sha256(raw, false);
            observation.ObservedUtc = DateTime.UtcNow.ToString("O");
            return Result<SkillObservation>.Ok(observation, "test_observation", "Offline replay of the recorded boundary.");
        };
        var result = store.Capture(profile, new CaptureOptions {
            Kind = SnapshotKinds.Manual, Purpose = SnapshotPurposes.Preparation,
            UserConfirmedPreparation = true, SkipDeduplication = true });
        if (!result.Success) { Console.Error.WriteLine(result.Code + ": " + result.Message); return 1; }
        var verified = store.VerifySnapshot(result.Value.Manifest.Id, true);
        if (!verified.Success) { Console.Error.WriteLine(verified.Code + ": " + verified.Message); return 2; }
        Console.WriteLine(json.Serialize(new { id = result.Value.Manifest.Id, schema = result.Value.Manifest.SchemaVersion,
            use = SnapshotPolicy.Use(verified.Value), basis = result.Value.Manifest.CaptureBasis }));
        return 0;
    }
}
