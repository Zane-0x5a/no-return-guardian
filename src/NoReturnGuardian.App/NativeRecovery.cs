using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace NoReturnGuardian
{
    /// <summary>
    /// 受监督的原生恢复：启动恢复与出发记录器的 Python 进程，核对它们留下的凭据，并把结果译成玩家可读的说明。
    /// 界面只负责发起与展示；这里不持有任何窗口。脚本的原话、标准错误输出和凭据都留在恢复日志里，不上界面。
    /// </summary>
    internal static class NativeRecovery
    {
        /// <summary>窗口与快捷键共用；同一时刻只允许一个原生恢复监督进程。</summary>
        private static int _running;

        public static string RuntimeRoot
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "native-recovery"); }
        }

        public static bool Available
        {
            get { return File.Exists(Path.Combine(RuntimeRoot, "runtime.json")); }
        }

        /// <summary>主窗口注册成功的结算页快捷键；未注册时为空，界面不提示。</summary>
        public static string HotkeyName { get; set; }

        /// <summary>主窗口注册成功的重开遭遇快捷键；未注册时为空。</summary>
        public static string RestartHotkeyName { get; set; }

        public static bool Running
        {
            get { return Volatile.Read(ref _running) != 0; }
        }

        internal sealed class GameTarget
        {
            public int Pid;
            public long Birth;
            public IntPtr Window;
        }

        /// <summary>
        /// Auto：由恢复脚本按游戏当时的样子决定从哪里开始——赴死之旅菜单、结算页、战斗中（先放弃本场），
        /// 或者已经回到这份战备的兵营里（只剩出发）。其余三个是固定入口，供诊断和旧日志对照。
        /// </summary>
        internal enum RecoveryEntry { Menu, Results, MidEncounter, Auto }

        /// <summary>玩家已经站在兵营里；恢复脚本没有动游戏。消息是脚本的原话，只进诊断记录。</summary>
        internal sealed class InHideoutException : InvalidOperationException
        {
            public InHideoutException(string message) : base(message) { }
        }

        /// <summary>恢复没有开始或没有完成：Message 是给玩家看的说明，Diagnostic 是只进诊断记录的原因。</summary>
        internal sealed class RecoveryFailedException : InvalidOperationException
        {
            public RecoveryFailedException(string message, string diagnostic) : base(message)
            {
                Diagnostic = diagnostic;
            }

            public string Diagnostic { get; private set; }
        }

        /// <summary>恢复脚本拒绝时的原话（NativeCheckpointTrigger.IN_HIDEOUT 的开头）。</summary>
        private const string InHideoutReason = "Already in a playable hideout";

        /// <summary>
        /// 恢复脚本在挂任何钩子之前拒绝的原话（NativeCodeSignature.UNVERIFIED_BUILD）：游戏更新后，
        /// 原生恢复用到的代码不再和验证过的版本逐字节一致。
        /// </summary>
        private const string UnverifiedBuildReason = "Game code differs from the verified build";

        private const string UnverifiedBuildText =
            "这个游戏版本里，原生恢复要用到的代码和验证过的版本不一样，所以没有运行，游戏没有被改动。";

        /// <summary>脚本报告完成，但凭据对不上：游戏多半已经载入，守护器不能替玩家说它对。</summary>
        private const string UnconfirmedText =
            "游戏可能已经载入了这份战备，但守护器没能确认每一步都完成。请核对兵营、装备和资源；不对的话，退出游戏后可以撤销最近一次恢复。";

        private const string BrokenRuntimeText = "原生恢复组件无法启动，请重新安装守护器。";

        internal sealed class RecoveryOutcome
        {
            public bool RedeployRequested;
            public bool Redeployed;
            public bool? SameEncounter;
            public bool Abandoned;
            /// <summary>没有重新载入战备，直接从玩家所在的、这份战备重建出的兵营出发。</summary>
            public bool FromHideout;
            /// <summary>改回战备值的征途统计项数（死信投递已用道具等）；没有回滚凭据时为 null。</summary>
            public int? RunStatsChanged;
            /// <summary>征途统计没能回滚时的原因；回滚成功为 null。</summary>
            public string RunStatsProblem;
        }

        /// <summary>路线板出发记录；由出发记录器在玩家第一次从这份战备出发时写入。</summary>
        public static string DeparturePath(string storage, string snapshotId)
        {
            return Path.Combine(Path.GetFullPath(storage), "departures", snapshotId + ".json");
        }

        public static bool HasDeparture(string storage, string snapshotId)
        {
            return File.Exists(DeparturePath(storage, snapshotId));
        }

        /// <summary>原生恢复和出发记录器的日志目录；每次新开日志前只留最近几次（LogRetention）。</summary>
        private static string LogDirectory(string storage)
        {
            string directory = Path.Combine(Path.GetFullPath(storage), "native-recovery-logs");
            Directory.CreateDirectory(directory);
            LogRetention.PruneSessions(directory);
            return directory;
        }

        /// <summary>
        /// 启动出发记录器（NativeDepartureRecorder.py）：等到兵营可玩后只挂一个只读钩子，抓到路线板出发事件并确认
        /// 进入遭遇后自行卸载。给了存档目录时，它在兵营里把游戏每次写好的兵营存档冻结一份到存储里，出发时把出发前
        /// 最后那一份保存为新的“出发前战备”，记录归这份新战备；这样它会一直等过遭遇，到下一个可玩兵营再挂钩。
        /// 保存不成时，记录归 snapshotId（可为空）。只给 snapshotId 时，玩家留在这份战备的兵营里多久都继续等，
        /// 离开兵营或游戏退出即停止。关闭它的标准输入即请求干净撤销。
        /// </summary>
        public static Process StartDepartureRecorder(string storage, string profile, string snapshotId, int pid,
            out string logPath)
        {
            string python = Python();
            string log = Path.Combine(LogDirectory(storage), "departure-" + Guid.NewGuid().ToString("N") + ".jsonl");
            logPath = log;
            var arguments = new List<string> { "-X", "utf8",
                Path.Combine(RuntimeRoot, "scripts", "diagnostics", "NativeDepartureRecorder.py"),
                pid.ToString(), "--storage", Path.GetFullPath(storage), "--log", log, "--duration", "43200" };
            if (!string.IsNullOrEmpty(snapshotId)) arguments.AddRange(new[] { "--snapshot", snapshotId });
            if (!string.IsNullOrEmpty(profile)) arguments.AddRange(new[] { "--profile", Path.GetFullPath(profile) });
            var start = new ProcessStartInfo(python, string.Join(" ", arguments.Select(Quote)))
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RuntimeRoot, RedirectStandardInput = true
            };
            return Process.Start(start);
        }

        private static Dictionary<string, object> Configuration()
        {
            return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(
                File.ReadAllText(Path.Combine(RuntimeRoot, "runtime.json"), Encoding.UTF8));
        }

        /// <summary>安装包自带的 Python 记成相对 native-recovery 的路径；本机开发构建也可以是绝对路径。</summary>
        private static string Python()
        {
            string python = Path.Combine(RuntimeRoot, (string)Configuration()["PythonPath"]);
            if (!File.Exists(python))
                throw new RecoveryFailedException("原生恢复组件不完整，缺少自带的 Python，请重新安装守护器。", "missing " + python);
            return python;
        }

        public static bool TryBegin()
        {
            return Interlocked.CompareExchange(ref _running, 1, 0) == 0;
        }

        public static void End()
        {
            Volatile.Write(ref _running, 0);
        }

        public static GameTarget FindGame()
        {
            Process[] games = Process.GetProcessesByName("tlou-ii");
            try
            {
                if (games.Length != 1) throw new InvalidOperationException("需要游戏正在运行，而且只开着一个游戏。");
                return new GameTarget
                {
                    Pid = games[0].Id,
                    Birth = games[0].StartTime.ToUniversalTime().ToFileTimeUtc(),
                    Window = games[0].MainWindowHandle
                };
            }
            finally { foreach (var game in games) game.Dispose(); }
        }

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr window, int command);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr window);

        public static void BringToFront(IntPtr window)
        {
            if (window == IntPtr.Zero) return;
            if (IsIconic(window)) ShowWindow(window, 9);
            SetForegroundWindow(window);
        }

        public static string NewLogPath(string storage)
        {
            return Path.Combine(LogDirectory(storage), "recovery-" + Guid.NewGuid().ToString("N") + ".jsonl");
        }

        /// <param name="hideoutRebuilt">
        /// 守护器担保：玩家所在的兵营就是这份战备在这个游戏进程里重建出来的，之后没有出发过；只有这样，
        /// 重开才可以不重新载入、直接从这个兵营出发。只随自动入口的重开传下去。
        /// </param>
        private static string[] RecoveryArguments(string runtimeRoot, int pid, string logPath, string storage,
            string profile, string snapshotId, RecoveryEntry entry, bool redeploy, bool hideoutRebuilt)
        {
            var arguments = new List<string> { "-X", "utf8", Path.Combine(runtimeRoot, "scripts", "diagnostics", "NativeCheckpointTrigger.py"),
                pid.ToString(), "--mode", "recover-preparation", "--duration", redeploy ? "240" : "180", "--log", logPath,
                "--recovery-storage", storage, "--recovery-profile", profile, "--recovery-snapshot", snapshotId,
                "--confirm-intrusive-experiment" };
            if (redeploy) arguments.Add("--redeploy");
            if (redeploy && hideoutRebuilt && entry == RecoveryEntry.Auto) arguments.Add("--hideout-rebuilt-from-snapshot");
            arguments.Add("--entry");
            arguments.Add(entry == RecoveryEntry.Menu ? "menu" : entry == RecoveryEntry.Results ? "results"
                : entry == RecoveryEntry.MidEncounter ? "mid-encounter" : "auto");
            return arguments.ToArray();
        }

        private static string Quote(string value)
        {
            var result = new StringBuilder("\"");
            int slashes = 0;
            foreach (char character in value)
            {
                if (character == '\\') { slashes++; continue; }
                result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
                result.Append(character);
                slashes = 0;
            }
            return result.Append('\\', slashes * 2).Append('"').ToString();
        }

        /// <summary>
        /// 阻塞执行一次受监督的原生恢复。不能开始或没有完成时抛 InHideoutException 或 RecoveryFailedException，
        /// 其他异常不会从这里出去。调用方先 TryBegin，结束后 End。
        /// </summary>
        public static RecoveryOutcome Execute(string storage, string profile, string snapshotId, RecoveryEntry entry,
            bool redeploy, GameTarget game, string logPath, Action<string> progress, bool hideoutRebuilt = false)
        {
            string stderr;
            int exitCode;
            try
            {
                string[] arguments = RecoveryArguments(RuntimeRoot, game.Pid, logPath, Path.GetFullPath(storage),
                    Path.GetFullPath(profile), snapshotId, entry, redeploy, hideoutRebuilt);
                exitCode = Run(Python(), arguments, progress, out stderr);
            }
            catch (Exception error) when (error is IOException || error is Win32Exception || error is UnauthorizedAccessException)
            {
                throw new RecoveryFailedException(BrokenRuntimeText, error.ToString());
            }

            if (stderr.Length > 0)
            {
                try { File.WriteAllText(Path.ChangeExtension(logPath, ".stderr.txt"), stderr, new UTF8Encoding(false)); }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { }
            }

            try
            {
                if (IsHideoutReplay(logPath, game.Pid, game.Birth))
                {
                    return ReadHideoutReplay(logPath, exitCode, stderr);
                }

                if (exitCode != 0)
                {
                    if (stderr.Contains(InHideoutReason)) throw new InHideoutException(stderr);
                    throw new RecoveryFailedException(DescribeFailure(logPath, stderr), "exit " + exitCode + Environment.NewLine + stderr);
                }

                VerifyMechanicalReceipt(logPath, snapshotId, game.Pid, game.Birth);
                var outcome = new RecoveryOutcome { RedeployRequested = redeploy };
                ReadRunStatsOutcome(logPath, outcome);
                RecoveryEntry used = entry == RecoveryEntry.Auto
                    ? ResolvedEntry(Path.ChangeExtension(logPath, ".supervisor.jsonl"), game.Pid, game.Birth)
                    : entry;
                if (used != RecoveryEntry.Menu) VerifyResultsReceipt(Path.ChangeExtension(logPath, ".results.jsonl"), game.Pid, game.Birth);
                string encounterJournal = Path.ChangeExtension(logPath, ".encounter.jsonl");
                if (File.Exists(encounterJournal)) outcome.Abandoned = VerifyAbandonReceipt(encounterJournal, game.Pid, game.Birth);
                if (redeploy) ReadRedeployOutcome(logPath, outcome);
                return outcome;
            }
            catch (Exception error) when (RecoveryJournal.Unreadable(error))
            {
                throw new RecoveryFailedException(UnconfirmedText, "journal unreadable: " + error.Message);
            }
        }

        private static int Run(string python, string[] arguments, Action<string> progress, out string stderr)
        {
            var start = new ProcessStartInfo(python, string.Join(" ", arguments.Select(Quote)))
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RuntimeRoot,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            using (var process = new Process { StartInfo = start })
            {
                var errors = new StringBuilder();
                process.ErrorDataReceived += (errorSender, line) => { if (line.Data != null) errors.AppendLine(line.Data); };
                process.Start();
                process.BeginErrorReadLine();
                string line;
                bool pressing = false;
                while ((line = process.StandardOutput.ReadLine()) != null)
                {
                    if (!pressing && line.Contains("results-continue-injected"))
                    {
                        pressing = true;
                        progress("正在走完结算");
                    }
                    if (line.Contains("abandon-broadcast")) progress("已结束本场，等待结算");
                    if (line.Contains("results-exit-complete")) progress("结算结束，正在保存现场");
                    if (line.Contains("recovery-transition")) progress("游戏正在载入战备");
                    if (line.Contains("\"hideout-redeploy\"")) progress("已在这份战备的兵营里，正在出发");
                    else if (line.Contains("redeploy-start")) progress("已回到战备，正在出发");
                }
                process.WaitForExit();
                stderr = errors.ToString();
                return process.ExitCode;
            }
        }

        /// <summary>
        /// 恢复没完成时给玩家的说明：游戏有没有被动、存档在什么状态、下一步怎么做。依据是监督器的日志
        /// （它关闭了游戏、回滚了存档没有）和脚本的结论行；原话不出现在说明里。
        /// </summary>
        internal static string DescribeFailure(string logPath, string stderr)
        {
            RecoveryJournal supervisor = TryRead(Path.ChangeExtension(logPath, ".supervisor.jsonl"));
            if (supervisor != null && supervisor.Has("supervisor-target-exited"))
            {
                if (supervisor.Has("recovery-disk-rollback-failed"))
                    return "恢复中途出错，守护器关闭了游戏，但存档没能自动还原。先不要启动游戏，在守护器里撤销最近一次恢复。";
                RecoveryJournal.Entry rollback = supervisor.OfKind("recovery-disk-rollback").LastOrDefault();
                return "恢复中途出错，守护器关闭了游戏，"
                    + (rollback != null && rollback.IsTrue("required") ? "存档已还原到恢复之前。" : "存档没有改动。")
                    + "现在可以在守护器里选一份战备恢复。";
            }

            string reason = stderr ?? string.Empty;
            if (reason.Contains(UnverifiedBuildReason))
                return UnverifiedBuildText + "完整退出游戏后，仍可以在守护器里恢复战备。";
            if (reason.Contains("A native recovery already owns this game process"))
                return "已有一次恢复正在进行，请等它结束。";
            if (new[] { "requires the unloaded No Return menu", "Results exit requires an inactive No Return run",
                    "Results entry requires a visible death results page", "is not a supported death results page",
                    "the game is already at the menu", "requires an active No Return encounter" }.Any(reason.Contains))
                return "现在的画面不能开始恢复：需要停在死亡结算页、赴死之旅菜单，或者战斗中。游戏没有被改动。";
            if (new[] { "Results did not advance", "Results kept ignoring", "Results exit timed out",
                    "Results returned to an earlier page", "Results state kept changing", "Results task changed" }.Any(reason.Contains))
                return "结算页没有按预期走完，恢复没有开始，游戏仍在运行。可以手动走完结算，回到赴死之旅菜单后再试。";
            if (reason.Contains("has no recorded departure"))
                return "这份战备还没有出发记录，不能重开战斗。先在它的兵营从路线板出发一次。";
            if (new[] { "Formal preparation snapshot verification failed", "not a confirmed preparation", "does not allow",
                    "can only restart its encounter", "changed after formal verification" }.Any(reason.Contains))
                return "这份战备没有通过校验，没有恢复，游戏没有被改动。";
            if (reason.Contains("Pre-recovery live disk snapshot failed"))
                return "没能先把当前存档留档，所以没有恢复，游戏仍在运行。稍等几秒再试。";
            return "恢复没有完成，游戏仍在运行。可以再试一次。";
        }

        private static RecoveryJournal TryRead(string path)
        {
            try
            {
                return File.Exists(path) ? RecoveryJournal.Read(path) : null;
            }
            catch (Exception error) when (RecoveryJournal.Unreadable(error))
            {
                return null;
            }
        }

        /// <summary>自动入口实际从哪里开始：监督器在任何改动之前记下了它。</summary>
        private static RecoveryEntry ResolvedEntry(string supervisorLog, int pid, long birth)
        {
            var secured = RecoveryJournal.Read(supervisorLog).OfKind("recovery-preflight-secured")
                .Where(entry => entry.From(pid, birth)).ToList();
            if (secured.Count != 1) throw new RecoveryFailedException(UnconfirmedText, "no single entry receipt");
            switch (secured[0].Text("entry"))
            {
                case "menu": return RecoveryEntry.Menu;
                case "results": return RecoveryEntry.Results;
                case "mid-encounter": return RecoveryEntry.MidEncounter;
                default: throw new RecoveryFailedException(UnconfirmedText, "unknown entry " + secured[0].Text("entry"));
            }
        }

        private static bool IsHideoutReplay(string logPath, int pid, long birth)
        {
            if (!File.Exists(logPath)) return false;
            RecoveryJournal.Entry header = RecoveryJournal.Read(logPath).Entries.FirstOrDefault();
            return header != null && header.Kind == "host-preflight" && header.Text("mode") == "hideout-redeploy"
                && header.From(pid, birth);
        }

        /// <summary>
        /// 直接从兵营出发：没有载入，也没有改存档。出发没成功只说明玩家还在兵营；钩子没证明撤销才算失败。
        /// </summary>
        internal static RecoveryOutcome ReadHideoutReplay(string logPath, int exitCode, string stderr)
        {
            RecoveryJournal journal = RecoveryJournal.Read(logPath);
            bool detached = journal.Count("host-detached") == 1 && journal.OfKind("host-detached").Single().IsTrue("codeRestored");
            if (!detached || (exitCode != 0 && exitCode != 4))
                throw new RecoveryFailedException("从兵营出发时出了问题，守护器没能确认游戏已恢复原样。建议完整退出游戏后再继续。",
                    "hideout replay hooks not proven removed, exit " + exitCode + Environment.NewLine + stderr);
            var outcome = new RecoveryOutcome { RedeployRequested = true, FromHideout = true };
            ReadRedeployOutcome(logPath, outcome);
            if (exitCode == 0 && !outcome.Redeployed)
                throw new RecoveryFailedException("没能确认已经出发。请看一下游戏里是否已经进入战斗。", "hideout replay without arrival receipt");
            return outcome;
        }

        private static bool VerifyAbandonReceipt(string journalPath, int pid, long birth)
        {
            RecoveryJournal journal = RecoveryJournal.Read(journalPath);
            bool detached = journal.OfKind("abandon-detached").Any(entry => entry.IsTrue("codeRestored"));
            bool complete = journal.Count("abandon-complete") == 1;
            if (!journal.AllFrom(pid, birth) || !detached || !complete || journal.Has("abandon-stopped"))
                throw new RecoveryFailedException(UnconfirmedText, "abandon receipt incomplete");
            return true;
        }

        /// <summary>
        /// 原生载入只替换征途存档；档案里由新征途初始化的统计（死信投递已用道具等）在调度载入前改回战备值。
        /// 这一步失败不撤销已接受的恢复，只如实报告。
        /// </summary>
        internal static void ReadRunStatsOutcome(string logPath, RecoveryOutcome outcome)
        {
            foreach (RecoveryJournal.Entry entry in RecoveryJournal.Read(logPath).Entries)
            {
                if (entry.Kind == "run-stats-restored" && entry.Number("changed").HasValue)
                {
                    outcome.RunStatsChanged = (int)entry.Number("changed").Value;
                    outcome.RunStatsProblem = null;
                }
                else if (entry.Kind == "run-stats-unrestored")
                {
                    outcome.RunStatsChanged = null;
                    outcome.RunStatsProblem = DescribeRunStatsProblem(entry.Text("reason"));
                    if (entry.Flag("reverted") == false)
                        outcome.RunStatsProblem += "；部分统计已改写且未能撤回";
                }
            }
        }

        private static string DescribeRunStatsProblem(string reason)
        {
            switch (reason)
            {
                case "Profile stat service identity mismatch": return "游戏的统计服务与核对过的版本不一致，没有调用";
                case "Profile run stat is not registered": return "游戏里找不到对应的统计项";
                case "Native profile stat write was rejected": return "游戏拒绝了统计写入";
                default: return "原因未知";
            }
        }

        private static void ReadRedeployOutcome(string logPath, RecoveryOutcome outcome)
        {
            foreach (RecoveryJournal.Entry entry in RecoveryJournal.Read(logPath).OfKind("redeploy-complete"))
            {
                outcome.Redeployed = true;
                outcome.SameEncounter = entry.Flag("sameEncounter");
            }
        }

        /// <summary>出发记录器最后写下的停止原因（原话）；没有时为 null。读不出日志时抛 IOException 或 InvalidDataException。</summary>
        public static string ReadRecorderStop(string logPath)
        {
            RecoveryJournal.Entry stop = RecoveryJournal.Read(logPath).OfKind("departure-recorder-stopped").LastOrDefault();
            return stop == null ? null : stop.Text("reason");
        }

        /// <summary>
        /// 记录器没写出出发记录时给玩家看的原因；守护器自己停掉的返回 null。自动保存模式下记录器会一直重启，
        /// 离开兵营、游戏退出、等待到期这类正常结束也不打扰玩家。原话由调用方记进诊断记录。
        /// </summary>
        public static string DescribeRecorderStop(string logPath, bool protecting)
        {
            string reason;
            try
            {
                reason = ReadRecorderStop(logPath);
            }
            catch (Exception error) when (RecoveryJournal.Unreadable(error))
            {
                return "出发记录器的日志不可读，这次出发没有记录。";
            }

            if (protecting && (reason == "run-left" || reason == "game-exited" || reason == "hideout-not-reached"
                || reason == "no-departure-captured"))
            {
                return null;
            }

            string retry = protecting ? "" : "下次原生恢复回到这份战备后会重新记录。";
            if (reason != null && reason.StartsWith(UnverifiedBuildReason, StringComparison.Ordinal))
            {
                return UnverifiedBuildText + "出发记录和出发自动保存在这个版本上不可用。";
            }

            const string unprotected = "departure-not-protected: ";
            if (reason != null && reason.StartsWith(unprotected, StringComparison.Ordinal))
            {
                return DescribeUnprotectedDeparture(reason.Substring(unprotected.Length));
            }

            switch (reason)
            {
                case "guardian-stopped":
                    return null;
                case null:
                    return "出发记录器意外退出，这次出发没有记录。";
                case "encounter-before-armed":
                    return "记录器就绪前已经进入战斗，这次出发没有记录。" + retry;
                case "encounter-entered-without-capture":
                    return "进入了战斗，但没有看到这次从路线板出发，这次出发没有" + (protecting ? "自动保存。" : "记录。");
                case "encounter-not-entered":
                    return "看到了路线板出发，但 90 秒内没有进入战斗，这次出发没有记录。" + retry;
                case "run-left":
                    return "已离开这份战备的兵营，出发记录器已停止。";
                case "game-exited":
                    return "游戏已退出，出发记录器已停止。";
                case "hideout-not-reached":
                case "no-departure-captured":
                    return "一直没有等到路线板出发，出发记录器已停止。" + retry;
                default:
                    return "出发记录器出错停止了，这次出发没有记录。" + retry;
            }
        }

        private static string DescribeUnprotectedDeparture(string reason)
        {
            string prefix = "这次出发没有自动保存：";
            switch (reason)
            {
                case "no-hideout-save-before-departure":
                    return prefix + "进兵营后游戏还没写过兵营存档（拿补给、交易都会存档），可以出发前手动保护。";
                case "hideout-save-in-progress-at-departure":
                case "newest-hideout-save-not-frozen":
                case "hideout-save-missed-before-departure":
                case "frozen-save-not-before-departure":
                    return prefix + "出发前一刻游戏刚写了兵营存档，没来得及保存那一份。";
                case "departure-save-not-observed":
                    return prefix + "出发后没有等到游戏保存，无法确认出发前那份存档是最后一份。";
                default:
                    return prefix + "保存前的核对没有通过。";
            }
        }

        /// <summary>出发记录器这一轮保存下的“出发前战备”；没有保存成功时为 null。</summary>
        public static string ReadProtectedDeparture(string logPath)
        {
            try
            {
                RecoveryJournal.Entry protectedEntry = RecoveryJournal.Read(logPath).OfKind("departure-protected").LastOrDefault();
                return protectedEntry == null ? null : protectedEntry.Text("snapshotId");
            }
            catch (Exception error) when (RecoveryJournal.Unreadable(error))
            {
                return null;
            }
        }

        /// <summary>重开结果的玩家可读说明；不把机械完成说成游戏内验收。</summary>
        public static string DescribeOutcome(RecoveryOutcome outcome)
        {
            string text;
            if (outcome == null || !outcome.RedeployRequested)
                text = "已回到战备，可以继续游玩。";
            else if (outcome.FromHideout && !outcome.Redeployed)
                text = "没有出发成功，你还在兵营里。可以再试一次，或在路线板手动出发。";
            else if (!outcome.Redeployed)
                text = "已回到战备，但没有自动出发，请在路线板手动出发。";
            else
                text = outcome.SameEncounter == false
                    ? "已自动出发，但进入的遭遇和记录不同，请核对。"
                    : outcome.FromHideout
                        ? "已从兵营出发，重开同一遭遇，请核对装备和资源。"
                        : "已重开同一遭遇，请核对装备和资源。";
            if (outcome?.RunStatsProblem != null)
                text += "死信投递等记录没能改回（" + outcome.RunStatsProblem
                    + "），这局的死信投递可能要求不同的道具。";
            return text;
        }

        /// <summary>玩家站在兵营里按了“恢复兵营”或不是这份战备的兵营里按了“重开战斗”。</summary>
        public static string DescribeInHideout(bool restart, string recoverHotkey)
        {
            return restart
                ? "你在兵营里，但这不是这份战备刚恢复出的兵营，不能直接出发。先在暂停菜单退回赴死之旅菜单，再重开战斗。"
                : "你已经在兵营里。要回到战备时的样子，先在暂停菜单退回赴死之旅菜单，再"
                    + (string.IsNullOrEmpty(recoverHotkey) ? "恢复兵营。" : "按 " + recoverHotkey + "。");
        }

        private static void VerifyResultsReceipt(string journalPath, int pid, long birth)
        {
            RecoveryJournal journal = RecoveryJournal.Read(journalPath);
            bool detached = journal.Count("results-continue-detached") == 1
                && journal.OfKind("results-continue-detached").Single().IsTrue("codeRestored");
            bool complete = journal.Count("results-exit-complete") == 1;
            if (!journal.AllFrom(pid, birth) || !detached || !complete || journal.Has("results-exit-stopped"))
                throw new RecoveryFailedException(UnconfirmedText, "results receipt incomplete");
        }

        private static void VerifyMechanicalReceipt(string logPath, string snapshotId, int pid, long birth)
        {
            RecoveryJournal journal = RecoveryJournal.Read(logPath);
            var headers = journal.OfKind("host-preflight").ToList();
            bool identity = headers.Count == 1 && headers[0].From(pid, birth) && headers[0].Text("mode") == "recover-preparation";
            bool rebuilt = journal.OfKind("native-recovery-rebuilt")
                .Count(entry => entry.Text("snapshotId") == snapshotId && entry.Flag("inGameAcceptance") == false) == 1;
            bool detached = journal.OfKind("host-detached").Count(entry => entry.IsTrue("codeRestored")) == 1;
            bool failed = journal.Has("recovery-fatal") || journal.Has("script-error");
            if (!identity || !rebuilt || !detached || failed)
                throw new RecoveryFailedException(UnconfirmedText, "mechanical receipt incomplete");
        }
    }
}
