using System;
using System.Drawing;
using System.Windows.Forms;

namespace NoReturnGuardian
{
    // 只剩实验性暖恢复对话框还用 WinForms 自绘按钮；主界面已改为 WebView2 网页。
    // 2026-10-01 从 16:39 构建的 exe 反编译恢复（源文件被前端构建误删），行为与原文件一致。
    internal class CommandButton : BufferedControl
    {
        private readonly string _glyph;

        private bool _hover;

        private bool _primary;

        private bool _danger;

        internal CommandButton(string glyph, string text)
        {
            _glyph = glyph;
            Text = text;
            base.Height = 44;
            Cursor = Cursors.Hand;
            base.TabStop = true;
            UpdateWidth();
        }

        internal void MarkPrimary()
        {
            _primary = true;
            Invalidate();
        }

        internal void MarkDanger()
        {
            _danger = true;
            Invalidate();
        }

        protected override void OnTextChanged(EventArgs e)
        {
            UpdateWidth();
            base.OnTextChanged(e);
        }

        private void UpdateWidth()
        {
            using Font font = UiPalette.Ui(10f, FontStyle.Bold);
            int num = UiPalette.TextWidth(Text, font);
            base.Width = (string.IsNullOrEmpty(_glyph) ? (16 + num + 16) : (42 + num + 18));
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hover = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            Invalidate();
            base.OnEnabledChanged(e);
        }

        protected override void OnPaintBackground(PaintEventArgs pevent)
        {
        }

        protected override void PaintContent(Graphics g)
        {
            Rectangle bounds = new Rectangle(0, 0, base.Width - 1, base.Height - 1);
            bool enabled = base.Enabled;
            Color color;
            Color color2;
            Color color3;
            Color color4;
            if (_primary)
            {
                color = ((!enabled) ? UiPalette.AmberDeep : (_hover ? ControlPaint.Light(UiPalette.Amber, 0.15f) : UiPalette.Amber));
                color2 = color;
                color3 = (enabled ? UiPalette.AmberInk : Color.FromArgb(150, UiPalette.Amber));
                color4 = color3;
            }
            else if (_danger)
            {
                color = (enabled ? UiPalette.DangerDeep : UiPalette.PanelDeep);
                color2 = ((!enabled) ? Color.FromArgb(120, UiPalette.Danger) : (_hover ? UiPalette.DangerSoft : UiPalette.Danger));
                color3 = (enabled ? UiPalette.DangerSoft : Color.FromArgb(150, UiPalette.Danger));
                color4 = color3;
            }
            else
            {
                color = ((!enabled) ? UiPalette.PanelDeep : ((_hover || Focused) ? UiPalette.ButtonHover : UiPalette.ButtonFace));
                color2 = ((enabled && (_hover || Focused)) ? UiPalette.InkFaint : UiPalette.ButtonEdge);
                color3 = (enabled ? UiPalette.Ink : UiPalette.InkFaint);
                color4 = (enabled ? UiPalette.InkDim : UiPalette.InkFaint);
            }
            UiPaint.FillRound(g, bounds, 5, color);
            UiPaint.StrokeRound(g, bounds, 5, color2, (_hover && enabled) ? 1.6f : 1f);
            int num = 16;
            if (!string.IsNullOrEmpty(_glyph))
            {
                Rectangle bounds2 = new Rectangle(16, (base.Height - 18) / 2, 18, 18);
                UiPaint.DrawGlyph(g, _glyph, bounds2, color4);
                num = bounds2.Right + 8;
            }
            using Font font = UiPalette.Ui(10f, FontStyle.Bold);
            using Brush brush = new SolidBrush(color3);
            SizeF sizeF = g.MeasureString(Text, font);
            g.DrawString(Text, font, brush, num, ((float)base.Height - sizeF.Height) / 2f - 1f);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            if (keyData != Keys.Space && keyData != Keys.Return)
            {
                return base.IsInputKey(keyData);
            }
            return true;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Return)
            {
                InvokeOnClick(this, EventArgs.Empty);
                e.Handled = true;
            }
            else
            {
                base.OnKeyDown(e);
            }
        }
    }

    internal sealed class LedgerButton : CommandButton
    {
        internal LedgerButton(string glyph, string text)
            : base(glyph, text)
        {
            base.Height = 34;
        }
    }
}
