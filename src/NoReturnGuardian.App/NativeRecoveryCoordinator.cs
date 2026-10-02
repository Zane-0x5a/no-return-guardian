using NoReturnGuardian.Core;
using System;
using System.ComponentModel;
using System.Linq;
using System.Windows.Forms;

namespace NoReturnGuardian
{
    /// <summary>
    /// 恢复兵营与重开战斗：窗口里的确认面板和游戏内快捷键走同一道门控、同一条执行路径。
    /// 能不能开始只在这里判断；界面藏起按钮只是为了好看，打开面板、开始执行时都会重新判断。
    /// </summary>
    internal sealed class NativeRecoveryCoordinator
    {
        private readonly IGuardianHost _host;
        private readonly DepartureRecorder _recorder;

        public NativeRecoveryCoordinator(IGuardianHost host, DepartureRecorder recorder)
        {
            _host = host;
            _recorder = recorder;
        }

        /// <summary>恢复面板开着：这期间不新挂出发记录器、不自动清理。藏到托盘时关掉，真开始恢复时重新算开着。</summary>
        public bool PanelOpen { get; private set; }

        /// <summary>已经通过门控的一次原生恢复。</summary>
        private sealed class Request
        {
            public SnapshotRecord Target;
            public bool Restart;
            /// <summary>重开，并且有路线板出发记录：回到战备后按原路线自动出发。</summary>
            public bool Redeploy;
            /// <summary>玩家就站在这份战备刚重建出的兵营里：不重新载入，直接出发。</summary>
            public bool FromHideout;
            public NativeRecovery.GameTarget Game;
        }

        /// <summary>
        /// 门控的前一半，与选哪份战备无关：原生组件在、没有别的恢复在进行、没有未闭合的恢复事务或暖恢复、
        /// 找到了存档、游戏正在运行（只开着一个）。返回给玩家看的拒绝原因，能开始时返回 null。
        /// </summary>
        private string Refusal(out NativeRecovery.GameTarget game)
        {
            game = null;
            if (!NativeRecovery.Available)
                return "这份守护器没有原生恢复组件，请在退出游戏后恢复战备。";
            if (NativeRecovery.Running)
                return "已有一次恢复正在进行，请等它结束。";
            if (_host.RestoreUnfinished)
                return "上次恢复还没完成，先处理它再恢复。";
            if (string.IsNullOrWhiteSpace(_host.Settings.ProfilePath))
                return "还没有找到游戏存档。";
            try
            {
                game = NativeRecovery.FindGame();
                return null;
            }
            catch (InvalidOperationException error)
            {
                return error.Message;
            }
        }

        /// <summary>
        /// 门控的后一半：目标是这个存档账号的热保存战备，“恢复兵营”要能回到兵营，“重开战斗”要能重开它的战斗。
        /// </summary>
        private string Resolve(SnapshotRecord target, bool restart, NativeRecovery.GameTarget game, out Request request)
        {
            request = null;
            GuardianSettings settings = _host.Settings;
            if (target == null || target.Manifest == null)
                return "这份战备已经不在了。";
            if (!string.Equals(target.Manifest.SourceProfilePath, settings.ProfilePath, StringComparison.OrdinalIgnoreCase))
                return "这份战备属于另一个存档账号。";
            if (target.Manifest.SchemaVersion != 5)
                return "这份战备只能在完整退出游戏后恢复。";

            bool redeploy = restart && NativeRecovery.HasDeparture(settings.StoragePath, target.Manifest.Id);
            if (restart ? !SnapshotPolicy.IsRedeployablePreparation(target) : !SnapshotPolicy.IsConfirmedPreparation(target))
                return restart || !SnapshotPolicy.IsPostDepartureSave(target)
                    ? "这份快照不能恢复。"
                    : "这份是旧版出发自动保存，存的是出发之后的状态，回不了兵营，只能重开战斗。";
            // 没有出发记录的“重开战斗”就是回到兵营，再请玩家手动出发一次；出发之后才存下的旧版保存回不了兵营。
            if (restart && !redeploy && !SnapshotPolicy.IsConfirmedPreparation(target))
                return "这份战备没有出发记录，不能重开战斗。";

            request = new Request
            {
                Target = target,
                Restart = restart,
                Redeploy = redeploy,
                // 先判断再停记录器：判断靠的正是它一直看着兵营。
                FromHideout = redeploy && _recorder.RebuiltHideoutMatches(target.Manifest.Id, game),
                Game = game
            };
            return null;
        }

        private string Resolve(string id, bool restart, out Request request)
        {
            NativeRecovery.GameTarget game;
            request = null;
            return Refusal(out game) ?? Resolve(Find(id), restart, game, out request);
        }

