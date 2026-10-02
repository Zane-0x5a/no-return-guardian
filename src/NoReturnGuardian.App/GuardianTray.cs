using NoReturnGuardian.Core;
using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace NoReturnGuardian
{
    /// <summary>
    /// 托盘图标：门洞的轮廓不变，里面那扇小门的颜色就是状态——香槟是能保护，石榴红是征途疑似结束。
    /// 气泡是游戏在前台时玩家能看到的唯一提示。
    /// </summary>
    internal sealed class GuardianTray : IDisposable
    {
        private const string Title = "赴死之旅守护器";
        private readonly NotifyIcon _icon;
        private readonly Icon _guarding;
        private readonly Icon _alert;
        private readonly Icon _idle;
        private Action _balloonClicked;

        public GuardianTray(Action open, Action protect, Action exit)
        {
            Size tray = SystemInformation.SmallIconSize;
            _guarding = LoadIcon("tray-guarding", tray);
            _alert = LoadIcon("tray-alert", tray);
            _idle = LoadIcon("tray-idle", tray);

            var menu = new ContextMenuStrip
            {
                BackColor = Color.FromArgb(20, 19, 17),
                ForeColor = Color.FromArgb(237, 231, 220),
                ShowImageMargin = false,
                Padding = new Padding(4),
                Renderer = new ToolStripProfessionalRenderer(new TrayColorTable())
            };
            menu.Items.Add("打开守护器", null, (sender, args) => open());
            menu.Items.Add("保护当前战备", null, (sender, args) => protect());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, (sender, args) => exit());
            foreach (ToolStripItem item in menu.Items)
            {
                item.Padding = new Padding(10, 5, 18, 5);
            }

            _icon = new NotifyIcon
            {
                Icon = _idle,
                Text = Title,
                Visible = true,
                ContextMenuStrip = menu
            };
            _icon.DoubleClick += (sender, args) => open();
            _icon.BalloonTipClicked += (sender, args) =>
            {
                Action clicked = _balloonClicked;
                _balloonClicked = null;
                if (clicked != null)
                {
                    clicked();
                }
            };
            _icon.BalloonTipClosed += (sender, args) => _balloonClicked = null;
        }

        /// <summary>
        /// 图标都编进 exe（scripts/make-icons.py 生成）：窗口与任务栏用完整的多尺寸图标，
        /// 系统按 DPI 取合适的一帧，不会把 32 像素放大到模糊。
        /// </summary>
        internal static Icon LoadIcon(string name, Size size)
        {
            using (var stream = new MemoryStream(WebShell.ReadResource("NoReturnGuardian.Icons." + name + ".ico")))
            {
                return size.IsEmpty ? new Icon(stream) : new Icon(stream, size);
            }
        }

        public void Update(MonitorStatus status, SnapshotRecord protectedRecord)
        {
            _icon.Icon = status.SuspectedRunEnded ? _alert : status.ManualCaptureReady ? _guarding : _idle;
            _icon.Text = protectedRecord == null
                ? Title
                : Title + " · 最近保护 " + protectedRecord.CreatedUtc.ToLocalTime().ToString("HH:mm");
        }

        public void Balloon(int milliseconds, string title, string text, ToolTipIcon icon, Action clicked = null)
        {
            // 系统只有一个气泡位置，新气泡顶掉旧的；点击只算给最近这一个。
            _balloonClicked = clicked;
            // 气泡正文有长度上限；说明都是一两句话，超长时截短而不是让系统拒绝显示。
            _icon.ShowBalloonTip(milliseconds, title ?? Title, text.Length > 200 ? text.Substring(0, 200) + "…" : text, icon);
        }

        public void Hide()
        {
            _icon.Visible = false;
        }

        public void Dispose()
        {
            _icon.Dispose();
            _guarding.Dispose();
            _alert.Dispose();
            _idle.Dispose();
        }

        private sealed class TrayColorTable : ProfessionalColorTable
        {
            private static readonly Color Face = Color.FromArgb(20, 19, 17);
            private static readonly Color Hover = Color.FromArgb(38, 35, 31);
            private static readonly Color Edge = Color.FromArgb(48, 44, 39);

            public override Color MenuItemSelected { get { return Hover; } }
            public override Color MenuItemBorder { get { return Hover; } }
            public override Color MenuBorder { get { return Edge; } }
            public override Color ToolStripDropDownBackground { get { return Face; } }
            public override Color ImageMarginGradientBegin { get { return Face; } }
            public override Color ImageMarginGradientMiddle { get { return Face; } }
            public override Color ImageMarginGradientEnd { get { return Face; } }
            public override Color SeparatorDark { get { return Edge; } }
            public override Color SeparatorLight { get { return Face; } }
        }
    }
}
