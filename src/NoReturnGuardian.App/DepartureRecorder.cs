using NoReturnGuardian.Core;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace NoReturnGuardian
{
    /// <summary>
    /// 出发记录器的生命周期，同一时刻最多一个记录器进程（NativeDepartureRecorder.py）。保护战备或回到它之后，
    /// 它替这份战备记录下一次路线板出发；开着“出发自动保存”时，游戏运行期间始终留一个记录器，把出发前最后一份
    /// 兵营存档保存为新战备。原生恢复刚重建出的兵营也由它看着：只有它一直在看、还没看到出发，“重开战斗”
    /// 才可以不重新载入、直接从这个兵营出发。
    /// </summary>
    internal sealed class DepartureRecorder
    {
        private const int Retention = 10;
        private static readonly TimeSpan WatchBackoff = TimeSpan.FromSeconds(60);
        private readonly IGuardianHost _host;
        private readonly bool _enabled;
        private Process _process;
        private string _owner;
        private string _log;
        private RebuiltHideout _rebuilt;
        private int _blockedPid;
        private DateTime _retryUtc;
        private string _notice;

        /// <param name="enabled">截图模式下为 false：不启动任何记录器。</param>
        public DepartureRecorder(IGuardianHost host, bool enabled)
        {
            _host = host;
            _enabled = enabled;
        }

        /// <summary>
        /// 原生恢复刚把这份战备重建成兵营，玩家还站在里面：同一个游戏进程，紧接着挂上的记录器一直在看着，
        /// 还没看到任何出发。
        /// </summary>
        private sealed class RebuiltHideout
        {
            public string SnapshotId;
            public int Pid;
            public long Birth;
            public Process Recorder;
            public string RecorderLog;
        }

        /// <summary>正在运行的记录器替哪份战备记录出发；自动清理不删它。</summary>
        public string Owner
        {
            get { return _process == null ? null : _owner; }
        }

        public bool AutoProtect
        {
            get
            {
                GuardianSettings settings = _host.Settings;
                return settings.AutoProtectOnDeparture && NativeRecovery.Available
                    && !string.IsNullOrWhiteSpace(settings.ProfilePath);
            }
        }

        /// <summary>
        /// 保护战备或回到它之后，记录下一次路线板出发。开着“出发自动保存”时，记录器还会把出发前最后一份兵营存档
        /// 保存为新战备，出发记录归新战备；保存不成时归这份战备。记录总是跟着这份战备的兵营里最近的一次出发，
        /// “重开战斗”重开的就是玩家刚打的那一场。rebuilt：这个兵营是原生恢复刚重建的，记录器同时替它看着。
        /// </summary>
        public void Arm(string snapshotId, bool rebuilt = false)
        {
            Stop();
            string owner = string.IsNullOrEmpty(snapshotId) ? null : snapshotId;
            if (!_enabled || !NativeRecovery.Available || (owner == null && !AutoProtect))
            {
                return;
            }

            NativeRecovery.GameTarget game;
            try
            {
                game = NativeRecovery.FindGame();
            }
            catch (InvalidOperationException)
            {
                return;
            }

            Start(game, owner);
            if (rebuilt && _process != null)
            {
                _rebuilt = new RebuiltHideout
                {
                    SnapshotId = owner,
                    Pid = game.Pid,
                    Birth = game.Birth,
                    Recorder = _process,
                    RecorderLog = _log
                };
            }
        }

        /// <summary>
        /// 开着“出发自动保存”时，游戏运行期间始终留一个出发记录器：它只读等过遭遇和菜单，到可玩兵营才挂钩。
        /// 原生恢复进行中、恢复面板打开时、有未闭合的恢复事务时不启动，免得抓到恢复自己重放的出发。
        /// </summary>
        public void EnsureWatcher(bool gameRunning, bool recoveryPanelOpen)
        {
            if (_process != null || !_enabled || !AutoProtect || recoveryPanelOpen || DateTime.UtcNow < _retryUtc
                || NativeRecovery.Running || _host.RestoreUnfinished || !gameRunning)
            {
                return;
            }

            NativeRecovery.GameTarget game;
            try
            {
                game = NativeRecovery.FindGame();
            }
            catch (InvalidOperationException)
            {
                return;
            }

            // 游戏刚启动时还在加载，等它跑满一分钟再挂，免得启动期的读取失败被当成记录器故障。
            if (game.Pid != _blockedPid && DateTime.FromFileTimeUtc(game.Birth) <= DateTime.UtcNow - WatchBackoff)
            {
                Start(game, null);
            }
        }

        /// <summary>
        /// 换了出发自动保存的开关：重启记录器。它原本替哪份战备记录出发，就继续替它记录，替刚恢复出的兵营看着的
        /// 也继续看着。
        /// </summary>
        public void Restart(bool gameRunning, bool recoveryPanelOpen)
        {
            ResetBackoff();
            string owner = Owner;
            bool rebuilt = owner != null && WatchingRebuiltHideout(owner);
            Stop();
            if (owner != null)
            {
                Arm(owner, rebuilt);
            }
            else
            {
                EnsureWatcher(gameRunning, recoveryPanelOpen);
            }
        }

        /// <summary>换了存档账号或开关之后，之前因为同一个游戏进程出错而暂停的自动挂接重新开始。</summary>
        public void ResetBackoff()
        {
            _blockedPid = 0;
            _retryUtc = DateTime.MinValue;
        }

        /// <summary>“重开战斗”在刚恢复出的兵营里按下时，作用于重建它的那份战备。</summary>
        public string RebuiltHideoutSnapshot()
        {
            RebuiltHideout hideout = _rebuilt;
            return hideout != null && NativeRecovery.HasDeparture(_host.Settings.StoragePath, hideout.SnapshotId)
                && WatchingRebuiltHideout(hideout.SnapshotId)
                ? hideout.SnapshotId
                : null;
        }

        private bool WatchingRebuiltHideout(string snapshotId)
        {
            try
            {
                return RebuiltHideoutMatches(snapshotId, NativeRecovery.FindGame());
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        /// <summary>
        /// 玩家还站在这份战备刚重建出的兵营里：同一个游戏进程，重建后挂上的那个记录器还在运行，
        /// 已经在兵营里就位，没有抓到过出发，也没有停下。
        /// </summary>
        public bool RebuiltHideoutMatches(string snapshotId, NativeRecovery.GameTarget game)
        {
            RebuiltHideout hideout = _rebuilt;
            if (hideout == null || hideout.SnapshotId != snapshotId || hideout.Pid != game.Pid || hideout.Birth != game.Birth
                || !ReferenceEquals(hideout.Recorder, _process))
            {
                return false;
            }

            try
            {
                if (hideout.Recorder.HasExited)
                {
                    return false;
                }

                bool armed = false;
                using (var stream = new FileStream(hideout.RecorderLog, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.Contains("\"departure-recorder-armed\""))
                        {
                            armed = true;
                        }

                        if (line.Contains("\"departure-captured\"") || line.Contains("\"departure-recorder-stopped\""))
                        {
                            return false;
                        }
                    }
                }

                return armed;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException
                || error is InvalidOperationException)
            {
                return false;
            }
        }

        public void Stop()
        {
            Process recorder = _process;
            _process = null;
            _owner = null;
            _log = null;
            _rebuilt = null;
            if (recorder == null)
            {
                return;
            }

            try
            {
                if (!recorder.HasExited)
                {
                    recorder.StandardInput.Close();
                    if (!recorder.WaitForExit(8000))
                    {
                        recorder.Kill();
                        recorder.WaitForExit(2000);
                    }
                }
            }
            catch (InvalidOperationException)
            {
            }
            catch (Win32Exception)
            {
            }
        }

        private void Start(NativeRecovery.GameTarget game, string owner)
        {
            bool protecting = AutoProtect;
            try
            {
                string logPath;
                Process recorder = NativeRecovery.StartDepartureRecorder(_host.Settings.StoragePath,
                    protecting ? _host.Settings.ProfilePath : null, owner, game.Pid, out logPath);
                _process = recorder;
                _owner = owner;
                _log = logPath;
                recorder.EnableRaisingEvents = true;
                recorder.Exited += (sender, args) =>
                    _host.RunOnUiThread(() => Exited(recorder, owner, protecting, game.Pid, logPath));
            }
            catch (Exception error)
            {
                if (!(error is InvalidOperationException || error is IOException || error is Win32Exception))
                {
                    throw;
                }

                // 同一个游戏进程里重试只会重复同样的错误，等游戏重启或玩家重新开关再试。
                _blockedPid = game.Pid;
                DiagnosticLog.Write("departure-recorder", error);
                _host.Toast("出发记录器没有启动", "danger");
            }
        }

        private void Exited(Process recorder, string owner, bool protecting, int pid, string logPath)
        {
            if (_rebuilt != null && ReferenceEquals(_rebuilt.Recorder, recorder))
            {
                // 没人再看着那个兵营了（出发了、离开了或游戏退出），不再从它直接出发。
                _rebuilt = null;
            }

            if (ReferenceEquals(recorder, _process))
            {
                _process = null;
                _owner = null;
                _log = null;
                int exitCode = -1;
                try
                {
                    exitCode = recorder.ExitCode;
                }
                catch (InvalidOperationException)
                {
                }

                if (protecting && exitCode != 0 && exitCode != 4)
                {
                    // 出错退出（例如游戏还在启动、代码未就绪）时隔一会儿再挂，免得反复重启。
                    _retryUtc = DateTime.UtcNow + WatchBackoff;
                }

                Report(owner, protecting, pid, logPath, exitCode);
            }

            recorder.Dispose();
        }

        private void Report(string owner, bool protecting, int pid, string logPath, int exitCode)
        {
            string storage = _host.Settings.StoragePath;
            string protectedId = protecting ? NativeRecovery.ReadProtectedDeparture(logPath) : null;
            string recordedId = protectedId ?? owner;
            bool recorded = recordedId != null && NativeRecovery.HasDeparture(storage, recordedId);
            if (protectedId != null)
            {
                Result<int> pruned = _host.Store.PruneDepartureSnapshots(_host.Settings.ProfilePath, Retention);
                if (!pruned.Success)
                {
                    DiagnosticLog.Write("departure-retention", pruned.Code + ": " + pruned.Message);
                    _host.Toast("旧的出发前战备没有清理", "neutral");
                }

                _host.Monitor.RefreshProtectedSnapshot();
                _host.ReloadSnapshots(protectedId);
            }

            string restart = (NativeRecovery.RestartHotkeyName != null
                ? "死亡或战斗中按 " + NativeRecovery.RestartHotkeyName + " "
                : "在守护器里") + "可直接重开这场战斗。";
            if (recorded)
            {
                string title = protectedId != null ? "已在出发时保存战备" : "已记录这次出发";
                _host.Toast(title, "signal");
                _host.Notify(title, restart, ToolTipIcon.Info);
                return;
            }

            string reason = NativeRecovery.DescribeRecorderStop(logPath, protecting);
            if (reason != null || (exitCode != 0 && exitCode != 4))
            {
                string stop;
                try
                {
                    stop = NativeRecovery.ReadRecorderStop(logPath);
                }
                catch (Exception error) when (RecoveryJournal.Unreadable(error))
                {
                    stop = "log unreadable: " + error.Message;
                }

                DiagnosticLog.Write("departure-recorder", "exit " + exitCode + ": " + (stop ?? "no stop reason") + " (" + logPath + ")");
            }

            if (protectedId != null)
            {
                reason = "已在出发时保存战备，但这次出发没有记录，重开时需要在兵营手动出发。" + (reason ?? "");
            }

            // 自动保存模式下记录器会反复重启；同一个游戏进程里同样的问题只提示一次。
            string notice = pid + ":" + reason;
            if (reason != null && (!protecting || protectedId != null || notice != _notice))
            {
                _notice = notice;
                _host.Notify(protectedId != null ? "已在出发时保存战备" : protecting ? "这次出发没有自动保存" : "这次出发没有记录",
                    reason, ToolTipIcon.Warning);
            }
        }
    }
}