        private SnapshotRecord Find(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            Result<SnapshotRecord> found = _host.Store.GetSnapshot(id);
            return found.Success ? found.Value : null;
        }

        /// <summary>界面打开确认面板：现在就不能做时直接告诉玩家原因，不让他确认一件必然失败的事。</summary>
        public void Open(string id, string action)
        {
            if (action != "restart" && action != "hideout")
            {
                return;
            }

            Request request;
            string refusal = Resolve(id, action == "restart", out request);
            if (refusal != null)
            {
                _host.PostRecovery("failed", refusal);
                return;
            }

            // 确认面板开着时不新挂记录器；已经挂着的继续看着兵营，“重开战斗”要靠它确认玩家还没出发。
            PanelOpen = true;
        }

        public void Close()
        {
            if (!NativeRecovery.Running)
            {
                PanelOpen = false;
            }
        }

        public void Start(string id, string action)
        {
            if (action != "restart" && action != "hideout")
            {
                return;
            }

            Request request;
            string refusal = Resolve(id, action == "restart", out request);
            if (refusal != null)
            {
                _host.PostRecovery("failed", refusal);
                return;
            }

            Run(request, true);
        }

        /// <summary>
        /// 游戏内 Ctrl+Alt+F9 恢复兵营、Ctrl+Alt+F10 重开战斗，作用于最近的一份战备；刚恢复出的兵营里重开时，
        /// 作用于重建它的那份。从哪里开始由恢复脚本按游戏当时的样子决定：结算页、赴死之旅菜单、
        /// 战斗中（先放弃本场），或者已经站在这份战备的兵营里（直接出发）。不能开始时游戏不受影响。
        /// </summary>
        public void Hotkey(bool restart)
        {
            if (NativeRecovery.Running)
            {
                _host.Notify("恢复正在进行", "请等当前恢复结束。", ToolTipIcon.Info);
                return;
            }

            NativeRecovery.GameTarget game;
            string refusal = Refusal(out game);
            if (refusal != null)
            {
                _host.Notify("没有开始", refusal, ToolTipIcon.Warning);
                return;
            }

            GuardianSettings settings = _host.Settings;
            SnapshotRecord latest = _host.Store.ListSnapshots().FirstOrDefault(record =>
                record != null
                && record.Manifest != null
                && record.Manifest.SchemaVersion == 5
                && string.Equals(record.Manifest.SourceProfilePath, settings.ProfilePath, StringComparison.OrdinalIgnoreCase)
                && SnapshotPolicy.IsRedeployablePreparation(record));
            SnapshotRecord target = restart ? Find(_recorder.RebuiltHideoutSnapshot()) ?? latest : latest;
            if (target == null)
            {
                _host.Notify("没有开始", "还没有可以恢复的战备。", ToolTipIcon.Warning);
                return;
            }

            if (!restart && SnapshotPolicy.IsPostDepartureSave(target))
            {
                // 不悄悄换成更早的一份：玩家按的是“最近的战备”。
                _host.Notify("没有回兵营", "最近一份是旧版出发自动保存，存的是出发之后的状态，回不了兵营，只能"
                    + (NativeRecovery.RestartHotkeyName != null ? "按 " + NativeRecovery.RestartHotkeyName + " " : "")
                    + "重开战斗。要回兵营，请在守护器里选一份手动战备。", ToolTipIcon.Warning);
                return;
            }

            Request request;
            refusal = Resolve(target, restart, game, out request);
            if (refusal != null)
            {
                _host.Notify("没有开始", refusal, ToolTipIcon.Warning);
                return;
            }

            Run(request, false);
        }

