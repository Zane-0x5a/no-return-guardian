using NoReturnGuardian.Core;
using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace NoReturnGuardian
{
    /// <summary>
    /// “自动清理”是玩家的明确选择（2026-10-01）：开启时先列出要删的快照请玩家确认，焦点默认在取消；
    /// 开着时快照一有变化，就在下一次轮询里删掉每类最近三份以外、且没有在用的旧快照。
    /// 有未闭合的恢复事务、原生恢复进行中或恢复面板开着时不动；删除失败就隔一段时间再试，不打扰玩家。
    /// </summary>
    internal sealed class AutoCleanup
    {
        private const int KeepPerKind = 3;
        private static readonly TimeSpan Backoff = TimeSpan.FromMinutes(10);
        private readonly IGuardianHost _host;
        private readonly Func<IEnumerable<string>> _pinned;
        private readonly bool _enabled;
        private SnapshotCleanupPlan _plan;
        private string _token;
        private bool _due;
        private DateTime _retryUtc;

        /// <param name="pinned">正在用的快照，无论多旧都不删。</param>
        /// <param name="enabled">截图模式下为 false：从不删除。</param>
        public AutoCleanup(IGuardianHost host, Func<IEnumerable<string>> pinned, bool enabled)
        {
            _host = host;
            _pinned = pinned;
            _enabled = enabled;
        }

        /// <summary>正在删除；这期间不恢复、不删除别的快照。</summary>
        public bool Busy { get; private set; }

        /// <summary>快照列表变了，下次轮询时看看要不要清理。</summary>
        public void MarkDue()
        {
            _due = true;
        }

        /// <summary>
        /// 打开：要删的旧快照先给玩家确认，确认后才开启并删除；没有要删的就直接开启。关闭只是不再自动清理，不删除任何东西。
        /// </summary>
        public void Toggle(bool enabled)
        {
            GuardianSettings settings = _host.Settings;
            if (!enabled || Busy)
            {
                if (!enabled && settings.AutoCleanup)
                {
                    settings.AutoCleanup = false;
                    _host.SaveSettings();
                }

                _host.PublishState();
                return;
            }

            if (_host.RestoreUnfinished)
            {
                _host.Toast("上次恢复还没完成，暂时不能开启自动清理", "danger");
                _host.PublishState();
                return;
            }

            Result<SnapshotCleanupPlan> planned = _host.Store.PlanRecentCleanup(settings.ProfilePath, KeepPerKind, _pinned());
            if (!planned.Success)
            {
                _host.ShowResult(planned.Code, planned.Message, false);
                _host.PublishState();
                return;
            }

            SnapshotCleanupPlan plan = planned.Value;
            if (plan.Removals.Count == 0)
            {
                settings.AutoCleanup = true;
                _host.SaveSettings();
                _host.PublishState();
                return;
            }

            _plan = plan;
            _token = Guid.NewGuid().ToString("N");
            _host.ShowSheet(GuardianView.Map(
                "kind", "cleanup",
                "token", _token,
                "removals", plan.Removals.Count,
                "size", GuardianView.FormatBytes(plan.RemovalBytes)));
        }

        public void Confirm(string token)
        {
            SnapshotCleanupPlan plan = _plan;
            if (Busy || plan == null || token == null || token != _token)
            {
                return;
            }

            _plan = null;
            _token = null;
            if (_host.RestoreUnfinished)
            {
                _host.Toast("上次恢复还没完成，暂时不能清理", "danger");
                return;
            }

            _host.Settings.AutoCleanup = true;
            _host.SaveSettings();
            Start(plan, true);
        }

        /// <param name="blocked">原生恢复进行中或恢复面板开着。</param>
        public void RunIfDue(bool blocked)
        {
            GuardianSettings settings = _host.Settings;
            if (!_due || !_enabled || !settings.AutoCleanup || Busy || string.IsNullOrWhiteSpace(settings.ProfilePath)
                || DateTime.UtcNow < _retryUtc || blocked || _host.RestoreUnfinished)
            {
                return;
            }

            _due = false;
            Result<SnapshotCleanupPlan> planned = _host.Store.PlanRecentCleanup(settings.ProfilePath, KeepPerKind, _pinned());
            if (!planned.Success)
            {
                DiagnosticLog.Write("auto-cleanup", planned.Code + ": " + planned.Message);
                _retryUtc = DateTime.UtcNow + Backoff;
                return;
            }

            if (planned.Value.Removals.Count > 0)
            {
                Start(planned.Value, false);
            }
        }

        private void Start(SnapshotCleanupPlan plan, bool announce)
        {
            Busy = true;
            _host.PublishState();
            var worker = new BackgroundWorker();
            worker.DoWork += (sender, args) => args.Result = _host.Store.CleanupToRecent(plan);
            worker.RunWorkerCompleted += (sender, args) =>
            {
                Busy = false;
                worker.Dispose();
                Result<SnapshotCleanupOutcome> result = args.Error == null
                    ? (Result<SnapshotCleanupOutcome>)args.Result
                    : Result<SnapshotCleanupOutcome>.Fail("snapshot_cleanup_failed", args.Error.Message);
                if (!result.Success)
                {
                    _retryUtc = DateTime.UtcNow + Backoff;
                    DiagnosticLog.Write("auto-cleanup", result.Code + ": " + result.Message);
                }

                if (announce)
                {
                    if (result.Success)
                    {
                        _host.Toast("已开启自动清理，删除了 " + result.Value.Removed + " 份旧快照", "neutral");
                    }
                    else
                    {
                        _host.ShowResult(result.Code, result.Message, false);
                    }
                }

                _host.Monitor.RefreshProtectedSnapshot();
                _host.RefreshUndoPoint();
                _host.ReloadSnapshots(null);
            };
            worker.RunWorkerAsync();
        }
    }
}
