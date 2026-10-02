using NoReturnGuardian.Core;
using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace NoReturnGuardian
{
    internal sealed class WarmRestoreDialog : Form
    {
        private readonly WarmRestoreController _controller;
        private readonly WarmRestoreAutomationController _automation;
        private readonly string _profilePath;
        private readonly string _initialSnapshotId;
        private readonly Label _phaseLabel;
        private readonly Label _bodyLabel;
        private readonly CheckBox _confirmation;
        private readonly CommandButton _primaryButton;
        private readonly LedgerButton _manualButton;
        private readonly LedgerButton _closeButton;
        private Func<Result<WarmRestoreOutcome>> _operation;
        private bool _busy;

        public WarmRestoreDialog(
            WarmRestoreController controller,
            WarmRestoreAutomationController automation,
            string profilePath,
            string snapshotId,
            bool renderOnly = false)
        {
            _controller = controller ?? throw new ArgumentNullException("controller");
            _automation = automation ?? throw new ArgumentNullException("automation");
            _profilePath = profilePath;
            _initialSnapshotId = snapshotId;

            Text = "游戏内暖恢复";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(680, 458);
            BackColor = UiPalette.Canvas;
            ForeColor = UiPalette.Ink;
            Font = UiPalette.Ui(9.5f, FontStyle.Regular);

            Panel root = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(28, 24, 28, 22),
                BackColor = UiPalette.Canvas
            };

            Label title = new Label
            {
                AutoSize = false,
                Text = "游戏内暖恢复",
                Font = UiPalette.Ui(16f, FontStyle.Bold),
                ForeColor = UiPalette.Ink,
                Location = new Point(0, 0),
                Size = new Size(620, 34)
            };
            _phaseLabel = new Label
            {
                AutoSize = false,
                Font = UiPalette.Ui(11.5f, FontStyle.Bold),
                ForeColor = UiPalette.Amber,
                Location = new Point(0, 62),
                Size = new Size(620, 30)
            };
            _bodyLabel = new Label
            {
                AutoSize = false,
                Font = UiPalette.Ui(10f, FontStyle.Regular),
                ForeColor = UiPalette.InkDim,
                Location = new Point(0, 102),
                Size = new Size(620, 148)
            };
            Panel line = new Panel
            {
                BackColor = UiPalette.Line,
                Location = new Point(0, 268),
                Size = new Size(620, 1)
            };
            _confirmation = new CheckBox
            {
                AutoSize = false,
                FlatStyle = FlatStyle.Flat,
                ForeColor = UiPalette.Ink,
                BackColor = UiPalette.Canvas,
                Location = new Point(0, 288),
                Size = new Size(620, 42),
                TextAlign = ContentAlignment.MiddleLeft
            };
            _confirmation.CheckedChanged += (sender, args) => UpdatePrimaryAvailability();

            FlowLayoutPanel commands = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                BackColor = UiPalette.Canvas,
                Location = new Point(0, 364),
                Size = new Size(620, 48),
                Padding = new Padding(0),
                Margin = new Padding(0)
            };
            _closeButton = new LedgerButton(null, "关闭")
            {
                Margin = new Padding(10, 5, 0, 0)
            };
            _closeButton.Click += (sender, args) => Close();
            _manualButton = new LedgerButton(null, "手动打开模式")
            {
                Margin = new Padding(10, 5, 0, 0),
                Visible = false
            };
            _manualButton.Click += (sender, args) => ConfigureManualStart();
            _primaryButton = new CommandButton("restore", "继续")
            {
                Margin = new Padding(0, 0, 0, 0)
            };
            _primaryButton.MarkPrimary();
            _primaryButton.Click += (sender, args) => RunCurrentOperation();
            commands.Controls.Add(_closeButton);
            commands.Controls.Add(_manualButton);
            commands.Controls.Add(_primaryButton);

            root.Controls.Add(title);
            root.Controls.Add(_phaseLabel);
            root.Controls.Add(_bodyLabel);
            root.Controls.Add(line);
            root.Controls.Add(_confirmation);
            root.Controls.Add(commands);
            Controls.Add(root);

            if (!renderOnly)
            {
                Shown += (sender, args) => RefreshPhase(null);
            }
            FormClosing += HandleFormClosing;
        }

        public bool OperationChanged { get; private set; }
        public string LastStatus { get; private set; }
        public bool LastSucceeded { get; private set; }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            UiWindow.ApplyDarkChrome(this);
        }

        public void RenderToPng(string outputPath, string phase, int dpi = 0)
        {
            string fullPath = System.IO.Path.GetFullPath(outputPath);
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath));
            PrepareRenderPhase(phase);
            Show();
            Application.DoEvents();
            Refresh();
            using (Bitmap bitmap = new Bitmap(Width, Height))
            {
                if (dpi >= 96)
                {
                    bitmap.SetResolution(dpi, dpi);
                }

                DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height));
                bitmap.Save(fullPath, System.Drawing.Imaging.ImageFormat.Png);
            }

            _busy = false;
            Close();
        }

        private void PrepareRenderPhase(string phase)
        {
            string normalized = string.IsNullOrWhiteSpace(phase)
                ? "start"
                : phase.Trim().ToLowerInvariant();
            switch (normalized)
            {
                case "manual":
                    ConfigureManualStart();
                    break;
                case "error":
                    ConfigureAutomaticStart(
                        "没有确认进入 T2R 制作人员名单，目标战备未写入。请回到主菜单重试。");
                    break;
                case "busy":
                    ConfigureAutomaticStart(null);
                    _busy = true;
                    _confirmation.Checked = true;
                    _confirmation.AutoCheck = false;
                    _closeButton.Enabled = false;
                    _primaryButton.Text = "正在自动导航";
                    UpdatePrimaryAvailability();
                    break;
                case "staged":
                    ShowJournalState(
                        new WarmRestoreJournal { State = WarmRestorePhases.TargetStagedInCredits },
                        null);
                    break;
                case "main-menu":
                    ShowJournalState(
                        new WarmRestoreJournal { State = WarmRestorePhases.MainMenuConfirmed },
                        null);
                    break;
                case "hideout":
                    ShowJournalState(
                        new WarmRestoreJournal { State = WarmRestorePhases.TargetLineagePendingPlayer },
                        null);
                    break;
                case "rejected":
                    ShowJournalState(
                        new WarmRestoreJournal
                        {
                            State = WarmRestorePhases.Rejected,
                            LastFailureCode = "warm_restore_rejected"
                        },
                        null);
                    break;
                case "accepted":
                    ConfigureTerminal(
                        "暖恢复已完成",
                        "暖恢复已由兵营代次与交易站状态共同验收。",
                        true);
                    break;
                default:
                    ConfigureAutomaticStart(null);
                    break;
            }
        }

        private void RefreshPhase(string error)
        {
            Result<WarmRestoreJournal> active = _controller.GetActiveOperation(_profilePath);
            if (!active.Success)
            {
                ConfigureTerminal(
                    "暖恢复日志不可用",
                    WarmMessage(active.Code, active.Message),
                    false);
                return;
            }

            if (active.Value == null)
            {
                if (!string.IsNullOrWhiteSpace(error))
                {
                    ConfigureAutomaticStart(error);
                    return;
                }

                if (string.IsNullOrWhiteSpace(_initialSnapshotId))
                {
                    ConfigureTerminal("没有目标战备", "关闭窗口并先选择一份可恢复战备。", false);
                    return;
                }

                ConfigureAutomaticStart(null);
                return;
            }

            ShowJournalState(active.Value, error);
        }

        private void ConfigureAutomaticStart(string error)
        {
            string body = "守护器会从主菜单自动打开 T2R 制作人员名单，用影片资源确认到达后才暂存目标，再自动退出并校验主菜单代次。焦点、菜单或到达判据有任何偏差都会停手；到达名单前绝不写入存档。";
            if (!string.IsNullOrWhiteSpace(error))
            {
                body = error + "\n守护器已经停手；确认到达名单前没有写入存档。";
            }

            ConfigureStep(
                "1 / 4 · 一键完成名单暂存",
                body,
                "我确认游戏已停在主菜单，可以交给守护器操作",
                "自动导航并暖恢复",
                () => _automation.StartFromMainMenu(
                    _profilePath,
                    _initialSnapshotId));
            _manualButton.Visible = true;
        }

        private void ConfigureManualStart()
        {
            ConfigureStep(
                "1 / 4 · 手动名单暂存",
                "仅在自动导航无法取得游戏窗口焦点时使用。打开 T2R 制作人员名单并停留在播放页面；守护器仍会验证完整原生导出、游戏版本和目标战备，再暂停进程完成事务暂存。",
                "我确认当前已打开 T2R 制作人员名单页面",
                "暂存所选战备",
                () => _controller.Stage(_profilePath, _initialSnapshotId));
        }

        private void ShowJournalState(WarmRestoreJournal journal, string error)
        {
            string failure = string.IsNullOrWhiteSpace(error)
                ? WarmMessage(journal.LastFailureCode, journal.LastFailureMessage)
                : error;
            switch (journal.State)
            {
                case WarmRestorePhases.ReadyToSuspend:
                case WarmRestorePhases.ProcessSuspended:
                case WarmRestorePhases.ResumeRequired:
                    bool running = new GameProcessProbe().IsGameRunning();
                    ConfigureStep(
                        "暖恢复在暂存阶段中断",
                        string.IsNullOrWhiteSpace(failure)
                            ? "守护器检测到可恢复的阶段日志。继续后会先处理进程暂停状态和磁盘事务，再决定保留目标或回滚。"
                            : failure,
                        running ? "我确认继续处理当前游戏进程" : "我确认游戏已经完全退出",
                        running ? "恢复暂存流程" : "恢复原现场",
                        running
                            ? (Func<Result<WarmRestoreOutcome>>)(() =>
                                _controller.ResumeInterruptedStage(_profilePath))
                            : () => _controller.RecoverAfterFullExit(_profilePath));
                    break;

                case WarmRestorePhases.TargetStagedInCredits:
                    ConfigureStep(
                        "2 / 4 · 返回主菜单",
                        "目标战备已经在制作人员名单页面完成事务写入并校验。现在正常退出制作人员名单，停在游戏主菜单；不要先进入赴死之旅。",
                        "我确认已从 T2R 制作人员名单回到主菜单",
                        "校验主菜单",
                        () => _controller.CheckpointMainMenu(_profilePath));
                    break;

                case WarmRestorePhases.MainMenuConfirmed:
                    ConfigureStep(
                        "3 / 4 · 进入兵营验证代次",
                        "主菜单上的磁盘代次已经确认属于目标战备。现在进入赴死之旅并停在兵营，不要开始下一场遭遇。",
                        "我确认已经进入赴死之旅兵营",
                        "验证兵营代次",
                        () => _controller.VerifyHideout(_profilePath));
                    break;

                case WarmRestorePhases.TargetLineagePendingPlayer:
                    ConfigureStep(
                        "4 / 4 · 验收交易站",
                        "守护器已经锁定目标代次进入兵营的证据。请检查货币、库存、武器升级和交易站；正常操作导致的字节变化不会抹掉这份已锁定证据。",
                        "我确认货币、库存、升级和交易站均正常",
                        "确认暖恢复完成",
                        () => _controller.AcceptTradingStation(_profilePath));
                    break;

                case WarmRestorePhases.Rejected:
                    ConfigureStep(
                        "暖恢复未通过代次验收",
                        string.IsNullOrWhiteSpace(failure)
                            ? "游戏没有载入所选目标代次。请完整退出游戏，再恢复暖恢复前的原现场。"
                            : failure + "\n\n请完整退出游戏，再恢复暖恢复前的原现场。",
                        "我确认游戏已经完全退出",
                        "恢复原现场",
                        () => _controller.RecoverAfterFullExit(_profilePath));
                    break;

                case WarmRestorePhases.StageFailed:
                    ConfigureTerminal(
                        "暂存失败 · 原现场已恢复",
                        string.IsNullOrWhiteSpace(failure)
                            ? "目标没有被保留，原始磁盘现场已经恢复。"
                            : failure,
                        false);
                    break;

                default:
                    ConfigureTerminal(
                        "暖恢复状态需要处理",
                        string.IsNullOrWhiteSpace(failure)
                            ? "当前阶段无法继续，请完整退出游戏后恢复原现场。"
                            : failure,
                        false);
                    break;
            }
        }

        private void ConfigureStep(
            string phase,
            string body,
            string confirmation,
            string command,
            Func<Result<WarmRestoreOutcome>> operation)
        {
            _phaseLabel.Text = phase;
            _phaseLabel.ForeColor = UiPalette.Amber;
            _bodyLabel.Text = body;
            _confirmation.Visible = true;
            _confirmation.Checked = false;
            _confirmation.AutoCheck = true;
            _confirmation.Enabled = true;
            _confirmation.Text = confirmation;
            _primaryButton.Visible = true;
            _primaryButton.Text = command;
            _operation = operation;
            _closeButton.Text = "稍后继续";
            _manualButton.Visible = false;
            UpdatePrimaryAvailability();
        }

        private void ConfigureTerminal(string phase, string body, bool success)
        {
            _phaseLabel.Text = phase;
            _phaseLabel.ForeColor = success ? UiPalette.Amber : UiPalette.Danger;
            _bodyLabel.Text = body;
            _confirmation.Visible = false;
            _primaryButton.Visible = false;
            _manualButton.Visible = false;
            _operation = null;
            _closeButton.Text = "关闭";
        }

        private void UpdatePrimaryAvailability()
        {
            _primaryButton.Enabled = !_busy
                && _operation != null
                && (!_confirmation.Visible || _confirmation.Checked);
            _manualButton.Enabled = !_busy;
        }

        private void RunCurrentOperation()
        {
            if (_busy || _operation == null || !_primaryButton.Enabled)
            {
                return;
            }

            Func<Result<WarmRestoreOutcome>> operation = _operation;
            _busy = true;
            _confirmation.AutoCheck = false;
            _closeButton.Enabled = false;
            _manualButton.Enabled = false;
            _primaryButton.Text = "正在校验";
            UpdatePrimaryAvailability();

            ThreadPool.QueueUserWorkItem(state =>
            {
                Result<WarmRestoreOutcome> result;
                try
                {
                    result = operation();
                }
                catch (Exception exception)
                {
                    result = Result<WarmRestoreOutcome>.Fail(
                        "warm_restore_ui_operation_failed",
                        exception.Message);
                }

                if (!IsDisposed && IsHandleCreated)
                {
                    BeginInvoke(new Action(() => CompleteOperation(result)));
                }
            });
        }

        private void CompleteOperation(Result<WarmRestoreOutcome> result)
        {
            _busy = false;
            _confirmation.AutoCheck = true;
            _closeButton.Enabled = true;
            _manualButton.Enabled = true;
            OperationChanged = true;
            LastSucceeded = result.Success;
            LastStatus = WarmMessage(result.Code, result.Message);

            if (result.Success
                && result.Value != null
                && result.Value.Journal != null
                && (string.Equals(
                        result.Value.Journal.State,
                        WarmRestorePhases.Accepted,
                        StringComparison.Ordinal)
                    || string.Equals(
                        result.Value.Journal.State,
                        WarmRestorePhases.Recovered,
                        StringComparison.Ordinal)))
            {
                ConfigureTerminal(
                    string.Equals(
                        result.Value.Journal.State,
                        WarmRestorePhases.Accepted,
                        StringComparison.Ordinal)
                        ? "暖恢复已完成"
                        : "原现场已恢复",
                    LastStatus,
                    true);
                return;
            }

            RefreshPhase(result.Success ? null : LastStatus);
        }

        private void HandleFormClosing(object sender, FormClosingEventArgs e)
        {
            if (_busy)
            {
                e.Cancel = true;
                _bodyLabel.Text = "当前阶段正在执行。进程暂停、写入和恢复校验完成前不能关闭此窗口。";
                _phaseLabel.ForeColor = UiPalette.Danger;
            }
        }

        private static string WarmMessage(string code, string detail)
        {
            switch (code)
            {
                case "warm_restore_target_staged": return "所选战备已暂存并校验，请正常返回主菜单。";
                case "warm_restore_preflight_ready": return "目标、当前导出与游戏版本已通过自动导航前检。";
                case "warm_navigation_window_missing": return "没有找到属于游戏进程的可见窗口，未发送任何按键。";
                case "warm_navigation_focus_failed": return "无法把焦点交给游戏，未发送任何按键。";
                case "warm_navigation_focus_lost": return "导航期间游戏失去前台焦点，守护器已立即停手。";
                case "warm_navigation_credits_not_reached": return "没有确认进入 T2R 制作人员名单，目标战备未写入。请回到主菜单重试。";
                case "warm_navigation_resource_probe_failed": return "Windows 无法确认 T2R 名单影片归属于游戏进程，未继续自动流程。";
                case "warm_navigation_credits_exit_timeout": return "目标已安全暂存，但自动退出名单失败；请手动回到主菜单后继续。";
                case "warm_restore_main_menu_confirmed": return "主菜单代次已确认，请进入赴死之旅兵营。";
                case "warm_restore_hideout_verified": return "目标代次已在兵营确认，等待交易站验收。";
                case "warm_restore_accepted": return "暖恢复已由兵营代次与交易站状态共同验收。";
                case "warm_restore_original_recovered": return "暖恢复前的原始现场已经恢复。";
                case "warm_restore_source_not_exported": return "当前仍有兵营工作包；请先退回主菜单并打开 T2R 制作人员名单。";
                case "warm_restore_source_partial_working_envelope": return "游戏正在切换存档形态，请等待后重试。";
                case "warm_restore_native_source_incomplete": return "制作人员名单页的原生导出还不完整，请稍后重试。";
                case "game_build_unrecognized": return "当前游戏版本未通过暖恢复静态验证，未执行写入。";
                case "game_not_running": return "未找到正在运行的游戏进程。";
                case "warm_restore_rejected": return "游戏重新进入赴死之旅后没有建立目标代次。";
                case "warm_restore_hideout_not_ready": return "当前不是稳定的非战斗兵营状态，尚不能验收。";
                case "warm_restore_lineage_changed": return "当前兵营代次已偏离已锁定的目标证据，未完成验收。";
                case "game_running": return "游戏仍在运行，只有完整退出后才能恢复原现场。";
                default: return string.IsNullOrWhiteSpace(detail) ? "暖恢复操作未完成。" : detail;
            }
        }
    }
}
