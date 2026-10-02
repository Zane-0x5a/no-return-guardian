using System;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace NoReturnGuardian
{
    // 托盘图标与实验性暖恢复对话框仍用的 GDI+ 调色板和绘制工具；主界面已改为 WebView2 网页。
    // 2026-10-01 从 16:39 构建的 exe 反编译恢复（源文件被前端构建误删），行为与原文件一致。
    internal static class UiPalette
    {
        internal static readonly Color Canvas = Color.FromArgb(16, 18, 20);

        internal static readonly Color Panel = Color.FromArgb(26, 29, 32);

        internal static readonly Color PanelDeep = Color.FromArgb(20, 22, 25);

        internal static readonly Color Row = Color.FromArgb(31, 35, 39);

        internal static readonly Color RowAlt = Color.FromArgb(27, 30, 34);

        internal static readonly Color RowHover = Color.FromArgb(37, 42, 47);

        internal static readonly Color RowSelected = Color.FromArgb(35, 41, 48);

        internal static readonly Color Line = Color.FromArgb(42, 46, 51);

        internal static readonly Color LineSoft = Color.FromArgb(34, 38, 42);

        internal static readonly Color Ink = Color.FromArgb(242, 241, 236);

        internal static readonly Color InkDim = Color.FromArgb(155, 161, 168);

        internal static readonly Color InkFaint = Color.FromArgb(107, 112, 118);

        internal static readonly Color Amber = Color.FromArgb(232, 163, 61);

        internal static readonly Color AmberDeep = Color.FromArgb(74, 54, 24);

        internal static readonly Color AmberInk = Color.FromArgb(26, 19, 5);

        internal static readonly Color Danger = Color.FromArgb(217, 83, 79);

        internal static readonly Color DangerSoft = Color.FromArgb(233, 148, 140);

        internal static readonly Color DangerDeep = Color.FromArgb(66, 32, 30);

        internal static readonly Color LedOff = Color.FromArgb(74, 79, 85);

        internal static readonly Color ButtonFace = Color.FromArgb(34, 38, 43);

        internal static readonly Color ButtonHover = Color.FromArgb(41, 46, 52);

        internal static readonly Color ButtonEdge = Color.FromArgb(58, 64, 70);

        private static readonly string UiFamily = PickFamily("Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI");

        private static readonly string MonoFamily = PickFamily("Consolas", "Courier New");

        private static Graphics _measureGraphics;

        private static string PickFamily(params string[] candidates)
        {
            foreach (string text in candidates)
            {
                try
                {
                    using FontFamily fontFamily = new FontFamily(text);
                    if (string.Equals(fontFamily.Name, text, StringComparison.OrdinalIgnoreCase))
                    {
                        return text;
                    }
                }
                catch (ArgumentException)
                {
                }
            }
            return FontFamily.GenericSansSerif.Name;
        }

        internal static Font Ui(float size, FontStyle style)
        {
            return new Font(UiFamily, size, style, GraphicsUnit.Point);
        }

        internal static Font Mono(float size, FontStyle style)
        {
            return new Font(MonoFamily, size, style, GraphicsUnit.Point);
        }

        internal static int LineHeight(Font font)
        {
            if (_measureGraphics == null)
            {
                _measureGraphics = Graphics.FromHwnd(IntPtr.Zero);
            }
            return (int)Math.Ceiling(font.GetHeight(_measureGraphics));
        }

        internal static int TextWidth(string text, Font font)
        {
            if (_measureGraphics == null)
            {
                _measureGraphics = Graphics.FromHwnd(IntPtr.Zero);
            }
            return (int)Math.Ceiling(_measureGraphics.MeasureString(text, font).Width);
        }
    }

    internal static class UiPaint
    {
        internal static GraphicsPath RoundRect(Rectangle bounds, int radius)
        {
            int num = radius * 2;
            GraphicsPath graphicsPath = new GraphicsPath();
            if (num <= 0)
            {
                graphicsPath.AddRectangle(bounds);
                return graphicsPath;
            }
            graphicsPath.AddArc(bounds.X, bounds.Y, num, num, 180f, 90f);
            graphicsPath.AddArc(bounds.Right - num, bounds.Y, num, num, 270f, 90f);
            graphicsPath.AddArc(bounds.Right - num, bounds.Bottom - num, num, num, 0f, 90f);
            graphicsPath.AddArc(bounds.X, bounds.Bottom - num, num, num, 90f, 90f);
            graphicsPath.CloseFigure();
            return graphicsPath;
        }

        internal static void FillRound(Graphics g, Rectangle bounds, int radius, Color color)
        {
            using GraphicsPath path = RoundRect(bounds, radius);
            using SolidBrush brush = new SolidBrush(color);
            g.FillPath(brush, path);
        }

        internal static void StrokeRound(Graphics g, Rectangle bounds, int radius, Color color, float width)
        {
            using GraphicsPath path = RoundRect(bounds, radius);
            using Pen pen = new Pen(color, width);
            g.DrawPath(pen, path);
        }

        internal static void DrawLamp(Graphics g, Rectangle bounds, Color core, bool lit, float glow)
        {
            if (lit && glow > 0.01f)
            {
                Rectangle rect = bounds;
                rect.Inflate((int)((float)bounds.Width * 0.55f * glow), (int)((float)bounds.Height * 0.55f * glow));
                using GraphicsPath graphicsPath = new GraphicsPath();
                graphicsPath.AddEllipse(rect);
                using PathGradientBrush pathGradientBrush = new PathGradientBrush(graphicsPath);
                pathGradientBrush.CenterColor = Color.FromArgb((int)(70f * glow), core);
                pathGradientBrush.SurroundColors = new Color[1] { Color.FromArgb(0, core) };
                g.FillEllipse(pathGradientBrush, rect);
            }
            Color color = (lit ? core : UiPalette.LedOff);
            using (SolidBrush brush = new SolidBrush(color))
            {
                g.FillEllipse(brush, bounds);
            }
            using (Pen pen = new Pen(ControlPaint.Dark(color, 0.35f), 1f))
            {
                g.DrawEllipse(pen, bounds);
            }
            if (lit)
            {
                Rectangle rect2 = new Rectangle(bounds.X + bounds.Width / 4, bounds.Y + bounds.Height / 5, Math.Max(2, bounds.Width / 3), Math.Max(2, bounds.Height / 4));
                using SolidBrush brush2 = new SolidBrush(Color.FromArgb(120, Color.White));
                g.FillEllipse(brush2, rect2);
            }
        }

        internal static void DrawGlyph(Graphics g, string name, Rectangle bounds, Color color)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using Pen pen = new Pen(color, 1.6f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            using SolidBrush brush = new SolidBrush(color);
            int x = bounds.X;
            int y = bounds.Y;
            int width = bounds.Width;
            int height = bounds.Height;
            if (name == null)
            {
                return;
            }
            switch (name.Length)
            {
            case 6:
                switch (name[0])
                {
                case 's':
                    if (name == "shield")
                    {
                        PointF[] points2 = new PointF[6]
                        {
                            new PointF((float)x + (float)width * 0.5f, y + 1),
                            new PointF(x + width - 2, (float)y + (float)height * 0.22f),
                            new PointF(x + width - 2, (float)y + (float)height * 0.55f),
                            new PointF((float)x + (float)width * 0.5f, y + height - 1),
                            new PointF(x + 2, (float)y + (float)height * 0.55f),
                            new PointF(x + 2, (float)y + (float)height * 0.22f)
                        };
                        g.DrawPolygon(pen, points2);
                        g.DrawLine(pen, new PointF((float)x + (float)width * 0.34f, (float)y + (float)height * 0.5f), new PointF((float)x + (float)width * 0.47f, (float)y + (float)height * 0.64f));
                        g.DrawLine(pen, new PointF((float)x + (float)width * 0.47f, (float)y + (float)height * 0.64f), new PointF((float)x + (float)width * 0.7f, (float)y + (float)height * 0.34f));
                    }
                    break;
                case 'f':
                    if (name == "folder")
                    {
                        Rectangle rect = new Rectangle(x + 1, y + height / 3, width - 2, height - height / 3 - 1);
                        g.DrawRectangle(pen, rect);
                        g.DrawLine(pen, x + 1, y + height / 3, x + width / 3, y + height / 3);
                        g.DrawLine(pen, x + width / 3, y + height / 3, x + width / 3 + 2, y + 2);
                        g.DrawLine(pen, x + width / 3 + 2, y + 2, x + width / 2 + 3, y + 2);
                        g.DrawLine(pen, x + width / 2 + 3, y + 2, x + width / 2 + 5, y + height / 3);
                    }
                    break;
                }
                break;
            case 4:
                switch (name[0])
                {
                case 'u':
                    if (name == "undo")
                    {
                        g.DrawArc(pen, x + 2, y + 2, width - 4, height - 4, 240, 250);
                        PointF[] points4 = new PointF[3]
                        {
                            new PointF((float)x + (float)width * 0.58f, (float)y + (float)height * 0.06f),
                            new PointF((float)x + (float)width * 0.86f, (float)y + (float)height * 0.22f),
                            new PointF((float)x + (float)width * 0.52f, (float)y + (float)height * 0.36f)
                        };
                        g.FillPolygon(brush, points4);
                    }
                    break;
                case 'p':
                    if (name == "play")
                    {
                        PointF[] points3 = new PointF[3]
                        {
                            new PointF((float)x + (float)width * 0.3f, (float)y + (float)height * 0.18f),
                            new PointF((float)x + (float)width * 0.3f, (float)y + (float)height * 0.82f),
                            new PointF((float)x + (float)width * 0.8f, (float)y + (float)height * 0.5f)
                        };
                        g.FillPolygon(brush, points3);
                    }
                    break;
                }
                break;
            case 5:
                switch (name[2])
                {
                case 'a':
                    if (name == "trash")
                    {
                        g.DrawLine(pen, x + 4, y + 5, x + width - 4, y + 5);
                        g.DrawLine(pen, x + 7, y + 2, x + width - 7, y + 2);
                        g.DrawRectangle(pen, x + 5, y + 6, width - 10, height - 8);
                        g.DrawLine(pen, x + 8, y + 9, x + 8, y + height - 5);
                        g.DrawLine(pen, x + width - 8, y + 9, x + width - 8, y + height - 5);
                    }
                    break;
                case 'e':
                    if (name == "check")
                    {
                        g.DrawLine(pen, new PointF((float)x + (float)width * 0.18f, (float)y + (float)height * 0.55f), new PointF((float)x + (float)width * 0.42f, (float)y + (float)height * 0.76f));
                        g.DrawLine(pen, new PointF((float)x + (float)width * 0.42f, (float)y + (float)height * 0.76f), new PointF((float)x + (float)width * 0.84f, (float)y + (float)height * 0.26f));
                    }
                    break;
                case 'o':
                    if (name == "cross")
                    {
                        g.DrawLine(pen, x + 3, y + 3, x + width - 3, y + height - 3);
                        g.DrawLine(pen, x + width - 3, y + 3, x + 3, y + height - 3);
                    }
                    break;
                case 'w':
                    if (name == "power")
                    {
                        g.DrawArc(pen, x + 3, y + 3, width - 6, height - 6, 130, 280);
                        g.DrawLine(pen, new PointF((float)x + (float)width / 2f, y + 1), new PointF((float)x + (float)width / 2f, (float)y + (float)height * 0.52f));
                    }
                    break;
                }
                break;
            case 7:
                if (name == "restore")
                {
                    g.DrawArc(pen, x + 2, y + 2, width - 4, height - 4, 300, 265);
                    PointF[] points = new PointF[3]
                    {
                        new PointF((float)x + (float)width * 0.16f, (float)y + (float)height * 0.3f),
                        new PointF((float)x + (float)width * 0.16f, (float)y + (float)height * 0.62f),
                        new PointF((float)x + (float)width * 0.44f, (float)y + (float)height * 0.48f)
                    };
                    g.FillPolygon(brush, points);
                }
                break;
            }
        }

        internal static Icon BuildTrayIcon(Color lampColor, bool lit)
        {
            int num = 32;
            using Bitmap bitmap = new Bitmap(num, num);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                Rectangle rectangle = new Rectangle(5, 3, num - 10, num - 7);
                using (GraphicsPath graphicsPath = new GraphicsPath())
                {
                    graphicsPath.AddLine(rectangle.X + rectangle.Width / 2, rectangle.Y, rectangle.Right, rectangle.Y + 6);
                    graphicsPath.AddLine(rectangle.Right, rectangle.Y + 6, rectangle.Right, rectangle.Y + 15);
                    graphicsPath.AddLine(rectangle.Right, rectangle.Y + 15, rectangle.X + rectangle.Width / 2, rectangle.Bottom);
                    graphicsPath.AddLine(rectangle.X + rectangle.Width / 2, rectangle.Bottom, rectangle.X, rectangle.Y + 15);
                    graphicsPath.AddLine(rectangle.X, rectangle.Y + 15, rectangle.X, rectangle.Y + 6);
                    graphicsPath.CloseFigure();
                    using (SolidBrush brush = new SolidBrush(UiPalette.Panel))
                    {
                        graphics.FillPath(brush, graphicsPath);
                    }
                    using Pen pen = new Pen(UiPalette.InkDim, 1.6f);
                    graphics.DrawPath(pen, graphicsPath);
                }
                DrawLamp(graphics, new Rectangle(11, 10, 10, 10), lampColor, lit, lit ? 0.9f : 0f);
            }
            return (Icon)Icon.FromHandle(bitmap.GetHicon()).Clone();
        }
    }

    internal static class UiWindow
    {
        private const int ImmersiveDarkMode = 20;

        private const int ImmersiveDarkModeLegacy = 19;

        private const int BorderColor = 34;

        private const int CaptionColor = 35;

        private const int CaptionTextColor = 36;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        internal static void ApplyDarkChrome(Form form)
        {
            try
            {
                if (!Set(form.Handle, 20, 1))
                {
                    Set(form.Handle, 19, 1);
                }
                Set(form.Handle, 35, ColorRef(UiPalette.Canvas));
                Set(form.Handle, 36, ColorRef(UiPalette.InkDim));
                Set(form.Handle, 34, ColorRef(UiPalette.Line));
            }
            catch (DllNotFoundException)
            {
            }
            catch (EntryPointNotFoundException)
            {
            }
        }

        private static bool Set(IntPtr window, int attribute, int value)
        {
            return DwmSetWindowAttribute(window, attribute, ref value, 4) == 0;
        }

        private static int ColorRef(Color color)
        {
            return color.R | (color.G << 8) | (color.B << 16);
        }
    }

    internal abstract class BufferedControl : Control
    {
        protected BufferedControl()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, value: true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            PaintContent(e.Graphics);
        }

        protected abstract void PaintContent(Graphics g);
    }
}
