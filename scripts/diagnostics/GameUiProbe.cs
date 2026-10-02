using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

internal static class GameUiProbe
{
    [StructLayout(LayoutKind.Sequential)] struct NativeInput { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] struct InputUnion
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public HardwareInput Hardware;
    }
    [StructLayout(LayoutKind.Sequential)] struct KeyboardInput { public ushort VirtualKey, ScanCode; public uint Flags, Time; public UIntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] struct MouseInput { public int X, Y; public uint MouseData, Flags, Time; public UIntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] struct HardwareInput { public uint Message; public ushort Low, High; }
    [StructLayout(LayoutKind.Sequential)] struct Rectangle { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, NativeInput[] inputs, int size);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr window);
    [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr window, int command);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint first, uint second, bool attach);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window, out Rectangle rectangle);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr window, IntPtr context, uint flags);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    static string directory;
    static void Log(string text)
    {
        string line = DateTimeOffset.Now.ToString("O") + " " + text;
        Console.WriteLine(line);
        File.AppendAllText(Path.Combine(directory, "trace.txt"), line + Environment.NewLine);
    }
    static void Capture(IntPtr window, string name)
    {
        Rectangle rectangle;
        if (!GetWindowRect(window, out rectangle)) throw new Exception("No window rectangle.");
        using (var bitmap = new Bitmap(rectangle.Right - rectangle.Left, rectangle.Bottom - rectangle.Top, PixelFormat.Format32bppArgb))
        using (var graphics = Graphics.FromImage(bitmap))
        {
            IntPtr context = graphics.GetHdc();
            bool captured;
            try { captured = PrintWindow(window, context, 3); }
            finally { graphics.ReleaseHdc(context); }
            if (!captured) throw new Exception("Capture failed.");
            bitmap.Save(Path.Combine(directory, name + ".png"), ImageFormat.Png);
        }
        Log("capture=" + name + " foreground=" + GetForegroundWindow());
    }
    static void Focus(IntPtr window)
    {
        uint ignored;
        uint first = GetWindowThreadProcessId(GetForegroundWindow(), out ignored);
        uint second = GetWindowThreadProcessId(window, out ignored);
        bool attached = first != 0 && second != 0 && first != second && AttachThreadInput(first, second, true);
        try { ShowWindowAsync(window, 9); BringWindowToTop(window); SetForegroundWindow(window); Thread.Sleep(500); }
        finally { if (attached) AttachThreadInput(first, second, false); }
        Log("focus=" + (GetForegroundWindow() == window));
        if (GetForegroundWindow() != window) throw new Exception("No game focus; input blocked.");
    }
    static void SendOne(NativeInput input)
    {
        if (SendInput(1, new[] { input }, Marshal.SizeOf(typeof(NativeInput))) != 1)
            throw new Exception("SendInput failed: " + Marshal.GetLastWin32Error());
    }
    static void Press(IntPtr window, string name, int hold)
    {
        ushort scan;
        bool extended = true;
        switch (name)
        {
            case "up": scan = 0x48; break;
            case "down": scan = 0x50; break;
            case "left": scan = 0x4B; break;
            case "right": scan = 0x4D; break;
            case "enter": scan = 0x1C; extended = false; break;
            case "escape": scan = 0x01; extended = false; break;
            case "space": scan = 0x39; extended = false; break;
            default: throw new Exception("Unsupported action.");
        }
        foreach (int modifier in new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C })
            if ((GetAsyncKeyState(modifier) & 0x8000) != 0) throw new Exception("Modifier held; input blocked.");
        if (GetForegroundWindow() != window) throw new Exception("Focus lost; input blocked.");
        var down = new NativeInput { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { ScanCode = scan, Flags = 8u | (extended ? 1u : 0u) } } };
        var up = down;
        up.Data.Keyboard.Flags |= 2u;
        bool pressed = false;
        try { SendOne(down); pressed = true; Thread.Sleep(hold); }
        finally { if (pressed) SendOne(up); }
        Log("key=" + name + " holdMs=" + hold + " foregroundAfter=" + GetForegroundWindow());
    }
    [STAThread] static int Main(string[] arguments)
    {
        if (arguments.Length < 2) return 2;
        directory = Path.GetFullPath(arguments[0]);
        Directory.CreateDirectory(directory);
        try
        {
            SetProcessDPIAware();
            var processes = Process.GetProcessesByName("tlou-ii");
            if (processes.Length != 1) throw new Exception("Expected exactly one game process.");
            var process = processes[0];
            IntPtr window = process.MainWindowHandle;
            if (window == IntPtr.Zero) throw new Exception("No game window.");
            Log("pid=" + process.Id + " started=" + process.StartTime.ToString("O") + " hwnd=" + window + " inputSize=" + Marshal.SizeOf(typeof(NativeInput)));
            Capture(window, "before");
            if (arguments[1] == "capture") return 0;
            Focus(window);
            Capture(window, "focused");
            int hold = arguments.Length > 2 ? Int32.Parse(arguments[2]) : 180;
            if (hold < 0 || hold > 1000) throw new Exception("Hold outside 0-1000ms.");
            Press(window, arguments[1], hold);
            Thread.Sleep(arguments.Length > 3 ? Int32.Parse(arguments[3]) : 1500);
            Capture(window, "after");
            return 0;
        }
        catch (Exception exception) { Log("STOP " + exception.Message); return 1; }
    }
}
