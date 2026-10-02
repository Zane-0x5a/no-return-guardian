using Microsoft.Win32;
using NoReturnGuardian.Core;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NoReturnGuardian
{
    internal enum UpdatePhase
    {
        Idle,
        Downloading,
        Installing,
        Failed
    }

    /// <summary>
    /// 更新：游戏没运行时，每天最多问一次 GitHub 最新的正式版；有新版本时窗口里显示，托盘气泡每个版本只说一次。
    /// 玩家点“更新”后，下载这一版的安装程序，核对发布里的校验和与版本号，在游戏没运行时运行它：安装程序像平常升级一样
    /// 请守护器退出，装好后再打开它。便携版和开发构建不在安装程序记下的位置，点“更新”打开下载页。
    /// </summary>
    internal sealed class Updater
    {
        private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan Interval = TimeSpan.FromHours(24);
        private static readonly TimeSpan Retry = TimeSpan.FromHours(1);

        // 与 installer\NoReturnGuardian.iss 的 AppId 相同。
        private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{295032DA-7333-4CA6-B405-D53B639C79A9}_is1";
        private static readonly string Downloads = Path.Combine(Path.GetTempPath(), "NoReturnGuardian-update");

        internal static readonly Version Current = Assembly.GetExecutingAssembly().GetName().Version;

        private readonly IGuardianHost _host;
        private readonly IGameProcessProbe _game;
        private readonly Action _showWindow;
        private readonly bool _live;
        private readonly DateTime _started = DateTime.UtcNow;
        private DateTime _lastAttempt = DateTime.MinValue;
        private bool _checking;
        private bool _swept;
        // 下载并核对过的安装程序；文件名带版本号，和要装的版本对得上才直接用。
        private string _verified;

        /// <param name="showWindow">从托盘气泡开始更新时，把窗口拿出来看进度。</param>
        public Updater(IGuardianHost host, IGameProcessProbe game, Action showWindow, bool live)
        {
            _host = host;
            _game = game;
            _showWindow = showWindow;
            _live = live;
            Installable = live && InstalledHere();
        }

        /// <summary>界面上显示的新版本；没有新版本、或玩家关了检查更新时为空。</summary>
        public Version Available
        {
            get
            {
                Version latest = ReleaseFeed.ParseStored(_host.Settings.LatestRelease);
                return _host.Settings.CheckForUpdates && ReleaseFeed.IsNewer(latest, Current) ? latest : null;
            }
        }

        /// <summary>这份守护器装在安装程序记下的位置，能直接更新；否则只能打开下载页。</summary>
        public bool Installable { get; private set; }

        public UpdatePhase Phase { get; private set; }

        /// <summary>下载进度，0 到 100。</summary>
        public int Progress { get; private set; }

        /// <summary>随状态轮询调用：到时间就在后台检查；有还没说过的新版本，就用气泡说一次。</summary>
        public void Poll(bool gameRunning)
        {
            DateTime now = DateTime.UtcNow;
            if (!_live || now - _started < FirstDelay)
            {
                return;
            }

            if (!_swept && _verified == null && Phase == UpdatePhase.Idle)
            {
                // 上次更新留下的安装程序：那时它还在运行，删不掉。
                _swept = true;
                Sweep();
            }

            if (gameRunning || !_host.Settings.CheckForUpdates)
            {
                return;
            }

            Announce();
            if (_checking || now - _lastAttempt < Retry || !Due(now))
            {
                return;
            }

            _checking = true;
            _lastAttempt = now;
            Task.Run(() => ReleaseFeed.FetchLatest(Current)).ContinueWith(task => _host.RunOnUiThread(() => Checked(task)));
        }

        /// <summary>玩家点了“更新”：下载、核对这一版的安装程序，然后运行它。不能直接更新时打开下载页。</summary>
        public void Install()
        {
            Version target = Available;
            if (target == null || Phase == UpdatePhase.Downloading || Phase == UpdatePhase.Installing)
            {
                return;
            }

            if (!Installable)
            {
                OpenPage();
                return;
            }

            if (_game.IsGameRunning())
            {
                _host.Toast("退出游戏后再更新", "neutral");
                return;
            }

            if (_verified == Path.Combine(Downloads, ReleaseFeed.SetupName(target)) && File.Exists(_verified))
            {
                Launch(target);
                return;
            }

            Phase = UpdatePhase.Downloading;
            Progress = 0;
            _host.PublishState();
            Task.Run(() =>
            {
                Sweep();
                return ReleaseFeed.DownloadSetup(ReleaseFeed.AssetsFor(target), target, Downloads, Current,
                    percent => _host.RunOnUiThread(() => ShowProgress(percent)));
            }).ContinueWith(task => _host.RunOnUiThread(() => Downloaded(target, task)));
        }

        public void OpenPage()
        {
            try
            {
                Process.Start(ReleaseFeed.PageFor(Available));
            }
            catch (Exception error) when (error is Win32Exception || error is InvalidOperationException)
            {
                DiagnosticLog.Write("open-release", error);
            }
        }

        /// <summary>
        /// 界面就绪时：上次运行的安装程序装好了没有，告诉玩家一次。安装程序在托盘里重新打开守护器，页面就绪后才拿出窗口，
        /// 界面起不来时就不弹窗。
        /// </summary>
        public void PageReady()
        {
            string target = _host.Settings.UpdatingTo;
            if (string.IsNullOrEmpty(target))
            {
                return;
            }

            _host.Settings.UpdatingTo = null;
            _host.SaveSettings();
            if (target == ReleaseFeed.Display(Current))
            {
                _showWindow();
                _host.Toast("已更新到 " + target, "signal");
            }
            else
            {
                DiagnosticLog.Write("update-install", "The " + target + " installer ran, but this is still " + ReleaseFeed.Display(Current) + ".");
                _host.Toast("没有更新到 " + target + "，可以再点一次更新", "danger");
            }
        }

        private bool Due(DateTime now)
        {
            DateTime last;
            if (!DateTime.TryParse(_host.Settings.LastUpdateCheckUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out last))
            {
                return true;
            }

            last = last.ToUniversalTime();
            // 系统时间往回调过时，记下的时间会在未来；照样当作该查了。
            return last > now || now - last >= Interval;
        }

        private void Announce()
        {
            Version available = Available;
            if (available == null || ReleaseFeed.Display(available) == _host.Settings.AnnouncedRelease)
            {
                return;
            }

            _host.Settings.AnnouncedRelease = ReleaseFeed.Display(available);
            _host.SaveSettings();
            // 最不急的一种提示：不加系统的大图标，标题栏里已经有守护器自己的图标。
            if (Installable)
            {
                _host.Notify("有新版本 " + ReleaseFeed.Display(available), "点这里更新。", ToolTipIcon.None, () =>
                {
                    _showWindow();
                    Install();
                });
            }
            else
            {
                _host.Notify("有新版本 " + ReleaseFeed.Display(available), "点这里打开下载页。", ToolTipIcon.None, OpenPage);
            }
        }

        private void Checked(Task<Version> task)
        {
            _checking = false;
            if (task.IsFaulted)
            {
                DiagnosticLog.Write("update-check", task.Exception.GetBaseException());
                return;
            }

            _host.Settings.LatestRelease = task.Result == null ? null : ReleaseFeed.Display(task.Result);
            _host.Settings.LastUpdateCheckUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            _host.SaveSettings();
            _host.PublishState();
        }

        private void ShowProgress(int percent)
        {
            if (Phase == UpdatePhase.Downloading)
            {
                Progress = percent;
                _host.PublishState();
            }
        }

        private void Downloaded(Version target, Task<string> task)
        {
            if (task.IsFaulted)
            {
                DiagnosticLog.Write("update-download", task.Exception.GetBaseException());
                Phase = UpdatePhase.Failed;
                _host.PublishState();
                return;
            }

            _verified = task.Result;
            if (_game.IsGameRunning())
            {
                // 下载时游戏开了：留着这份安装程序，玩家退出游戏后再点更新就直接装。
                Phase = UpdatePhase.Idle;
                _host.PublishState();
                return;
            }

            Launch(target);
        }

        private void Launch(Version target)
        {
            Phase = UpdatePhase.Installing;
            _host.Settings.UpdatingTo = ReleaseFeed.Display(target);
            _host.SaveSettings();
            _host.PublishState();
            try
            {
                // /RELAUNCH：装好后安装程序重新打开守护器（见 installer\NoReturnGuardian.iss）。
                Process setup = Process.Start(new ProcessStartInfo(_verified, "/SILENT /NORESTART /RELAUNCH") { UseShellExecute = false });
                setup.EnableRaisingEvents = true;
                setup.Exited += (sender, args) => _host.RunOnUiThread(() => SetupEnded(setup));
            }
            catch (Win32Exception error)
            {
                DiagnosticLog.Write("update-install", error);
                Abandon();
            }
        }

        // 正常时安装程序先请守护器退出，守护器看不到它结束；看到了，就是没装（比如没给管理员授权）。
        private void SetupEnded(Process setup)
        {
            DiagnosticLog.Write("update-install", "The installer ended with exit code " + setup.ExitCode + " while Guardian was still running.");
            setup.Dispose();
            Abandon();
        }

        private void Abandon()
        {
            _host.Settings.UpdatingTo = null;
            _host.SaveSettings();
            Phase = UpdatePhase.Failed;
            _host.PublishState();
        }

        private static void Sweep()
        {
            try
            {
                if (Directory.Exists(Downloads))
                {
                    Directory.Delete(Downloads, true);
                }
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                DiagnosticLog.Write("update-sweep", error);
            }
        }

        // 安装程序在卸载登记里记下的位置就是这份 exe 所在的目录：为当前用户安装记在 HKCU，为所有用户安装记在 HKLM。
        private static bool InstalledHere()
        {
            string here = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd('\\');
            foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            {
                using (RegistryKey root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64))
                using (RegistryKey key = root.OpenSubKey(UninstallKey))
                {
                    string location = key == null ? null : key.GetValue("InstallLocation") as string;
                    if (!string.IsNullOrWhiteSpace(location)
                        && string.Equals(Path.GetFullPath(location).TrimEnd('\\'), here, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
