using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static class TestNativeRecoveryDialog
{
    [STAThread]
    private static int Main(string[] args)
    {
        try { return Run(args); }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        var assembly = Assembly.LoadFrom(Path.GetFullPath(args[0]));
        var dialogType = assembly.GetType("NoReturnGuardian.NativeRecovery", true);
        Require(!typeof(Form).IsAssignableFrom(dialogType), "Native recovery holds no window");
        var quote = dialogType.GetMethod("Quote", BindingFlags.NonPublic | BindingFlags.Static);
        var verify = dialogType.GetMethod("VerifyMechanicalReceipt", BindingFlags.NonPublic | BindingFlags.Static);
        var serializer = new JavaScriptSerializer();
        string[] values = { "", "D:\\路径 with spaces\\", "a\"b", "backslash\\\"quote", "$(literal)", "`literal`" };
        var parameters = new[] { "-X", "utf8", "-c", "import json,sys; print(json.dumps(sys.argv[1:], ensure_ascii=False))" }.Concat(values);
        var start = new ProcessStartInfo(args[1], string.Join(" ", parameters.Select(value => (string)quote.Invoke(null, new object[] { value }))))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = Encoding.UTF8
        };
        using (var process = Process.Start(start))
        {
            var received = serializer.Deserialize<string[]>(process.StandardOutput.ReadToEnd());
            process.WaitForExit();
            Require(process.ExitCode == 0 && values.SequenceEqual(received), "Native Windows argv round-trip");
        }
        var root = Path.Combine(Path.GetTempPath(), "guardian-dialog-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var valid = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object> { {"kind", "host-preflight"}, {"pid", 11}, {"birth", 134336713473966430L}, {"mode", "recover-preparation"} },
                new Dictionary<string, object> { {"kind", "native-recovery-rebuilt"}, {"snapshotId", "target"}, {"inGameAcceptance", false} },
                new Dictionary<string, object> { {"kind", "host-detached"}, {"codeRestored", true} }
            };
            string log = Path.Combine(root, "recovery.jsonl");
            for (int scenario = 0; scenario < 9; scenario++)
            {
                var records = valid.Select(record => new Dictionary<string, object>(record)).ToList();
                if (scenario == 1) records[0]["birth"] = 22L;
                if (scenario == 2) records[0]["mode"] = "observe";
                if (scenario == 3) records[1]["snapshotId"] = "other";
                if (scenario == 4) records[1]["inGameAcceptance"] = true;
                if (scenario == 5) records.RemoveAt(2);
                if (scenario == 6) records[2]["codeRestored"] = false;
                if (scenario == 7) records.Add(records[1]);
                if (scenario == 8) records.Add(new Dictionary<string, object> { {"kind", "recovery-fatal"} });
                File.WriteAllLines(log, records.Select(record => serializer.Serialize(record)), new UTF8Encoding(false));
                bool accepted = true;
                try { verify.Invoke(null, new object[] { log, "target", 11, 134336713473966430L }); }
                catch (TargetInvocationException error)
                {
                    if (!(error.InnerException is InvalidOperationException)) throw;
                    accepted = false;
                }
                Require(accepted == (scenario == 0), "Mechanical receipt case " + scenario);
            }
            var entryType = dialogType.GetNestedType("RecoveryEntry", BindingFlags.NonPublic);
            var build = dialogType.GetMethod("RecoveryArguments", BindingFlags.NonPublic | BindingFlags.Static);
            foreach (var entry in new[] { new[] { "Menu", "menu" }, new[] { "Results", "results" },
                new[] { "MidEncounter", "mid-encounter" }, new[] { "Auto", "auto" } })
            {
                foreach (bool redeploy in new[] { false, true })
                {
                    var built = (string[])build.Invoke(null, new object[] { root, 11, log, root, root, "target",
                        Enum.Parse(entryType, entry[0]), redeploy, false });
                    Require(built.Count(value => value == "--entry") == 1
                        && built[built.Length - 2] == "--entry" && built.Last() == entry[1]
                        && !built.Any(value => value.StartsWith("--confirm-post-death-") || value == "--confirm-auto")
                        && built.Contains("--redeploy") == redeploy
                        && !built.Contains("--hideout-rebuilt-from-snapshot")
                        && built[Array.IndexOf(built, "--duration") + 1] == (redeploy ? "240" : "180"),
                        "Entry " + entry[0] + " redeploy " + redeploy);
                    // Guardian's assertion about the current hideout only travels with an automatic restart.
                    var vouched = (string[])build.Invoke(null, new object[] { root, 11, log, root, root, "target",
                        Enum.Parse(entryType, entry[0]), redeploy, true });
                    Require(vouched.Contains("--hideout-rebuilt-from-snapshot") == (redeploy && entry[0] == "Auto"),
                        "Hideout assertion for " + entry[0] + " redeploy " + redeploy);
                }
            }
            var outcomeType = dialogType.GetNestedType("RecoveryOutcome", BindingFlags.NonPublic);
            var describe = dialogType.GetMethod("DescribeOutcome", BindingFlags.Public | BindingFlags.Static);
            var outcome = Activator.CreateInstance(outcomeType);
            outcomeType.GetField("RedeployRequested").SetValue(outcome, true);
            Require(((string)describe.Invoke(null, new[] { outcome })).Contains("手动出发"), "Unfinished departure is reported");
            outcomeType.GetField("Redeployed").SetValue(outcome, true);
            Require(((string)describe.Invoke(null, new[] { outcome })).Contains("同一遭遇"), "Departure is reported");
            Require(!((string)describe.Invoke(null, new[] { outcome })).Contains("验收"), "Departure claims no player acceptance");
            outcomeType.GetField("FromHideout").SetValue(outcome, true);
            Require(((string)describe.Invoke(null, new[] { outcome })).Contains("从兵营出发"), "A departure from the hideout is reported");
            outcomeType.GetField("Redeployed").SetValue(outcome, false);
            Require(((string)describe.Invoke(null, new[] { outcome })).Contains("还在兵营"), "A lost departure from the hideout leaves the player there");
            var inHideout = dialogType.GetMethod("DescribeInHideout", BindingFlags.Public | BindingFlags.Static);
            Require(((string)inHideout.Invoke(null, new object[] { false, "Ctrl+Alt+F9" })).Contains("退回赴死之旅菜单")
                && ((string)inHideout.Invoke(null, new object[] { false, "Ctrl+Alt+F9" })).Contains("Ctrl+Alt+F9")
                && ((string)inHideout.Invoke(null, new object[] { true, null })).Contains("不是这份战备"), "In-hideout refusals explain the way out");
            var replay = dialogType.GetMethod("ReadHideoutReplay", BindingFlags.NonPublic | BindingFlags.Static);
            string replayLog = Path.Combine(root, "hideout.jsonl");
            Func<int, bool, bool, object> replayOutcome = (exitCode, arrived, restored) =>
            {
                var lines = new List<string> { serializer.Serialize(new Dictionary<string, object> {
                    {"kind", "host-preflight"}, {"mode", "hideout-redeploy"}, {"pid", 11}, {"birth", 134336713473966430L} }) };
                if (arrived) lines.Add(serializer.Serialize(new Dictionary<string, object> { {"kind", "redeploy-complete"}, {"sameEncounter", true} }));
                lines.Add(serializer.Serialize(new Dictionary<string, object> { {"kind", "host-detached"}, {"codeRestored", restored} }));
                File.WriteAllLines(replayLog, lines, new UTF8Encoding(false));
                try { return replay.Invoke(null, new object[] { replayLog, exitCode, "" }); }
                catch (TargetInvocationException error)
                {
                    if (!(error.InnerException is InvalidOperationException)) throw;
                    return null;
                }
            };
            var departed = replayOutcome(0, true, true);
            Require(departed != null && (bool)outcomeType.GetField("Redeployed").GetValue(departed)
                && (bool)outcomeType.GetField("FromHideout").GetValue(departed), "A departure from the hideout is read back");
            var stayed = replayOutcome(4, false, true);
            Require(stayed != null && !(bool)outcomeType.GetField("Redeployed").GetValue(stayed), "A lost departure is not a failure");
            Require(replayOutcome(0, false, true) == null && replayOutcome(3, false, false) == null && replayOutcome(4, false, false) == null,
                "A claimed departure without arrival, or an unproven hook removal, fails"); 
            var readRunStats = dialogType.GetMethod("ReadRunStatsOutcome", BindingFlags.NonPublic | BindingFlags.Static);
            string statsLog = Path.Combine(root, "run-stats.jsonl");
            Func<Dictionary<string, object>, object> runStatsOutcome = record =>
            {
                var lines = new List<string> { serializer.Serialize(new Dictionary<string, object> { {"kind", "host-preflight"} }) };
                if (record != null) lines.Add(serializer.Serialize(record));
                File.WriteAllLines(statsLog, lines, new UTF8Encoding(false));
                var read = Activator.CreateInstance(outcomeType);
                readRunStats.Invoke(null, new[] { statsLog, read });
                return read;
            };
            var restoredStats = runStatsOutcome(new Dictionary<string, object> { {"kind", "run-stats-restored"}, {"changed", 2} });
            Require((int?)outcomeType.GetField("RunStatsChanged").GetValue(restoredStats) == 2
                && outcomeType.GetField("RunStatsProblem").GetValue(restoredStats) == null
                && !((string)describe.Invoke(null, new[] { restoredStats })).Contains("注意"), "Restored run stats stay quiet");
            var rejectedStats = runStatsOutcome(new Dictionary<string, object> { {"kind", "run-stats-unrestored"},
                {"reason", "Native profile stat write was rejected"}, {"reverted", false} });
            string rejectedText = (string)describe.Invoke(null, new[] { rejectedStats });
            Require(outcomeType.GetField("RunStatsChanged").GetValue(rejectedStats) == null
                && rejectedText.Contains("已回到战备") && rejectedText.Contains("死信投递")
                && rejectedText.Contains("游戏拒绝了统计写入") && rejectedText.Contains("未能撤回"), "Unrestored run stats are reported");
            Require(((string)describe.Invoke(null, new[] { runStatsOutcome(new Dictionary<string, object> {
                {"kind", "run-stats-unrestored"}, {"reason", "Profile stat service identity mismatch"}, {"reverted", true} }) }))
                .Contains("没有调用"), "An unrecognized stat service is reported as never called");
            Require(outcomeType.GetField("RunStatsChanged").GetValue(runStatsOutcome(null)) == null
                && outcomeType.GetField("RunStatsProblem").GetValue(runStatsOutcome(null)) == null, "An older log claims nothing");
            var recorderStop = dialogType.GetMethod("DescribeRecorderStop", BindingFlags.Public | BindingFlags.Static);
            string recorderLog = Path.Combine(root, "departure.jsonl");
            var readProtected = dialogType.GetMethod("ReadProtectedDeparture", BindingFlags.Public | BindingFlags.Static);
            Func<string, bool, string> stopReason = (reason, protecting) =>
            {
                File.WriteAllLines(recorderLog, new[]
                {
                    serializer.Serialize(new Dictionary<string, object> { {"kind", "departure-recorder-start"} }),
                    serializer.Serialize(new Dictionary<string, object> { {"kind", "departure-recorder-stopped"}, {"reason", reason} }),
                    serializer.Serialize(new Dictionary<string, object> { {"kind", "departure-recorder-detached"}, {"codeRestored", true} })
                }, new UTF8Encoding(false));
                return (string)recorderStop.Invoke(null, new object[] { recorderLog, protecting });
            };
            Require(stopReason("guardian-stopped", false) == null && stopReason("guardian-stopped", true) == null,
                "Guardian's own recorder stop stays quiet");
            Require(stopReason("encounter-entered-without-capture", false).Contains("没有看到"), "A missed departure is explained");
            string unknown = stopReason("Second departure argument is not a boolean", false);
            Require(unknown.Contains("出错停止") && !unknown.Contains("boolean"), "An unknown failure is explained without the script's words");
            Require(stopReason("run-left", false) != null && new[] { "run-left", "game-exited", "hideout-not-reached", "no-departure-captured" }
                .All(reason => stopReason(reason, true) == null), "A protecting recorder's routine stops stay quiet");
            Require(stopReason("encounter-entered-without-capture", true).Contains("没有自动保存")
                && stopReason("departure-not-protected: no-hideout-save-before-departure", true).Contains("还没写过兵营存档")
                && stopReason("departure-not-protected: hideout-save-missed-before-departure", true).Contains("没来得及")
                && stopReason("departure-not-protected: departure_save_changed", true).Contains("核对没有通过")
                && !stopReason("departure-not-protected: departure_save_changed", true).Contains("departure_save_changed"),
                "A departure that was not protected says why, in the player's words");
            Require(new[] { "encounter-entered-without-capture", "departure-not-protected: newest-hideout-save-not-frozen",
                "departure-not-protected: departure-save-not-observed", "unexpected KeyError: 'slots'" }
                .All(reason => !stopReason(reason, true).Contains("诊断记录") && !stopReason(reason, true).Contains(root)),
                "Recorder stops carry no log paths");
            Require(readProtected.Invoke(null, new object[] { recorderLog }) == null, "No protection is read from a stop");
            File.AppendAllText(recorderLog, serializer.Serialize(new Dictionary<string, object>
                { {"kind", "departure-protected"}, {"snapshotId", "departure-new"} }) + Environment.NewLine, new UTF8Encoding(false));
            Require((string)readProtected.Invoke(null, new object[] { recorderLog }) == "departure-new", "The protected departure is read back");
            File.WriteAllText(recorderLog, "", new UTF8Encoding(false));
            Require(((string)recorderStop.Invoke(null, new object[] { recorderLog, false })).Contains("意外退出"), "A silent recorder exit is reported");
            Require(((string)recorderStop.Invoke(null, new object[] { Path.Combine(root, "missing.jsonl"), true })).Contains("不可读"),
                "A missing recorder log is reported");
            var results = dialogType.GetMethod("VerifyResultsReceipt", BindingFlags.NonPublic | BindingFlags.Static);
            string journal = Path.Combine(root, "recovery.results.jsonl");
            for (int scenario = 0; scenario < 5; scenario++)
            {
                var records = new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object> { {"kind", "results-continue-requested"}, {"pid", 11}, {"birth", 134336713473966430L} },
                    new Dictionary<string, object> { {"kind", "results-continue-detached"}, {"pid", 11}, {"birth", 134336713473966430L}, {"codeRestored", true} },
                    new Dictionary<string, object> { {"kind", "results-exit-complete"}, {"pid", 11}, {"birth", 134336713473966430L} }
                };
                if (scenario == 1) records[1]["codeRestored"] = false;
                if (scenario == 2) records.RemoveAt(2);
                if (scenario == 3) records.Add(new Dictionary<string, object> { {"kind", "results-exit-stopped"}, {"pid", 11}, {"birth", 134336713473966430L} });
                if (scenario == 4) records[0]["pid"] = 12;
                File.WriteAllLines(journal, records.Select(record => serializer.Serialize(record)), new UTF8Encoding(false));
                bool accepted = true;
                try { results.Invoke(null, new object[] { journal, 11, 134336713473966430L }); }
                catch (TargetInvocationException error)
                {
                    if (!(error.InnerException is InvalidOperationException)) throw;
                    accepted = false;
                }
                Require(accepted == (scenario == 0), "Results receipt case " + scenario);
            }
            Require((string)describe.Invoke(null, new[] { Activator.CreateInstance(outcomeType) }) == "已回到战备，可以继续游玩。",
                "A plain recovery reads as a player sentence");
            // 进程创建时间超出 JavaScript 的安全整数，原生脚本把它写成字符串；两种写法是同一个进程。
            File.WriteAllLines(journal, new[]
            {
                serializer.Serialize(new Dictionary<string, object> { {"kind", "results-continue-detached"}, {"pid", 11}, {"birth", "134336713473966430"}, {"codeRestored", true} }),
                serializer.Serialize(new Dictionary<string, object> { {"kind", "results-exit-complete"}, {"pid", 11}, {"birth", 134336713473966430L} })
            }, new UTF8Encoding(false));
            results.Invoke(null, new object[] { journal, 11, 134336713473966430L });
            var journalType = assembly.GetType("NoReturnGuardian.RecoveryJournal", true);
            File.WriteAllText(journal, "{\"kind\":\"results-exit-complete\"}\n{\"kind\":", new UTF8Encoding(false));
            try
            {
                journalType.GetMethod("Read").Invoke(null, new object[] { journal });
                Require(false, "A torn journal line is rejected");
            }
            catch (TargetInvocationException error)
            {
                Require(error.InnerException is InvalidDataException, "A torn journal line is rejected");
            }

            // 恢复没完成时，玩家看到的是游戏和存档现在的样子，不是脚本的原话。
            var describeFailure = dialogType.GetMethod("DescribeFailure", BindingFlags.NonPublic | BindingFlags.Static);
            string failureLog = Path.Combine(root, "failure.jsonl");
            Func<string, string[], string> failed = (stderr, supervisor) =>
            {
                string supervisorLog = Path.ChangeExtension(failureLog, ".supervisor.jsonl");
                if (supervisor == null) File.Delete(supervisorLog);
                else File.WriteAllLines(supervisorLog, supervisor, new UTF8Encoding(false));
                return (string)describeFailure.Invoke(null, new object[] { failureLog, stderr });
            };
            string exited = serializer.Serialize(new Dictionary<string, object> { {"kind", "supervisor-target-exited"} });
            string rolledBack = failed("EXPERIMENT_FAILED: worker failed", new[] { exited,
                serializer.Serialize(new Dictionary<string, object> { {"kind", "recovery-disk-rollback"}, {"required", true} }) });
            Require(rolledBack.Contains("关闭了游戏") && rolledBack.Contains("已还原"), "A closed game with a rolled back disk is reported");
            Require(failed("", new[] { exited, serializer.Serialize(new Dictionary<string, object> { {"kind", "recovery-disk-rollback"}, {"required", false} }) })
                .Contains("存档没有改动"), "A closed game before any write is reported");
            Require(failed("", new[] { exited, serializer.Serialize(new Dictionary<string, object> { {"kind", "recovery-disk-rollback-failed"} }) })
                .Contains("没能自动还原"), "A failed rollback asks the player to undo before playing");
            string place = failed("EXPERIMENT_FAILED: Results entry requires a visible death results page", null);
            Require(place.Contains("死亡结算页") && place.Contains("没有被改动") && !place.Contains("Results"), "A wrong screen is explained");
            Require(failed("EXPERIMENT_FAILED: Results did not advance after bounded native continue commands", null).Contains("手动走完结算"),
                "A stalled results page is explained");
            Require(failed("EXPERIMENT_FAILED: Game code differs from the verified build (code at 0x1b843a0); nothing was attached or changed", null)
                .Contains("验证过的版本"), "An unverified build is explained when recovery starts");
            string unexpected = failed("Traceback (most recent call last):\n  File x\nKeyError: 'slots'", null);
            Require(unexpected.Contains("恢复没有完成") && !unexpected.Contains("Traceback") && !unexpected.Contains("slots"),
                "An unexpected failure shows no traceback");

            // 游戏更新后代码不一致时，记录器在挂钩之前就停下；玩家看到的是原因，不是英文原话。
            File.WriteAllText(recorderLog, serializer.Serialize(new Dictionary<string, object> { { "kind", "departure-recorder-stopped" },
                { "reason", "Game code differs from the verified build (code at 0x1b843a0); nothing was attached or changed" } }),
                new UTF8Encoding(false));
            string unverified = (string)recorderStop.Invoke(null, new object[] { recorderLog, true });
            Require(unverified.Contains("验证过的版本") && unverified.Contains("没有被改动") && !unverified.Contains("Game code"),
                "An unverified build is explained to the player");

            // 界面文字：状态字与副行、失败原因的小字、操作结果。
            var view = assembly.GetType("NoReturnGuardian.GuardianView", true);
            var inputType = view.GetNestedType("StatusInput", BindingFlags.NonPublic);
            var statusType = view.GetNestedType("Status", BindingFlags.NonPublic);
            var describeStatus = view.GetMethod("DescribeStatus", BindingFlags.NonPublic | BindingFlags.Static);
            Func<Action<object>, string[]> status = configure =>
            {
                var input = Activator.CreateInstance(inputType);
                inputType.GetField("ProfileFound").SetValue(input, true);
                configure(input);
                var result = describeStatus.Invoke(null, new[] { input });
                return new[] { "Mode", "Headline", "Detail", "Blocked" }
                    .Select(field => (string)statusType.GetField(field).GetValue(result)).ToArray();
            };
            Action<object, string, object> set = (input, field, value) => inputType.GetField(field).SetValue(input, value);
            var guarding = status(input => { set(input, "GameRunning", true); set(input, "CaptureReady", true); set(input, "HasProtected", true); set(input, "Restorable", 3); });
            Require(guarding[0] == "guarding" && guarding[1] == "守护中" && guarding[2] == "游戏运行中 · 3 份可恢复", "Guarding status");
            var ended = status(input => { set(input, "SuspectedRunEnded", true); set(input, "HasProtected", true); });
            Require(ended[0] == "alert" && ended[1] == "征途疑似结束" && ended[2] == "选一份战备恢复", "A run end stays a suspicion");
            var endedRunning = status(input => { set(input, "SuspectedRunEnded", true); set(input, "HasProtected", true); set(input, "GameRunning", true); set(input, "NativeAvailable", true); });
            Require(endedRunning[2] == "可以回兵营或重开这场战斗", "A run end in game offers both actions");
            var pending = status(input => { set(input, "PendingRestore", true); set(input, "GameRunning", true); set(input, "CaptureReady", true); });
            Require(pending[0] == "alert" && pending[1] == "恢复未完成" && pending[3] != null, "An open restore journal outranks guarding");
            var noProfile = status(input => set(input, "ProfileFound", false));
            Require(noProfile[1] == "待机" && noProfile[2] == "没有找到游戏存档", "A missing profile is said plainly");
            var observing = status(input => set(input, "GameRunning", true));
            Require(observing[0] == "observing" && observing[2] == "游戏运行中 · 进入兵营后可保护", "Observing status");
            Require(new[] { guarding, ended, endedRunning, pending, noProfile, observing }
                .All(item => !string.Join("", item).Contains("写批") && !string.Join("", item).Contains("状态字")), "Status lines carry no diagnostics");

            var friendly = view.GetMethod("FriendlyMessage", BindingFlags.NonPublic | BindingFlags.Static);
            Require((string)friendly.Invoke(null, new object[] { "game_running", "x" }) == "游戏还在运行，请先完整退出游戏"
                && (string)friendly.Invoke(null, new object[] { "unknown_code", "The process cannot access the file." }) == "操作没有完成"
                && ((string)friendly.Invoke(null, new object[] { "snapshot_io_failed", "Sharing violation" })).Contains("占用"),
                "Result messages never pass Core's own words through");
            var day = view.GetMethod("DayLabel", BindingFlags.NonPublic | BindingFlags.Static);
            var today = new DateTime(2026, 10, 1, 9, 0, 0);
            Require((string)day.Invoke(null, new object[] { today.AddHours(8), today }) == "今天"
                && (string)day.Invoke(null, new object[] { today.AddDays(-1), today }) == "昨天"
                && (string)day.Invoke(null, new object[] { new DateTime(2026, 9, 28), today }) == "9月28日"
                && (string)day.Invoke(null, new object[] { new DateTime(2025, 12, 30), today }) == "2025年12月30日", "Day labels");
            Console.WriteLine("PASS: Windows argv, 9 receipt cases, 8 entry/redeploy argument sets with hideout assertion, 5 outcome texts, in-hideout refusals, 5 hideout replay receipts, 4 run-stat outcomes, 14 recorder stop texts, protected departure read-back, 6 results receipt cases, torn journal, 8 failure descriptions, unverified build refusal, windowless recovery, 6 status lines, result and day labels");
            return 0;
        }
        finally { Directory.Delete(root, true); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
