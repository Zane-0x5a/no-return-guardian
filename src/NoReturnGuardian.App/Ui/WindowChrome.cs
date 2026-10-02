using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace NoReturnGuardian
{
    /// <summary>
    /// 无系统标题栏的窗口外壳：DWM 的深色圆角边框，顶部非客户区并进页面（左右下仍保留系统的隐形缩放边框），
    /// 以及不经 MinimumSize 设置的最小拖动尺寸。
    /// </summary>
    internal static class WindowChrome
    {
        public const int WmNcCalcSize = 0x0083;
        public const int WmGetMinMaxInfo = 0x0024;

        public static void Apply(IntPtr window)
        {
            int dark = 1;
            DwmSetWindowAttribute(window, 20, ref dark, sizeof(int));
            int round = 2;
            DwmSetWindowAttribute(window, 33, ref round, sizeof(int));
            int border = 0x00221F1C;
            DwmSetWindowAttribute(window, 34, ref border, sizeof(int));
            SetWindowPos(window, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020);
        }

        /// <summary>WM_GETMINMAXINFO 交给系统算完之后，换成自己的最小拖动尺寸。</summary>
        public static void SetMinimumTrackSize(IntPtr parameters, Size size)
        {
            var info = (MinMaxInfo)Marshal.PtrToStructure(parameters, typeof(MinMaxInfo));
            info.MinTrackSize.X = size.Width;
            info.MinTrackSize.Y = size.Height;
            Marshal.StructureToPtr(info, parameters, false);
        }

        /// <summary>WM_NCCALCSIZE 交给系统之前的窗口顶边。</summary>
        public static int CaptionTop(IntPtr parameters)
        {
            return ((NcCalcSizeParams)Marshal.PtrToStructure(parameters, typeof(NcCalcSizeParams))).Rect0.Top;
        }

        /// <summary>系统照常算出左右下的缩放边框之后，只把顶部标题栏并进客户区；最大化时让出被推到屏幕外的那截边框。</summary>
        public static void MergeCaption(IntPtr window, IntPtr parameters, int top, int dpi)
        {
            var after = (NcCalcSizeParams)Marshal.PtrToStructure(parameters, typeof(NcCalcSizeParams));
            after.Rect0.Top = top + (IsZoomed(window)
                ? GetSystemMetricsForDpi(33, (uint)dpi) + GetSystemMetricsForDpi(92, (uint)dpi)
                : 0);
            Marshal.StructureToPtr(after, parameters, false);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NcCalcSizeParams
        {
            public NativeRect Rect0;
            public NativeRect Rect1;
            public NativeRect Rect2;
            public IntPtr Position;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MinMaxInfo
        {
            public NativePoint Reserved;
            public NativePoint MaxSize;
            public NativePoint MaxPosition;
            public NativePoint MinTrackSize;
            public NativePoint MaxTrackSize;
        }

        [DllImport("user32.dll")]
        private static extern bool IsZoomed(IntPtr window);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetricsForDpi(int index, uint dpi);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    }
}
