using NoReturnGuardian.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NoReturnGuardian
{
    /// <summary>界面截图模式的参数：只渲染、截图后退出，不注册托盘、快捷键，也不执行任何写入。</summary>
    internal sealed class RenderOptions
    {
        public string Path;
        public int Width;
        public int Height;
        public string DevUrl;
        public string Script;
        public int WaitMs;
        public int HideSeconds;
    }

    /*
     * 界面是 WebView2 里的网页（src/NoReturnGuardian.Web），背景色场由 Paper Shaders 绘制。
     * 这个窗口只是外壳和控制器——守护状态、门控与事务都留在宿主，页面只发命令、只读状态。
     * 原生恢复（NativeRecoveryCoordinator）、出发记录器（DepartureRecorder）和自动清理（AutoCleanup）各自成块，
     * 经 IGuardianHost 借用这里的提示与刷新；推给页面的状态由 GuardianView.State 投影。
     * 窗口无系统标题栏：顶部非客户区被并进页面，左右下仍保留系统的隐形缩放边框（WindowChrome）。
     */
    internal sealed class MainForm : Form, IGuardianHost
    {
        private static readonly bool WarmRestoreProductEntryEnabled = false;
        private static readonly Color Canvas = Color.FromArgb(6, 6, 6);
        private readonly bool _startMinimized;
        private bool _startedHidden;
        private bool _started;
        private Size _minimumTrackSize;
        private readonly RenderOptions _render;
        private readonly SettingsStore _settingsStore;
        private readonly SaveLocator _saveLocator;
        private readonly IGameProcessProbe _gameProbe;
        private readonly Timer _pollTimer;
        private readonly Timer _suspendTimer;
        private readonly StateTranscript _stateTranscript;
        private readonly WebShell _shell;
        private GuardianSettings _settings;
        private SnapshotStore _snapshotStore;
        private GuardianMonitor _monitor;
        private WarmRestoreController _warmRestore;
        private DepartureRecorder _recorder;
        private NativeRecoveryCoordinator _native;
        private AutoCleanup _cleanup;
        private FileRestore _files;
        private GuardianTray _tray;
        private MonitorStatus _lastMonitorStatus;
        private IList<SnapshotRecord> _records = new List<SnapshotRecord>();
        private bool _allowExit;
        private bool _polling;
        private bool _trayHintShown;
        private bool _pageReady;
        private bool _renderStarted;
        private string _lastStateJson;
        private string _focusId;
        private int _focusNonce = 1;
        private bool _recoveryHotkeyRegistered;
        private bool _restartHotkeyRegistered;
        private System.Threading.EventWaitHandle _showSignal;
        private System.Threading.RegisteredWaitHandle _showWait;
        private System.Threading.EventWaitHandle _exitSignal;
        private System.Threading.RegisteredWaitHandle _exitWait;
        private volatile bool _exitRequested;
        private const int RecoveryHotkeyId = 0x4E52;
        private const int RestartHotkeyId = 0x4E53;
        private const string RecoveryHotkeyName = "Ctrl+Alt+F9";
        private const string RestartHotkeyName = "Ctrl+Alt+F10";
        private const int WmHotkey = 0x0312;
        private const int WmClose = 0x0010;
        private const uint ModAlt = 0x0001;
        private const uint ModControl = 0x0002;
        private const uint ModNoRepeat = 0x4000;
        private const int LogicalWidth = 1040;
        private const int LogicalHeight = 680;
        private const int LogicalMinWidth = 880;
        private const int LogicalMinHeight = 580;

        public MainForm(bool startMinimized, RenderOptions render = null)
        {
            _startMinimized = startMinimized;
            _render = render;
            string localRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NoReturnGuardian");
            if (_render == null)
            {
                DiagnosticLog.Initialize(localRoot);
            }

            _settingsStore = new SettingsStore(Path.Combine(localRoot, "settings.json"));
            _stateTranscript = new StateTranscript(Path.Combine(localRoot, "state-transcript.tsv"));
            _settings = _settingsStore.Load();
            _saveLocator = new SaveLocator(null);
            _gameProbe = new GameProcessProbe();
            if (_render == null)
            {
                ResolveProfile();
            }

            InitializeServices();
            _files = new FileRestore(this, _gameProbe, ShowFromTray);
            _recorder = new DepartureRecorder(this, _render == null);
            _native = new NativeRecoveryCoordinator(this, _recorder);
            _cleanup = new AutoCleanup(this, PinnedSnapshots, _render == null);

            _pollTimer = new Timer { Interval = _settings.PollIntervalMs };
            _pollTimer.Tick += (sender, args) => PollNow();
            _suspendTimer = new Timer { Interval = 4000 };
            _suspendTimer.Tick += (sender, args) =>
            {
                _suspendTimer.Stop();
                _shell.Suspend();
            };

            // 截图模式用独立的数据目录和参数：不与正在运行的守护器共用浏览器进程，被遮挡时也照常渲染。
            _shell = _render == null
                ? new WebShell(null, Path.Combine(localRoot, "WebView2"), null)
                : new WebShell(
                    _render.DevUrl,
                    Path.Combine(Path.GetTempPath(), "NoReturnGuardian-render-webview2"),
                    _render.HideSeconds > 0
                        ? null
                        : "--disable-features=CalculateNativeWinOcclusion --disable-backgrounding-occluded-windows --disable-renderer-backgrounding");
            _shell.Message += HandleCommand;
            _shell.Failed += HandleShellFailed;

            BuildWindow();
            if (_render == null)
            {
                _tray = new GuardianTray(ShowFromTray, ProtectFromTray, ExitApplication);
                ListenForShowRequests();
                StartupEntry.Refresh();
            }

            WireServiceEvents();
            LoadSnapshots(null);

            Shown += HandleShown;
            FormClosing += HandleFormClosing;
            // WinForms 隐藏父窗口时不会通知子控件，WebView2 会一直以为自己可见、色场照常渲染。
            // 隐藏前先把它设为不可见（见 SetVisibleCore），最小化时也一样，页面才会停帧、休眠才能生效。
            Resize += (sender, args) => _shell.SetVisible(Visible && WindowState != FormWindowState.Minimized);
            VisibleChanged += (sender, args) =>
            {
                if (Visible)
                {
                    _suspendTimer.Stop();
                    _lastStateJson = null;
                    PublishState();
                }
                else if (_render == null)
                {
                    // 恢复面板开着就藏到托盘时，不再挡着出发记录器；真开始恢复时会重新停掉它。
                    _native.Close();
                    _suspendTimer.Start();
                }
            };
        }

        private void BuildWindow()
        {
            SuspendLayout();
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Canvas;
            ForeColor = Color.FromArgb(237, 231, 220);
            Text = "赴死之旅守护器";
            Icon = GuardianTray.LoadIcon("guardian", Size.Empty);
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = _render == null ? FormStartPosition.CenterScreen : FormStartPosition.Manual;
            if (_render != null)
            {
                ShowInTaskbar = false;
                Location = new Point(-32000, -32000);
            }

            Controls.Add(_shell.Control);
            ResumeLayout(false);
        }

        // 截图模式的窗口在屏幕外，显示时不激活，不从游戏手里抢前台。
        protected override bool ShowWithoutActivation
        {
            get { return _render != null; }
        }

        private void ResolveProfile()
        {
            Result<string> profile = _saveLocator.ResolveProfile(_settings.ProfilePath);
            if (profile.Success)
            {
                _settings.ProfilePath = profile.Value;
                _settingsStore.Save(_settings);
            }
        }

        private void InitializeServices()
        {
            _snapshotStore = new SnapshotStore(_settings.StoragePath, new SystemClock())
            {
                AutomaticRetention = _settings.AutomaticRetention
            };
            _warmRestore = new WarmRestoreController(_snapshotStore, new SystemClock());
            _monitor = new GuardianMonitor(_snapshotStore, _gameProbe, new SystemClock())
            {
                AutoMonitor = false,
                StabilityDelay = TimeSpan.FromMilliseconds(_settings.StabilityDelayMs)
            };
            _monitor.SetProfile(_settings.ProfilePath);
        }

        // ---------- 窗口外壳 ----------

        protected override void SetVisibleCore(bool value)
        {
            // 最小化启动时，Application.Run 的第一次显示请求不真的显示：窗口哪怕只闪一下，也会抢走玩家正在用的程序的焦点。
            // 只建好窗口和浏览器的句柄，在隐藏状态下初始化，之后在托盘里待命；第一次真正显示时页面已经就绪。
            if (value && _startMinimized && !_startedHidden && _render == null)
            {
                _startedHidden = true;
                if (!IsHandleCreated)
                {
                    CreateHandle();
                }

                // 读一次 Handle 就会建好浏览器控件的窗口，WebView2 才能在隐藏状态下初始化。
                IntPtr browser = _shell.Control.Handle;
                BeginInvoke(new Action(StartHidden));
                value = false;
            }

            if (!value && _shell != null)
            {
                _shell.SetVisible(false);
            }

            base.SetVisibleCore(value);
            if (value && _shell != null)
            {
                _shell.SetVisible(WindowState != FormWindowState.Minimized);
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            WindowChrome.Apply(Handle);
            FitToDpi(true);
            if (_exitRequested)
            {
                BeginInvoke(new Action(ExitIfRequested));
            }

            if (_render != null || !NativeRecovery.Available)
            {
                return;
            }

            _recoveryHotkeyRegistered = RegisterHotKey(Handle, RecoveryHotkeyId, ModControl | ModAlt | ModNoRepeat, (uint)Keys.F9);
            _restartHotkeyRegistered = RegisterHotKey(Handle, RestartHotkeyId, ModControl | ModAlt | ModNoRepeat, (uint)Keys.F10);
            NativeRecovery.HotkeyName = _recoveryHotkeyRegistered ? RecoveryHotkeyName : null;
            NativeRecovery.RestartHotkeyName = _restartHotkeyRegistered ? RestartHotkeyName : null;
        }

        /// <summary>窗口尺寸按当前屏幕 DPI 换算；首次创建时居中，且不超出工作区。</summary>
        private void FitToDpi(bool center)
        {
            float scale = DeviceDpi / 96f;
            Size frame = Size - ClientSize;
            Rectangle area = Screen.FromHandle(Handle).WorkingArea;
            int width = _render != null && _render.Width > 0 ? _render.Width : LogicalWidth;
            int height = _render != null && _render.Height > 0 ? _render.Height : LogicalHeight;
            var client = new Size(
                Math.Min((int)(width * scale), area.Width - frame.Width),
                Math.Min((int)(height * scale), area.Height - frame.Height));
            // 不用 MinimumSize：它的 setter 调 SetWindowPos 时不带 SWP_NOACTIVATE，窗口还没显示就被激活，
            // 最小化启动时会把键盘焦点从玩家正在用的程序里抢走。最小尺寸改在 WM_GETMINMAXINFO 里给。
            _minimumTrackSize = new Size(
                Math.Min((int)(LogicalMinWidth * scale), client.Width) + frame.Width,
                Math.Min((int)(LogicalMinHeight * scale), client.Height) + frame.Height);
            Size = client + frame;
            if (center && _render == null)
            {
                Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
            }
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            if (_recoveryHotkeyRegistered)
            {
                UnregisterHotKey(Handle, RecoveryHotkeyId);
                _recoveryHotkeyRegistered = false;
            }

            if (_restartHotkeyRegistered)
            {
                UnregisterHotKey(Handle, RestartHotkeyId);
                _restartHotkeyRegistered = false;
            }

            base.OnHandleDestroyed(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WindowChrome.WmGetMinMaxInfo && !_minimumTrackSize.IsEmpty)
            {
                base.WndProc(ref m);
                WindowChrome.SetMinimumTrackSize(m.LParam, _minimumTrackSize);
                return;
            }

            if (m.Msg == WmHotkey && (m.WParam.ToInt32() == RecoveryHotkeyId || m.WParam.ToInt32() == RestartHotkeyId))
            {
                _native.Hotkey(m.WParam.ToInt32() == RestartHotkeyId);
                return;
            }

            // 藏在托盘里的窗口不会被玩家点关闭；这时收到的关闭（taskkill、安装程序）是外部请求，干净退出。
            // 窗口可见时由 FormClosing 按关闭原因区分。
            if (m.Msg == WmClose && !Visible && !_allowExit && _render == null)
            {
                ExitApplication();
                return;
            }

            if (m.Msg == WindowChrome.WmNcCalcSize && m.WParam != IntPtr.Zero)
            {
                int top = WindowChrome.CaptionTop(m.LParam);
                base.WndProc(ref m);
                WindowChrome.MergeCaption(Handle, m.LParam, top, DeviceDpi);
                m.Result = IntPtr.Zero;
                return;
            }

            base.WndProc(ref m);
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr window, int id);

        private async void HandleShown(object sender, EventArgs e)
        {
            await StartAsync();
        }

        private async void StartHidden()
        {
            await StartAsync();
            // 和藏到托盘一样，过一会儿让页面休眠。
            _suspendTimer.Start();
        }

        private async Task StartAsync()
        {
            if (_started)
            {
                return;
            }

            _started = true;
            await _shell.InitializeAsync();
            if (_render != null)
            {
                return;
            }

            _files.OfferInterrupted(_pageReady, _warmRestore.HasActiveOperation);
            PollNow();
            _pollTimer.Start();
        }

        private void HandleShellFailed(string reason)
        {
            if (_render != null)
            {
                Console.Error.WriteLine("WebView2 failed: " + reason);
                Environment.ExitCode = 2;
                ExitApplication();
                return;
            }

            DiagnosticLog.Write("webview2", reason);
            MessageBox.Show(
                "界面组件 WebView2 无法启动，可能需要安装或修复 Microsoft Edge WebView2 运行时。\n\n守护器会继续在托盘运行，游戏内快捷键仍可使用。",
                "赴死之旅守护器",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            Hide();
        }

        // ---------- 状态发布与提示（IGuardianHost） ----------

        GuardianSettings IGuardianHost.Settings
        {
            get { return _settings; }
        }

        SnapshotStore IGuardianHost.Store
        {
            get { return _snapshotStore; }
        }

        GuardianMonitor IGuardianHost.Monitor
        {
            get { return _monitor; }
        }

        public bool RestoreUnfinished
        {
            get { return _snapshotStore.HasPendingRestore() || (_warmRestore != null && _warmRestore.HasActiveOperation); }
        }

        void IGuardianHost.SaveSettings()
        {
            if (_render == null)
            {
                _settingsStore.Save(_settings);
            }
        }

        void IGuardianHost.ReloadSnapshots(string focusId)
        {
            LoadSnapshots(focusId);
        }

        void IGuardianHost.RefreshUndoPoint()
        {
            _files.RefreshUndoPoint();
        }

        void IGuardianHost.RunOnUiThread(Action action)
        {
            if (IsHandleCreated && !IsDisposed)
            {
                BeginInvoke(action);
            }
        }

        public void PublishState()
        {
            if (!_pageReady)
            {
                return;
            }

            string json = _shell.Serialize(GuardianView.State(new GuardianView.StateInput
            {
                Monitor = _lastMonitorStatus,
                Records = _records,
                Settings = _settings,
                PendingRestore = _snapshotStore.HasPendingRestore(),
                WarmActive = _warmRestore != null && _warmRestore.HasActiveOperation,
                Cleaning = _cleanup.Busy,
                NativeAvailable = NativeRecovery.Available,
                NativeRunning = NativeRecovery.Running,
                AutoProtect = _recorder.AutoProtect,
                StartupEnabled = StartupEntry.Exists(),
                UndoSnapshotId = _files.UndoSnapshotId,
                FocusId = _focusId,
                FocusNonce = _focusNonce,
                RecoverHotkey = _recoveryHotkeyRegistered ? RecoveryHotkeyName : null,
                RestartHotkey = _restartHotkeyRegistered ? RestartHotkeyName : null,
                HasDeparture = id => NativeRecovery.HasDeparture(_settings.StoragePath, id)
            }));
            if (json == _lastStateJson)
            {
                return;
            }

            _lastStateJson = json;
            _shell.PostJson("{\"type\":\"state\",\"state\":" + json + "}");
        }

        public void Toast(string text, string tone)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            if (_pageReady)
            {
                _shell.Post(GuardianView.Map("type", "toast", "text", text, "tone", tone));
            }
            else if (_tray != null)
            {
                // 界面还没就绪（或 WebView2 无法启动）时，结果改由托盘提示，不让操作悄无声息。
                _tray.Balloon(4000, null, text, tone == "danger" ? ToolTipIcon.Warning : ToolTipIcon.Info);
            }
        }

        public void ShowResult(string code, string detail, bool success)
        {
            if (!success)
            {
                DiagnosticLog.Write("result", code + ": " + detail);
            }

            Toast(GuardianView.FriendlyMessage(code, detail), success ? "signal" : "danger");
        }

        public void Notify(string title, string text, ToolTipIcon icon)
        {
            if (_tray != null)
            {
                _tray.Balloon(5000, title, text, icon);
            }
        }

        public void ShowSheet(Dictionary<string, object> sheet)
        {
            if (_pageReady)
            {
                _shell.Post(GuardianView.Map("type", "sheet", "sheet", sheet));
            }
        }

        public void PostRecovery(string phase, string text)
        {
            if (_pageReady)
            {
                _shell.Post(GuardianView.Map("type", "recovery", "phase", phase, "text", text));
            }
        }

        private void FocusSnapshot(string id)
        {
            _focusId = id;
            _focusNonce++;
        }

        // ---------- 页面命令 ----------

        private void HandleCommand(Dictionary<string, object> message)
        {
            object value;
            string name = message.TryGetValue("name", out value) ? value as string : null;
            string id = message.TryGetValue("id", out value) ? value as string : null;
            string action = message.TryGetValue("action", out value) ? value as string : null;
            if (_render != null && name != "ready")
            {
                // 截图模式绝不执行命令：只记下页面发了什么，供检查界面接线。
                Console.Error.WriteLine("command " + _shell.Serialize(message));
                return;
            }

            switch (name)
            {
                case "ready":
                    _pageReady = true;
                    _lastStateJson = null;
                    if (_render != null)
                    {
                        // 开发模式下页面可能报两次就绪；截图只做一次。
                        if (!_renderStarted)
                        {
                            _renderStarted = true;
                            BeginRenderCapture();
                        }

                        return;
                    }

                    PublishState();
                    _files.OfferInterrupted(_pageReady, _warmRestore.HasActiveOperation);
                    break;
                case "protect":
                    HandleProtectNow();
                    break;
                case "restore":
                    if (_warmRestore.HasActiveOperation)
                    {
                        HandleWarmRestore();
                    }
                    else
                    {
                        _files.Restore(FindRecord(id));
                    }

                    break;
                case "native-open":
                    _native.Open(id, action);
                    break;
                case "native-start":
                    _native.Start(id, action);
                    break;
                case "native-close":
                    _native.Close();
                    break;
                case "undo":
                    _files.Undo();
                    break;
                case "remove":
                    HandleDeleteSnapshot(id);
                    break;
                case "launch":
                    HandleLaunchGame();
                    break;
                case "auto-cleanup":
                    _cleanup.Toggle(message.TryGetValue("value", out value) && Equals(value, true));
                    break;
                case "show-undo":
                    _settings.ShowUndoPoints = message.TryGetValue("value", out value) && Equals(value, true);
                    _settingsStore.Save(_settings);
                    PublishState();
                    break;
                case "panel-transparency":
                    if (message.TryGetValue("value", out value) && (value is int || value is decimal || value is double))
                    {
                        _settings.PanelTransparency = Math.Max(0, Math.Min(100, Convert.ToInt32(value)));
                        _settingsStore.Save(_settings);
                        PublishState();
                    }

                    break;
                case "cleanup-confirm":
                    _cleanup.Confirm(message.TryGetValue("token", out value) ? value as string : null);
                    break;
                case "interrupted-confirm":
                    _files.ConfirmInterrupted();
                    break;
                case "warm":
                    HandleWarmRestore();
                    break;
                case "choose-profile":
                    HandleChooseProfile();
                    break;
                case "open-saves":
                    OpenFolder(_settings.ProfilePath);
                    break;
                case "open-backups":
                    OpenFolder(_settings.StoragePath);
                    break;
                case "auto-protect":
                    HandleAutoProtectChanged(message.TryGetValue("value", out value) && Equals(value, true));
                    break;
                case "startup":
                    HandleStartupChanged(message.TryGetValue("value", out value) && Equals(value, true));
                    break;
                case "window":
                    HandleWindowCommand(action);
                    break;
            }
        }

        private void HandleWindowCommand(string action)
        {
            switch (action)
            {
                case "minimize":
                    WindowState = FormWindowState.Minimized;
                    break;
                case "maximize":
                    WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
                    break;
                case "close":
                    Close();
                    break;
            }
        }

        private async void BeginRenderCapture()
        {
            try
            {
                PollNow();
                _lastStateJson = null;
                PublishState();
                await Task.Delay(_render.WaitMs > 0 ? _render.WaitMs : 2600);
                if (!string.IsNullOrWhiteSpace(_render.Script))
                {
                    await _shell.ExecuteScriptAsync(_render.Script);
                    await Task.Delay(1400);
                }

                string fullPath = Path.GetFullPath(_render.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
                await _shell.CaptureAsync(fullPath);
                Console.Error.WriteLine("window " + Width + "x" + Height + " client " + ClientSize.Width + "x" + ClientSize.Height
                    + " dpi " + DeviceDpi + " min " + _minimumTrackSize.Width + "x" + _minimumTrackSize.Height);
                if (_render.HideSeconds > 0)
                {
                    // 检查藏到托盘后的休眠：隐藏窗口、按正常流程休眠页面，留出时间给外部测量。
                    Hide();
                    await Task.Delay(4500);
                    _shell.Suspend();
                    Console.Error.WriteLine("hidden");
                    await Task.Delay(_render.HideSeconds * 1000);
                }
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("Render failed: " + error.Message);
                Environment.ExitCode = 2;
            }

            ExitApplication();
        }

        // ---------- 托盘与外部请求 ----------

        private void ProtectFromTray()
        {
            if (!_pageReady)
            {
                // 界面没能启动时退回系统确认框；“身处兵营”的确认仍然必须由玩家给出。
                if (MessageBox.Show("请确认你正在兵营里。", "保护当前战备", MessageBoxButtons.OKCancel,
                    MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.OK)
                {
                    HandleProtectNow();
                }

                return;
            }

            ShowFromTray();
            ShowSheet(GuardianView.Map("kind", "protect"));
        }

        /// <summary>玩家再次打开守护器时（快捷方式、开始菜单），新进程发来信号，这里把窗口拿出来。</summary>
        private void ListenForShowRequests()
        {
            _showSignal = new System.Threading.EventWaitHandle(
                false, System.Threading.EventResetMode.AutoReset, Program.ShowEventName);
            _showWait = System.Threading.ThreadPool.RegisterWaitForSingleObject(_showSignal, (state, timedOut) =>
            {
                if (IsHandleCreated && !IsDisposed)
                {
                    BeginInvoke(new Action(ShowFromTray));
                }
            }, null, -1, false);

            // 安装程序升级或卸载前用 --exit 请守护器退出；原生恢复进行中时不退，安装程序会请玩家稍后再试。
            _exitSignal = new System.Threading.EventWaitHandle(
                false, System.Threading.EventResetMode.AutoReset, Program.ExitEventName);
            _exitWait = System.Threading.ThreadPool.RegisterWaitForSingleObject(_exitSignal, (state, timedOut) =>
            {
                // 刚启动、窗口还没建好时先记下，建好后再处理（见 OnHandleCreated），请求不会丢。
                _exitRequested = true;
                if (IsHandleCreated && !IsDisposed)
                {
                    BeginInvoke(new Action(ExitIfRequested));
                }
            }, null, -1, false);
        }

        private void ExitIfRequested()
        {
            if (!_exitRequested)
            {
                return;
            }

            _exitRequested = false;
            if (!NativeRecovery.Running)
            {
                ExitApplication();
            }
        }

        private void WireServiceEvents()
        {
            _monitor.SnapshotCreated += (sender, args) =>
            {
                LoadSnapshots(args.Snapshot == null || args.Snapshot.Manifest == null ? null : args.Snapshot.Manifest.Id);
            };
            _monitor.SuspectedEndDetected += (sender, args) =>
            {
                if (_tray != null)
                {
                    _tray.Balloon(6000, "征途疑似已结束", _recoveryHotkeyRegistered
                        ? "若是战斗死亡，可在结算页按 " + RecoveryHotkeyName + " 回兵营，按 " + RestartHotkeyName + " 重开这场战斗。"
                        : "若是战斗死亡，可以在守护器里选一份战备恢复。", ToolTipIcon.Warning);
                }
            };
        }

        // ---------- 轮询 ----------

        private void PollNow()
        {
            if (_polling)
            {
                return;
            }

            _polling = true;
            try
            {
                bool wasRunning = _lastMonitorStatus != null && _lastMonitorStatus.GameRunning;
                _lastMonitorStatus = _monitor.Poll();
                if (_render == null)
                {
                    _stateTranscript.Record(_lastMonitorStatus);
                }

                if (_tray != null)
                {
                    _tray.Update(_lastMonitorStatus, FindRecord(_lastMonitorStatus.LastProtectedSnapshotId));
                }

                _recorder.EnsureWatcher(_lastMonitorStatus.GameRunning, _native.PanelOpen);

                if (_render == null
                    && !string.IsNullOrWhiteSpace(_lastMonitorStatus.GameExecutablePath)
                    && !string.Equals(_settings.GameExecutablePath, _lastMonitorStatus.GameExecutablePath, StringComparison.OrdinalIgnoreCase))
                {
                    _settings.GameExecutablePath = _lastMonitorStatus.GameExecutablePath;
                    _settingsStore.Save(_settings);
                }

                if (_render == null && wasRunning && !_lastMonitorStatus.GameRunning && _lastMonitorStatus.SuspectedRunEnded)
                {
                    ShowFromTray();
                    SnapshotRecord preferred = FindRecord(_lastMonitorStatus.PreferredRestoreSnapshotId);
                    if (preferred != null)
                    {
                        FocusSnapshot(preferred.Manifest.Id);
                    }
                }

                PublishState();
                _cleanup.RunIfDue(NativeRecovery.Running || _native.PanelOpen);
            }
            finally
            {
                _polling = false;
            }
        }

        private void LoadSnapshots(string preferredId)
        {
            _records = _snapshotStore.ListSnapshots()
                .Where(record => record != null
                    && record.Manifest != null
                    && string.Equals(record.Manifest.SourceProfilePath, _settings.ProfilePath, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (!string.IsNullOrEmpty(preferredId))
            {
                FocusSnapshot(preferredId);
            }
            else if (_focusId == null)
            {
                string preferred = _monitor == null ? null : _monitor.ProtectedBeforeSuspectedEndId ?? _monitor.LastProtectedSnapshotId;
                SnapshotRecord candidate = _records.FirstOrDefault(record => record.Manifest.Id == preferred)
                    ?? _records.FirstOrDefault(SnapshotPolicy.IsConfirmedPreparation);
                _focusId = candidate == null ? null : candidate.Manifest.Id;
            }

            _cleanup.MarkDue();
            PublishState();
        }

        private SnapshotRecord FindRecord(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            SnapshotRecord cached = _records.FirstOrDefault(record => record.Manifest.Id == id);
            if (cached != null)
            {
                return cached;
            }

            Result<SnapshotRecord> result = _snapshotStore.GetSnapshot(id);
            return result.Success ? result.Value : null;
        }

        // ---------- 保护 ----------

        private void HandleProtectNow()
        {
            if (_snapshotStore.HasPendingRestore() || string.IsNullOrWhiteSpace(_settings.ProfilePath))
            {
                Toast("现在不能保护：上次恢复还没完成，或没有找到存档", "danger");
                return;
            }

            // 页面上的确认面板就是玩家对“身处兵营”的明确确认。
            Result<SnapshotRecord> result = _monitor.ProtectNow("手动战备", true);
            ShowResult(result.Code, result.Message, result.Success);
            if (result.Success)
            {
                LoadSnapshots(result.Value.Manifest.Id);
                _recorder.Arm(result.Value.Manifest.Id);
                if (!Visible && _tray != null)
                {
                    _tray.Balloon(3000, "战备已保护", "之后死亡可以回到这份战备。", ToolTipIcon.Info);
                }
            }
        }

        // ---------- 普通恢复（游戏已退出，见 FileRestore）与实验性暖恢复 ----------

        private void HandleWarmRestore()
        {
            if (!WarmRestoreProductEntryEnabled && !_warmRestore.HasActiveOperation)
            {
                Toast("游戏内暖恢复还没有开放", "neutral");
                return;
            }

            SnapshotRecord selected = FindRecord(_focusId);
            string snapshotId = selected == null || selected.Manifest == null ? null : selected.Manifest.Id;
            using (WarmRestoreDialog dialog = new WarmRestoreDialog(
                _warmRestore,
                new WarmRestoreAutomationController(_warmRestore, new WarmRestoreNavigator()),
                _settings.ProfilePath,
                snapshotId))
            {
                dialog.ShowDialog(this);
                if (dialog.OperationChanged)
                {
                    _files.RefreshUndoPoint();
                    LoadSnapshots(snapshotId);
                    PollNow();
                }

                if (!string.IsNullOrWhiteSpace(dialog.LastStatus))
                {
                    Toast(dialog.LastStatus, dialog.LastSucceeded ? "signal" : "danger");
                }
            }
        }

        // ---------- 快照库 ----------

        private void HandleDeleteSnapshot(string id)
        {
            SnapshotRecord selected = FindRecord(id);
            if (selected == null || selected.Manifest == null)
            {
                return;
            }

            if (_snapshotStore.HasPendingRestore())
            {
                Toast("上次恢复还没完成，暂时不能删除", "danger");
                return;
            }

            Result<bool> result = _snapshotStore.DeleteSnapshot(selected.Manifest.Id);
            ShowResult(result.Code, result.Message, result.Success);
            if (result.Success)
            {
                _monitor.RefreshProtectedSnapshot();
                _files.RefreshUndoPoint();
                _focusId = null;
                LoadSnapshots(null);
            }
        }

        /// <summary>
        /// 正在用的快照无论多旧都不清理：当前保护的、疑似结束前保护的、最近一次恢复的、
        /// 出发记录器正替它记录的，以及“撤销最近恢复”要用的那份。
        /// </summary>
        private IEnumerable<string> PinnedSnapshots()
        {
            return new[]
            {
                _monitor.LastProtectedSnapshotId,
                _monitor.ProtectedBeforeSuspectedEndId,
                _settings.LastRestoredSnapshotId,
                _recorder.Owner,
                _files.UndoSnapshotId
            };
        }

        public void RememberRestored(string snapshotId)
        {
            if (_render != null || string.Equals(_settings.LastRestoredSnapshotId, snapshotId, StringComparison.Ordinal))
            {
                return;
            }

            _settings.LastRestoredSnapshotId = snapshotId;
            _settingsStore.Save(_settings);
        }

        // ---------- 启动游戏与设置 ----------

        private void HandleLaunchGame()
        {
            if (_snapshotStore.HasPendingRestore())
            {
                Toast("上次恢复还没完成，先不要启动游戏", "danger");
                return;
            }

            if (_gameProbe.IsGameRunning())
            {
                Toast("游戏已经在运行", "neutral");
                return;
            }

            string executable = _settings.GameExecutablePath;
            if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
            {
                // 还没见过游戏运行时，先在 Steam 库、Epic 清单和系统的已安装程序里找；都没有才请玩家选。
                executable = GameLocator.FindExecutable();
                if (executable != null)
                {
                    _settings.GameExecutablePath = executable;
                    _settingsStore.Save(_settings);
                }
            }

            if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
            {
                using (OpenFileDialog dialog = new OpenFileDialog
                {
                    Title = "选择 The Last of Us Part II 可执行文件",
                    Filter = "游戏程序 (tlou-ii*.exe)|tlou-ii*.exe|可执行文件 (*.exe)|*.exe",
                    CheckFileExists = true
                })
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK)
                    {
                        return;
                    }

                    executable = dialog.FileName;
                    _settings.GameExecutablePath = executable;
                    _settingsStore.Save(_settings);
                }
            }

            try
            {
                Process.Start(new ProcessStartInfo(executable)
                {
                    WorkingDirectory = Path.GetDirectoryName(executable),
                    UseShellExecute = true
                });
                Toast("正在启动游戏", "neutral");
            }
            catch (Exception exception)
            {
                DiagnosticLog.Write("launch-game", exception);
                Toast("游戏没有启动，请从 Steam 或 Epic 启动", "danger");
            }
        }

        private void HandleChooseProfile()
        {
            if (_warmRestore != null && _warmRestore.HasActiveOperation)
            {
                Toast("上次恢复还没完成，暂时不能更换存档", "danger");
                return;
            }

            string picked = FolderPicker.Pick(
                this,
                "选择存档目录（数字账号文件夹，或 The Last of Us Part II 存档根目录）",
                Directory.Exists(_settings.ProfilePath) ? _settings.ProfilePath : _saveLocator.GameDocumentsPath);
            if (picked == null)
            {
                return;
            }

            string selected = SaveLocator.ResolvePickedProfile(picked);
            if (selected == null)
            {
                ShowSheet(GuardianView.Map("kind", "failed", "title", "不是游戏存档目录", "text", "所选目录里没有找到征途存档。"));
                return;
            }

            _recorder.Stop();
            _recorder.ResetBackoff();
            _settings.ProfilePath = selected;
            _settingsStore.Save(_settings);
            _monitor.SetProfile(selected);
            _focusId = null;
            Toast("已更换存档账号", "neutral");
            LoadSnapshots(null);
            PollNow();
        }

        private void HandleAutoProtectChanged(bool enabled)
        {
            if (!NativeRecovery.Available)
            {
                PublishState();
                return;
            }

            _settings.AutoProtectOnDeparture = enabled;
            _settingsStore.Save(_settings);
            _recorder.Restart(_lastMonitorStatus != null && _lastMonitorStatus.GameRunning, _native.PanelOpen);
            PublishState();
        }

        private void HandleStartupChanged(bool enabled)
        {
            try
            {
                StartupEntry.Set(enabled);
                _settings.StartWithWindows = enabled;
                _settingsStore.Save(_settings);
            }
            catch (Exception exception)
            {
                DiagnosticLog.Write("startup-entry", exception);
                Toast("开机启动没有设置成功", "danger");
            }

            PublishState();
        }

        private void OpenFolder(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    Toast("还没有这个目录", "neutral");
                    return;
                }

                Directory.CreateDirectory(path);
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true });
            }
            catch (Exception exception)
            {
                DiagnosticLog.Write("open-folder", exception);
                Toast("目录没有打开", "danger");
            }
        }

        // ---------- 显示与退出 ----------

        private void ShowFromTray()
        {
            if (_render != null)
            {
                return;
            }

            Show();
            if (WindowState == FormWindowState.Minimized)
            {
                WindowState = FormWindowState.Normal;
            }

            BringToFront();
            Activate();
        }

        private void HandleFormClosing(object sender, FormClosingEventArgs e)
        {
            // 关窗只缩到托盘；系统关机或任务管理器等外部关闭请求才真正退出。
            if (_allowExit || e.CloseReason == CloseReason.WindowsShutDown || e.CloseReason == CloseReason.TaskManagerClosing)
            {
                if (_tray != null)
                {
                    _tray.Hide();
                }

                return;
            }

            e.Cancel = true;
            Hide();
            if (!_trayHintShown && _tray != null)
            {
                _trayHintShown = true;
                _tray.Balloon(3500, "守护器仍在运行", "双击托盘图标重新打开。", ToolTipIcon.Info);
            }
        }

        private void ExitApplication()
        {
            _allowExit = true;
            _pollTimer.Stop();
            if (_tray != null)
            {
                _tray.Hide();
            }

            Close();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_showWait != null)
                {
                    _showWait.Unregister(null);
                }

                if (_showSignal != null)
                {
                    _showSignal.Dispose();
                }

                if (_exitWait != null)
                {
                    _exitWait.Unregister(null);
                }

                if (_exitSignal != null)
                {
                    _exitSignal.Dispose();
                }

                _recorder.Stop();
                _pollTimer.Dispose();
                _suspendTimer.Dispose();
                _shell.Dispose();
                if (_tray != null)
                {
                    _tray.Dispose();
                }
            }

            base.Dispose(disposing);
        }
    }
}