        /// <summary>
        /// 执行一次已通过门控的恢复：确认面板就是玩家的明确请求，快捷键本身也是。恢复成功、还留在兵营里时，
        /// 记下这个兵营，之后的“重开战斗”可以直接从这里出发。
        /// </summary>
        private void Run(Request request, bool fromWindow)
        {
            if (!NativeRecovery.TryBegin())
            {
                Fail(fromWindow, "恢复正在进行", "已有一次恢复正在进行，请等它结束。");
                return;
            }

            _recorder.Stop();
            PanelOpen = fromWindow;
            GuardianSettings settings = _host.Settings;
            string storage = settings.StoragePath;
            string profile = settings.ProfilePath;
            string snapshotId = request.Target.Manifest.Id;
            string targetTime = GuardianView.TimeOf(request.Target.CreatedUtc.ToLocalTime());
            NativeRecovery.GameTarget game = request.Game;
            var worker = new BackgroundWorker { WorkerReportsProgress = true };
            worker.ProgressChanged += (sender, args) => _host.PostRecovery("running", (string)args.UserState);
            worker.DoWork += (sender, args) =>
            {
                try
                {
                    string logPath = NativeRecovery.NewLogPath(storage);
                    DiagnosticLog.Write("native-recovery", (request.Restart ? "restart " : "hideout ") + snapshotId
                        + (request.Redeploy ? " redeploy" : "") + (request.FromHideout ? " from-hideout" : "")
                        + (fromWindow ? " window" : " hotkey") + " (" + logPath + ")");
                    args.Result = NativeRecovery.Execute(storage, profile, snapshotId, NativeRecovery.RecoveryEntry.Auto,
                        request.Redeploy, game, logPath, text => worker.ReportProgress(0, text), request.FromHideout);
                }
                finally
                {
                    NativeRecovery.End();
                }
            };
            worker.RunWorkerCompleted += (sender, args) =>
            {
                worker.Dispose();
                Finish(request, fromWindow, targetTime, args.Error, (NativeRecovery.RecoveryOutcome)(args.Error == null ? args.Result : null));
            };

            if (fromWindow)
            {
                _host.PostRecovery("running", request.FromHideout ? "正在核对兵营和出发记录" : "正在核对游戏状态和战备");
                // 游戏失焦时载入会停滞；点击的这一刻守护器仍持有前台权限，把它交还给游戏。
                NativeRecovery.BringToFront(game.Window);
            }
            else
            {
                _host.Notify(request.Restart ? "正在重开战斗" : "正在恢复兵营",
                    request.FromHideout
                        ? "从这个兵营按记录的路线出发。"
                        : request.Restart && !request.Redeploy
                            ? "回到 " + targetTime + " 的战备。这份战备还没有出发记录，请在兵营手动出发一次，之后即可直接重开。"
                            : "回到 " + targetTime + " 的战备" + (request.Redeploy ? "后自动出发。" : "。"),
                    ToolTipIcon.Info);
            }

            worker.RunWorkerAsync();
            _host.PublishState();
        }

        private void Finish(Request request, bool fromWindow, string targetTime, Exception error,
            NativeRecovery.RecoveryOutcome outcome)
        {
            string snapshotId = request.Target.Manifest.Id;
            if (error is NativeRecovery.InHideoutException)
            {
                DiagnosticLog.Write("native-recovery", "refused in hideout: " + error.Message);
                Fail(fromWindow, request.Restart ? "没有重开" : "没有回兵营",
                    NativeRecovery.DescribeInHideout(request.Restart, NativeRecovery.HotkeyName));
            }
            else if (error != null)
            {
                var failure = error as NativeRecovery.RecoveryFailedException;
                DiagnosticLog.Write("native-recovery", failure != null ? failure.Diagnostic : error.ToString());
                string text = failure != null ? failure.Message : "恢复没有完成。";
                if (fromWindow)
                {
                    _host.PostRecovery("failed", text);
                }
                else
                {
                    _host.Toast("快捷键" + (request.Restart ? "重开战斗" : "恢复兵营") + "没有完成", "danger");
                }

                _host.Notify("没有完成", text, ToolTipIcon.Warning);
            }
            else
            {
                string text = NativeRecovery.DescribeOutcome(outcome);
                if (!outcome.FromHideout)
                {
                    _host.RememberRestored(snapshotId);
                }

                // 成功即收起面板：游戏已在前台，玩家不必切出来关它。
                PanelOpen = false;
                bool clean = outcome.Redeployed && outcome.SameEncounter != false && outcome.RunStatsProblem == null;
                if (fromWindow)
                {
                    _host.PostRecovery("done", clean ? "已重开 " + targetTime + " 战备的同一遭遇" : text);
                }
                else
                {
                    _host.Toast(targetTime + " 的战备：" + text, "signal");
                }

                _host.Notify(outcome.Redeployed ? "已重开战斗" : outcome.FromHideout ? "没有出发" : "已回到兵营", text, ToolTipIcon.Info);
                if (!outcome.Redeployed)
                {
                    // 还在这份战备的兵营里：看着它，之后可以直接从这里重开。
                    _recorder.Arm(snapshotId, true);
                }
            }

            // 恢复前留下的现场是“撤销最近恢复”的目标。
            _host.RefreshUndoPoint();
            _host.ReloadSnapshots(snapshotId);
        }

        private void Fail(bool fromWindow, string title, string text)
        {
            if (fromWindow)
            {
                _host.PostRecovery("failed", text);
            }
            else
            {
                _host.Toast(text, "danger");
            }

            _host.Notify(title, text, ToolTipIcon.Warning);
        }
    }
}
