using NoReturnGuardian.Core;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NoReturnGuardian
{
    /// <summary>
    /// 检查更新：游戏没运行时，每天最多问一次 GitHub 最新的正式版。有新版本时窗口里显示，托盘气泡每个版本只说一次，
    /// 点开的都是发布页；守护器从不下载或运行任何东西。失败只记进日志，一小时后再试。
    /// </summary>
    internal sealed class UpdateCheck
    {
        private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan Interval = TimeSpan.FromHours(24);
        private static readonly TimeSpan Retry = TimeSpan.FromHours(1);

        internal static readonly Version Current = Assembly.GetExecutingAssembly().GetName().Version;

        private readonly IGuardianHost _host;
        private readonly bool _live;
        private readonly DateTime _started = DateTime.UtcNow;
        private DateTime _lastAttempt = DateTime.MinValue;
        private bool _checking;

        public UpdateCheck(IGuardianHost host, bool live)
        {
            _host = host;
            _live = live;
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

        /// <summary>随状态轮询调用：到时间就在后台检查；有还没说过的新版本，就用气泡说一次。</summary>
        public void Poll(bool gameRunning)
        {
            DateTime now = DateTime.UtcNow;
            if (!_live || gameRunning || !_host.Settings.CheckForUpdates || now - _started < FirstDelay)
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
            Task.Run(() => Fetch()).ContinueWith(task => _host.RunOnUiThread(() => Finish(task)));
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
            _host.Notify(
                "有新版本 " + ReleaseFeed.Display(available),
                "当前是 " + ReleaseFeed.Display(Current) + "。点这里打开下载页；升级不会动你的快照和设置。",
                ToolTipIcon.None,
                OpenPage);
        }

        private void Finish(Task<Version> task)
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

        private static Version Fetch()
        {
            // 只问最新版页面跳到哪里：不跟随跳转，也不读页面内容。
            var request = (HttpWebRequest)WebRequest.Create(ReleaseFeed.LatestPage);
            request.Method = "HEAD";
            request.AllowAutoRedirect = false;
            request.UserAgent = "NoReturnGuardian/" + ReleaseFeed.Display(Current);
            request.Timeout = 15000;
            request.ReadWriteTimeout = 15000;
            using (var response = (HttpWebResponse)request.GetResponse())
            {
                int status = (int)response.StatusCode;
                if (status < 300 || status >= 400)
                {
                    throw new FormatException("The latest-release page answered " + status + " instead of redirecting.");
                }

                return ReleaseFeed.ParseLatestRedirect(response.Headers[HttpResponseHeader.Location]);
            }
        }
    }
}
