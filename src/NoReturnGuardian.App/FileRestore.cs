using NoReturnGuardian.Core;
using System;
using System.Linq;

namespace NoReturnGuardian
{
    /// <summary>
    /// 游戏完整退出后的恢复：把战备写回存档、撤销最近一次恢复、回滚上次中断的恢复。写入都走 SnapshotStore 的事务，
    /// 游戏在运行时一律拒绝；每次恢复前的现场都会先留档，“撤销最近恢复”指向最新的那份。
    /// </summary>
    internal sealed class FileRestore
    {
        private readonly IGuardianHost _host;
        private readonly IGameProcessProbe _game;
        private readonly Action _showWindow;
        private bool _interruptedOffered;

        /// <param name="showWindow">中断的恢复需要玩家确认时，把窗口拿出来。</param>
        public FileRestore(IGuardianHost host, IGameProcessProbe game, Action showWindow)
        {
            _host = host;
            _game = game;
            _showWindow = showWindow;
            RefreshUndoPoint();
        }

        /// <summary>“撤销最近恢复”要写回的恢复前现场；没有时为 null。</summary>
        public string UndoSnapshotId { get; private set; }

        public void RefreshUndoPoint()
        {
            SnapshotRecord latestUndo = _host.Store.ListSnapshots().FirstOrDefault(
                item => item.StructurallyValid
                    && item.Manifest != null
                    && item.Manifest.SchemaVersion >= 2
                    && string.Equals(item.Manifest.Purpose, SnapshotPurposes.LiveState, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(item.Manifest.Kind, SnapshotKinds.PreRestore, StringComparison.OrdinalIgnoreCase));
            UndoSnapshotId = latestUndo == null ? null : latestUndo.Manifest.Id;
        }

        public void Restore(SnapshotRecord selected)
        {
            if (selected == null || !SnapshotPolicy.IsConfirmedPreparation(selected))
            {
                _host.Toast("这份快照不能恢复", "danger");
                return;
            }

            if (_game.IsGameRunning())
            {
                _host.Toast("游戏还在运行，请先完整退出游戏", "danger");
                return;
            }

            Result<RestoreOutcome> result = _host.Store.Restore(selected.Manifest.Id, _host.Settings.ProfilePath, _game, null);
            if (result.Success)
            {
                UndoSnapshotId = result.Value.UndoSnapshotId;
                _host.RememberRestored(selected.Manifest.Id);
                _host.ReloadSnapshots(selected.Manifest.Id);
                _host.ShowSheet(GuardianView.Map("kind", "written", "time", GuardianView.TimeOf(selected.CreatedUtc.ToLocalTime())));
            }
            else
            {
                DiagnosticLog.Write("restore", result.Code + ": " + result.Message);
                _host.ReloadSnapshots(null);
                _host.ShowSheet(GuardianView.Map(
                    "kind", "failed",
                    "title", "恢复没有完成",
                    "text", GuardianView.FriendlyMessage(result.Code, result.Message)));
            }
        }

        public void Undo()
        {
            if (string.IsNullOrWhiteSpace(UndoSnapshotId))
            {
                _host.Toast("没有可以撤销的恢复", "neutral");
                return;
            }

            if (_game.IsGameRunning())
            {
                _host.Toast("游戏还在运行，请先完整退出游戏", "danger");
                return;
            }

            Result<RestoreOutcome> result = _host.Store.Undo(UndoSnapshotId, _host.Settings.ProfilePath, _game);
            _host.ShowResult(result.Code, result.Message, result.Success);
            if (result.Success)
            {
                UndoSnapshotId = result.Value.UndoSnapshotId;
                _host.ReloadSnapshots(null);
            }
        }

        /// <summary>
        /// 上次恢复中断且游戏已退出时，请玩家确认回滚；游戏仍在运行时只在状态里阻断。暖恢复还没结束时由它自己的对话框处理。
        /// </summary>
        public void OfferInterrupted(bool pageReady, bool warmActive)
        {
            if (_interruptedOffered || !pageReady || warmActive || !_host.Store.HasPendingRestore() || _game.IsGameRunning())
            {
                return;
            }

            _interruptedOffered = true;
            _showWindow();
            _host.ShowSheet(GuardianView.Map("kind", "interrupted"));
        }

        public void ConfirmInterrupted()
        {
            if (!_host.Store.HasPendingRestore())
            {
                return;
            }

            Result<RestoreOutcome> recovery = _host.Store.RecoverInterruptedRestore(_game);
            if (recovery.Success)
            {
                _host.Toast("已回滚到恢复之前", "signal");
            }
            else
            {
                _host.ShowResult(recovery.Code, recovery.Message, false);
            }

            _interruptedOffered = false;
            RefreshUndoPoint();
            _host.ReloadSnapshots(null);
        }
    }
}
