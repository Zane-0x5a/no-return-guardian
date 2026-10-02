using NoReturnGuardian.Core;
using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace NoReturnGuardian
{
    /// <summary>
    /// 主窗口替出发记录器、原生恢复和自动清理做的事：保存设置、刷新快照库、给玩家看的提示。
    /// 控制器只在界面线程上调用它；后台线程的结果先经 RunOnUiThread 回到界面线程。
    /// </summary>
    internal interface IGuardianHost
    {
        GuardianSettings Settings { get; }
        SnapshotStore Store { get; }
        GuardianMonitor Monitor { get; }

        /// <summary>有未闭合的恢复事务，或实验性暖恢复还没结束。</summary>
        bool RestoreUnfinished { get; }

        void SaveSettings();

        /// <summary>重新读快照库，并选中 focusId（为空时保留当前选择）。</summary>
        void ReloadSnapshots(string focusId);

        /// <summary>“撤销最近恢复”改为指向最新的一份恢复前现场。</summary>
        void RefreshUndoPoint();

        /// <summary>记下最近恢复的战备（持久化）；自动清理不删它。</summary>
        void RememberRestored(string snapshotId);

        void PublishState();

        void Toast(string text, string tone);

        /// <summary>Core 操作结果的玩家可读说明（GuardianView.FriendlyMessage）；失败的原话进诊断记录。</summary>
        void ShowResult(string code, string detail, bool success);

        /// <summary>托盘气泡；游戏在前台时玩家看到的就是它。给了 clicked 时，点气泡执行它。</summary>
        void Notify(string title, string text, ToolTipIcon icon, Action clicked = null);

        /// <summary>恢复面板的进度：running、done、failed。</summary>
        void PostRecovery(string phase, string text);

        void ShowSheet(Dictionary<string, object> sheet);

        void RunOnUiThread(Action action);
    }
}
