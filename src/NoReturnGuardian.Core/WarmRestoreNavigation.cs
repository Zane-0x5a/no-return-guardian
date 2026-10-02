using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace NoReturnGuardian.Core
{
    public enum WarmRestoreNavigationKey
    {
        Up,
        Down,
        Left,
        Enter,
        Escape
    }

    public interface IWarmRestoreNavigationInput
    {
        Result<bool> FocusGame(IList<int> processIds);
        Result<bool> SendKey(IList<int> processIds, WarmRestoreNavigationKey key);
    }

    public interface IWarmRestoreCreditsProbe
    {
        Result<bool> IsT2RCreditsOpen(
            string gameExecutablePath,
            IList<int> processIds);
    }

    public interface IWarmRestoreNavigator
    {
        Result<bool> OpenT2RCredits();
        Result<bool> ReturnFromT2RCredits();
    }

    public sealed class WarmRestoreNavigationDryRun
    {
        private readonly IWarmRestoreNavigator _navigator;

        public WarmRestoreNavigationDryRun(IWarmRestoreNavigator navigator)
        {
            _navigator = navigator ?? throw new ArgumentNullException("navigator");
        }

        public Result<bool> RunRoundTrip()
        {
            Result<bool> opened = _navigator.OpenT2RCredits();
            if (!opened.Success)
            {
                return opened;
            }

            Result<bool> returned = _navigator.ReturnFromT2RCredits();
            if (!returned.Success)
            {
                return returned;
            }

            return Result<bool>.Ok(
                true,
                "warm_navigation_round_trip_complete",
                "The verified game process opened the exact T2R credits media and returned to the game menu. No save controller was involved.");
        }
    }

    public sealed class WarmRestoreNavigator : IWarmRestoreNavigator
    {
        private readonly IWarmRestoreProcessRuntime _runtime;
        private readonly IWarmRestoreNavigationInput _input;
        private readonly IWarmRestoreCreditsProbe _creditsProbe;
        private readonly IWarmRestoreDelay _delay;

        public WarmRestoreNavigator()
            : this(
                new WindowsWarmRestoreProcessRuntime(),
                new WindowsWarmRestoreNavigationInput(),
                new WindowsWarmRestoreCreditsProbe(),
                new SystemWarmRestoreDelay())
        {
        }

        public WarmRestoreNavigator(
            IWarmRestoreProcessRuntime runtime,
            IWarmRestoreNavigationInput input,
            IWarmRestoreCreditsProbe creditsProbe,
            IWarmRestoreDelay delay)
        {
            _runtime = runtime ?? new WindowsWarmRestoreProcessRuntime();
            _input = input ?? new WindowsWarmRestoreNavigationInput();
            _creditsProbe = creditsProbe ?? new WindowsWarmRestoreCreditsProbe();
            _delay = delay ?? new SystemWarmRestoreDelay();
            KeyDelay = TimeSpan.FromMilliseconds(55);
            FocusDelay = TimeSpan.FromMilliseconds(250);
            PageDelay = TimeSpan.FromMilliseconds(850);
            ConfirmationDelay = TimeSpan.FromMilliseconds(500);
            ProbeDelay = TimeSpan.FromMilliseconds(250);
            ArrivalTimeout = TimeSpan.FromSeconds(12);
            ExitTimeout = TimeSpan.FromSeconds(15);
        }

        public TimeSpan KeyDelay { get; set; }
        public TimeSpan FocusDelay { get; set; }
        public TimeSpan PageDelay { get; set; }
        public TimeSpan ConfirmationDelay { get; set; }
        public TimeSpan ProbeDelay { get; set; }
        public TimeSpan ArrivalTimeout { get; set; }
        public TimeSpan ExitTimeout { get; set; }

        public Result<bool> OpenT2RCredits()
        {
            Result<IWarmRestoreProcessSession> sessionResult =
                _runtime.OpenValidatedSession();
            if (!sessionResult.Success)
            {
                return Result<bool>.Fail(sessionResult.Code, sessionResult.Message);
            }

            using (IWarmRestoreProcessSession session = sessionResult.Value)
            {
                Result<bool> alreadyOpen = _creditsProbe.IsT2RCreditsOpen(
                    session.ExecutablePath,
                    session.ProcessIds);
                if (!alreadyOpen.Success)
                {
                    return alreadyOpen;
                }

                if (alreadyOpen.Value)
                {
                    return Result<bool>.Ok(
                        true,
                        "warm_navigation_credits_already_open",
                        "T2R credits is already open in the verified game process.");
                }

                Result<bool> focused = _input.FocusGame(session.ProcessIds);
                if (!focused.Success)
                {
                    return focused;
                }

                _delay.Wait(FocusDelay);

                // Exact verified-build focus order: Story, No Return, Options, Extras.
                Result<bool> route = SendRepeated(session.ProcessIds, WarmRestoreNavigationKey.Up, 12);
                if (!route.Success)
                {
                    return route;
                }

                route = SendRepeated(session.ProcessIds, WarmRestoreNavigationKey.Down, 3);
                if (!route.Success)
                {
                    return route;
                }

                route = SendOne(session.ProcessIds, WarmRestoreNavigationKey.Enter);
                if (!route.Success)
                {
                    return route;
                }

                _delay.Wait(PageDelay);

                // Credits is the final enabled item in the Extras vertical container.
                route = SendRepeated(session.ProcessIds, WarmRestoreNavigationKey.Down, 16);
                if (!route.Success)
                {
                    return route;
                }

                route = SendOne(session.ProcessIds, WarmRestoreNavigationKey.Enter);
                if (!route.Success)
                {
                    return route;
                }

                _delay.Wait(PageDelay);

                // T2R credits is the first item; original T2 credits is second.
                route = SendRepeated(session.ProcessIds, WarmRestoreNavigationKey.Up, 4);
                if (!route.Success)
                {
                    return route;
                }

                route = SendOne(session.ProcessIds, WarmRestoreNavigationKey.Enter);
                if (!route.Success)
                {
                    return route;
                }

                Result<bool> arrived = WaitForCreditsState(
                    session,
                    true,
                    ArrivalTimeout);
                if (!arrived.Success)
                {
                    return arrived;
                }

                if (!arrived.Value)
                {
                    return Result<bool>.Fail(
                        "warm_navigation_credits_not_reached",
                        "The verified T2R credits media was not opened. No target save was staged.");
                }

                return Result<bool>.Ok(
                    true,
                    "warm_navigation_credits_reached",
                    "The verified game process opened the T2R credits media.");
            }
        }

        public Result<bool> ReturnFromT2RCredits()
        {
            Result<IWarmRestoreProcessSession> sessionResult =
                _runtime.OpenValidatedSession();
            if (!sessionResult.Success)
            {
                return Result<bool>.Fail(sessionResult.Code, sessionResult.Message);
            }

            using (IWarmRestoreProcessSession session = sessionResult.Value)
            {
                Result<bool> open = _creditsProbe.IsT2RCreditsOpen(
                    session.ExecutablePath,
                    session.ProcessIds);
                if (!open.Success)
                {
                    return open;
                }

                if (!open.Value)
                {
                    return Result<bool>.Fail(
                        "warm_navigation_credits_not_open",
                        "T2R credits is no longer open; use the main-menu checkpoint directly.");
                }

                Result<bool> focused = _input.FocusGame(session.ProcessIds);
                if (!focused.Success)
                {
                    return focused;
                }

                _delay.Wait(FocusDelay);

                Result<bool> sent = SendOne(
                    session.ProcessIds,
                    WarmRestoreNavigationKey.Escape);
                if (!sent.Success)
                {
                    return sent;
                }

                _delay.Wait(ConfirmationDelay);
                Result<bool> afterBack = _creditsProbe.IsT2RCreditsOpen(
                    session.ExecutablePath,
                    session.ProcessIds);
                if (!afterBack.Success)
                {
                    return afterBack;
                }

                if (afterBack.Value)
                {
                    // The credits script uses the standard Yes/No dialog. Left anchors Yes.
                    sent = SendRepeated(
                        session.ProcessIds,
                        WarmRestoreNavigationKey.Left,
                        4);
                    if (!sent.Success)
                    {
                        return sent;
                    }

                    sent = SendOne(session.ProcessIds, WarmRestoreNavigationKey.Enter);
                    if (!sent.Success)
                    {
                        return sent;
                    }
                }

                Result<bool> returned = WaitForCreditsState(
                    session,
                    false,
                    ExitTimeout);
                if (!returned.Success)
                {
                    return returned;
                }

                if (!returned.Value)
                {
                    return Result<bool>.Fail(
                        "warm_navigation_credits_exit_timeout",
                        "T2R credits remained open. The staged target is retained for manual continuation.");
                }

                _delay.Wait(PageDelay);
                return Result<bool>.Ok(
                    true,
                    "warm_navigation_main_menu_reached",
                    "T2R credits closed and returned control to the game menu.");
            }
        }

        private Result<bool> WaitForCreditsState(
            IWarmRestoreProcessSession session,
            bool expectedOpen,
            TimeSpan timeout)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            do
            {
                Result<bool> state = _creditsProbe.IsT2RCreditsOpen(
                    session.ExecutablePath,
                    session.ProcessIds);
                if (!state.Success)
                {
                    return state;
                }

                if (state.Value == expectedOpen)
                {
                    return Result<bool>.Ok(
                        true,
                        expectedOpen
                            ? "warm_navigation_credits_reached"
                            : "warm_navigation_main_menu_reached",
                        "The requested T2R credits state was observed.");
                }

                if (stopwatch.Elapsed >= timeout)
                {
                    break;
                }

                _delay.Wait(ProbeDelay);
            }
            while (stopwatch.Elapsed <= timeout);

            return Result<bool>.Ok(
                false,
                expectedOpen
                    ? "warm_navigation_credits_not_reached"
                    : "warm_navigation_credits_still_open",
                "The requested T2R credits state was not observed before timeout.");
        }

        private Result<bool> SendRepeated(
            IList<int> processIds,
            WarmRestoreNavigationKey key,
            int count)
        {
            for (int index = 0; index < count; index++)
            {
                Result<bool> sent = SendOne(processIds, key);
                if (!sent.Success)
                {
                    return sent;
                }
            }

            return Result<bool>.Ok(true, "warm_navigation_input_sent", "Menu input sent.");
        }

        private Result<bool> SendOne(
            IList<int> processIds,
            WarmRestoreNavigationKey key)
        {
            Result<bool> sent = _input.SendKey(processIds, key);
            if (sent.Success)
            {
                _delay.Wait(KeyDelay);
            }

            return sent;
        }
    }

    public sealed class WarmRestoreAutomationController
    {
        private readonly WarmRestoreController _controller;
        private readonly IWarmRestoreNavigator _navigator;

        public WarmRestoreAutomationController(
            WarmRestoreController controller,
            IWarmRestoreNavigator navigator)
        {
            _controller = controller ?? throw new ArgumentNullException("controller");
            _navigator = navigator ?? throw new ArgumentNullException("navigator");
        }

        public Result<WarmRestoreOutcome> StartFromMainMenu(
            string profilePath,
            string snapshotId)
        {
            Result<WarmRestorePreflight> preflight = _controller.PreflightStageEvidence(
                profilePath,
                snapshotId);
            if (!preflight.Success)
            {
                return Result<WarmRestoreOutcome>.Fail(
                    preflight.Code,
                    preflight.Message);
            }

            Result<bool> opened = _navigator.OpenT2RCredits();
            if (!opened.Success)
            {
                return Result<WarmRestoreOutcome>.Fail(opened.Code, opened.Message);
            }

            Result<WarmRestoreOutcome> staged = _controller.Stage(
                profilePath,
                snapshotId,
                preflight.Value);
            if (!staged.Success)
            {
                return staged;
            }

            Result<bool> returned = _navigator.ReturnFromT2RCredits();
            if (!returned.Success)
            {
                return Result<WarmRestoreOutcome>.Fail(returned.Code, returned.Message);
            }

            return _controller.CheckpointMainMenu(profilePath);
        }
    }

    public sealed class WindowsWarmRestoreNavigationInput : IWarmRestoreNavigationInput
    {
        public WindowsWarmRestoreNavigationInput()
        {
            KeyHoldDuration = TimeSpan.FromMilliseconds(180);
        }

        public TimeSpan KeyHoldDuration { get; set; }

        public Result<bool> FocusGame(IList<int> processIds)
        {
            HashSet<int> expected = ProcessSet(processIds);
            if (expected.Count == 0)
            {
                return Result<bool>.Fail(
                    "warm_navigation_process_missing",
                    "No verified game process was available for menu navigation.");
            }

            IntPtr foreground = WarmNavigationNative.GetForegroundWindow();
            if (WindowBelongsTo(foreground, expected))
            {
                return Result<bool>.Ok(
                    true,
                    "warm_navigation_game_focused",
                    "The verified game window already has focus.");
            }

            IntPtr target = FindLargestWindow(expected);
            if (target == IntPtr.Zero)
            {
                return Result<bool>.Fail(
                    "warm_navigation_window_missing",
                    "No visible top-level window belonging to the verified game process was found.");
            }

            WarmNavigationNative.ShowWindowAsync(target, WarmNavigationNative.SW_RESTORE);
            uint currentThread = WarmNavigationNative.GetCurrentThreadId();
            uint foregroundThread = WarmNavigationNative.GetWindowThreadProcessId(
                foreground,
                IntPtr.Zero);
            uint targetThread = WarmNavigationNative.GetWindowThreadProcessId(target, IntPtr.Zero);
            bool foregroundAttached = foregroundThread != 0
                && targetThread != 0
                && foregroundThread != targetThread
                && WarmNavigationNative.AttachThreadInput(
                    foregroundThread,
                    targetThread,
                    true);
            bool currentAttached = currentThread != 0
                && targetThread != 0
                && currentThread != targetThread
                && WarmNavigationNative.AttachThreadInput(
                    currentThread,
                    targetThread,
                    true);
            try
            {
                WarmNavigationNative.BringWindowToTop(target);
                WarmNavigationNative.SetForegroundWindow(target);
                Thread.Sleep(50);
            }
            finally
            {
                if (currentAttached)
                {
                    WarmNavigationNative.AttachThreadInput(currentThread, targetThread, false);
                }

                if (foregroundAttached)
                {
                    WarmNavigationNative.AttachThreadInput(foregroundThread, targetThread, false);
                }
            }

            foreground = WarmNavigationNative.GetForegroundWindow();
            return WindowBelongsTo(foreground, expected)
                ? Result<bool>.Ok(
                    true,
                    "warm_navigation_game_focused",
                    "The verified game window has focus.")
                : Result<bool>.Fail(
                    "warm_navigation_focus_failed",
                    "Windows did not grant foreground focus to the verified game process. No menu input was sent.");
        }

        public Result<bool> SendKey(
            IList<int> processIds,
            WarmRestoreNavigationKey key)
        {
            HashSet<int> expected = ProcessSet(processIds);
            if (!WindowBelongsTo(WarmNavigationNative.GetForegroundWindow(), expected))
            {
                return Result<bool>.Fail(
                    "warm_navigation_focus_lost",
                    "The verified game process lost foreground focus. Menu automation stopped before sending more input.");
            }

            ushort virtualKey = VirtualKey(key);
            ushort scanCode = (ushort)WarmNavigationNative.MapVirtualKey(
                virtualKey,
                WarmNavigationNative.MAPVK_VK_TO_VSC);
            uint flags = WarmNavigationNative.KEYEVENTF_SCANCODE;
            if (key == WarmRestoreNavigationKey.Up
                || key == WarmRestoreNavigationKey.Down
                || key == WarmRestoreNavigationKey.Left)
            {
                flags |= WarmNavigationNative.KEYEVENTF_EXTENDEDKEY;
            }

            WarmNavigationNative.INPUT[] input =
            {
                WarmNavigationNative.KeyboardInput(scanCode, flags)
            };
            int inputSize = Marshal.SizeOf(typeof(WarmNavigationNative.INPUT));
            uint sent = WarmNavigationNative.SendInput(
                (uint)input.Length,
                input,
                inputSize);
            int error = Marshal.GetLastWin32Error();
            if (sent != input.Length)
            {
                return Result<bool>.Fail(
                    "warm_navigation_send_input_failed",
                    "Windows accepted only " + sent + " of " + input.Length
                        + " keyboard input records. Error " + error + ".");
            }

            uint released;
            int releaseError;
            try
            {
                if (KeyHoldDuration > TimeSpan.Zero)
                {
                    Thread.Sleep(KeyHoldDuration);
                }
            }
            finally
            {
                WarmNavigationNative.INPUT[] release =
                {
                    WarmNavigationNative.KeyboardInput(
                        scanCode,
                        flags | WarmNavigationNative.KEYEVENTF_KEYUP)
                };
                released = WarmNavigationNative.SendInput(
                    (uint)release.Length,
                    release,
                    inputSize);
                releaseError = Marshal.GetLastWin32Error();
            }

            if (released != 1)
            {
                return Result<bool>.Fail(
                    "warm_navigation_key_release_failed",
                    "Windows accepted only " + released
                        + " key-release record. Error " + releaseError + ".");
            }

            return Result<bool>.Ok(
                true,
                "warm_navigation_input_sent",
                "Menu input sent to the verified foreground game process.");
        }

        private static ushort VirtualKey(WarmRestoreNavigationKey key)
        {
            switch (key)
            {
                case WarmRestoreNavigationKey.Up: return 0x26;
                case WarmRestoreNavigationKey.Down: return 0x28;
                case WarmRestoreNavigationKey.Left: return 0x25;
                case WarmRestoreNavigationKey.Enter: return 0x0D;
                case WarmRestoreNavigationKey.Escape: return 0x1B;
                default: throw new ArgumentOutOfRangeException("key");
            }
        }

        private static HashSet<int> ProcessSet(IEnumerable<int> processIds)
        {
            return new HashSet<int>((processIds ?? Enumerable.Empty<int>()).Where(value => value > 0));
        }

        private static bool WindowBelongsTo(IntPtr window, ISet<int> processIds)
        {
            if (window == IntPtr.Zero || processIds == null || processIds.Count == 0)
            {
                return false;
            }

            uint processId;
            WarmNavigationNative.GetWindowThreadProcessId(window, out processId);
            return processIds.Contains((int)processId);
        }

        private static IntPtr FindLargestWindow(ISet<int> processIds)
        {
            IntPtr best = IntPtr.Zero;
            long bestArea = 0;
            WarmNavigationNative.EnumWindows((window, parameter) =>
            {
                if (!WarmNavigationNative.IsWindowVisible(window)
                    || !WindowBelongsTo(window, processIds))
                {
                    return true;
                }

                WarmNavigationNative.RECT rectangle;
                if (!WarmNavigationNative.GetWindowRect(window, out rectangle))
                {
                    return true;
                }

                long width = Math.Max(0, rectangle.Right - rectangle.Left);
                long height = Math.Max(0, rectangle.Bottom - rectangle.Top);
                long area = width * height;
                if (area > bestArea)
                {
                    bestArea = area;
                    best = window;
                }

                return true;
            }, IntPtr.Zero);
            return best;
        }
    }

    public sealed class WindowsWarmRestoreCreditsProbe : IWarmRestoreCreditsProbe
    {
        private const int ErrorMoreData = 234;
        private const string CreditsRelativePath =
            @"build\pc\main\movie1\cin-end-credits-t2r.bk2";

        public Result<bool> IsT2RCreditsOpen(
            string gameExecutablePath,
            IList<int> processIds)
        {
            if (string.IsNullOrWhiteSpace(gameExecutablePath))
            {
                return Result<bool>.Fail(
                    "warm_navigation_game_path_missing",
                    "The verified game executable path is unavailable.");
            }

            string root = Path.GetDirectoryName(Path.GetFullPath(gameExecutablePath));
            string moviePath = Path.Combine(root ?? string.Empty, CreditsRelativePath);
            if (!File.Exists(moviePath))
            {
                return Result<bool>.Fail(
                    "warm_navigation_credits_media_missing",
                    "The verified T2R credits media was not found at " + moviePath + ".");
            }

            HashSet<int> expected = new HashSet<int>(
                (processIds ?? new List<int>()).Where(value => value > 0));
            if (expected.Count == 0)
            {
                return Result<bool>.Fail(
                    "warm_navigation_process_missing",
                    "No verified game process was available for credits detection.");
            }

            uint sessionHandle = 0;
            int status = WarmNavigationNative.RmStartSession(
                out sessionHandle,
                0);
            if (status != 0)
            {
                return Result<bool>.Fail(
                    "warm_navigation_resource_probe_failed",
                    "Restart Manager session creation failed with status " + status + ".");
            }

            try
            {
                status = WarmNavigationNative.RmRegisterResources(
                    sessionHandle,
                    1,
                    new[] { moviePath },
                    0,
                    null,
                    0,
                    null);
                if (status != 0)
                {
                    return Result<bool>.Fail(
                        "warm_navigation_resource_probe_failed",
                        "Restart Manager resource registration failed with status " + status + ".");
                }

                uint needed = 0;
                uint count = 0;
                uint rebootReasons = 0;
                status = WarmNavigationNative.RmGetList(
                    sessionHandle,
                    out needed,
                    ref count,
                    null,
                    ref rebootReasons);
                if (status == 0 && needed == 0)
                {
                    return Result<bool>.Ok(
                        false,
                        "warm_navigation_credits_closed",
                        "The T2R credits media is not open.");
                }

                if (status != ErrorMoreData)
                {
                    return Result<bool>.Fail(
                        "warm_navigation_resource_probe_failed",
                        "Restart Manager process discovery failed with status " + status + ".");
                }

                WarmNavigationNative.RM_PROCESS_INFO[] processes =
                    new WarmNavigationNative.RM_PROCESS_INFO[needed];
                count = needed;
                status = WarmNavigationNative.RmGetList(
                    sessionHandle,
                    out needed,
                    ref count,
                    processes,
                    ref rebootReasons);
                if (status != 0)
                {
                    return Result<bool>.Fail(
                        "warm_navigation_resource_probe_failed",
                        "Restart Manager process enumeration failed with status " + status + ".");
                }

                bool usedByGame = processes.Take((int)count).Any(
                    item => expected.Contains(item.Process.dwProcessId));
                return Result<bool>.Ok(
                    usedByGame,
                    usedByGame
                        ? "warm_navigation_credits_open"
                        : "warm_navigation_credits_closed",
                    usedByGame
                        ? "The verified game process is using the T2R credits media."
                        : "The verified game process is not using the T2R credits media.");
            }
            catch (Exception exception)
            {
                return Result<bool>.Fail(
                    "warm_navigation_resource_probe_failed",
                    exception.Message);
            }
            finally
            {
                WarmNavigationNative.RmEndSession(sessionHandle);
            }
        }
    }

    internal static class WarmNavigationNative
    {
        internal const int SW_RESTORE = 9;
        internal const uint MAPVK_VK_TO_VSC = 0;
        internal const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        internal const uint KEYEVENTF_KEYUP = 0x0002;
        internal const uint KEYEVENTF_SCANCODE = 0x0008;
        private const uint INPUT_KEYBOARD = 1;

        internal delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT
        {
            internal int Left;
            internal int Top;
            internal int Right;
            internal int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct INPUT
        {
            internal uint Type;
            internal InputUnion Data;
        }

        [StructLayout(LayoutKind.Explicit)]
        internal struct InputUnion
        {
            [FieldOffset(0)]
            internal MOUSEINPUT Mouse;

            [FieldOffset(0)]
            internal KEYBDINPUT Keyboard;

            [FieldOffset(0)]
            internal HARDWAREINPUT Hardware;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MOUSEINPUT
        {
            internal int X;
            internal int Y;
            internal uint MouseData;
            internal uint Flags;
            internal uint Time;
            internal UIntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct KEYBDINPUT
        {
            internal ushort VirtualKey;
            internal ushort ScanCode;
            internal uint Flags;
            internal uint Time;
            internal UIntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct HARDWAREINPUT
        {
            internal uint Message;
            internal ushort ParameterLow;
            internal ushort ParameterHigh;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct RM_UNIQUE_PROCESS
        {
            internal int dwProcessId;
            internal System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct RM_PROCESS_INFO
        {
            internal RM_UNIQUE_PROCESS Process;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            internal string AppName;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            internal string ServiceShortName;

            internal uint ApplicationType;
            internal uint AppStatus;
            internal uint TerminalSessionId;

            [MarshalAs(UnmanagedType.Bool)]
            internal bool Restartable;
        }

        internal static INPUT KeyboardInput(ushort scanCode, uint flags)
        {
            return new INPUT
            {
                Type = INPUT_KEYBOARD,
                Data = new InputUnion
                {
                    Keyboard = new KEYBDINPUT
                    {
                        ScanCode = scanCode,
                        Flags = flags
                    }
                }
            };
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowRect(IntPtr window, out RECT rectangle);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool BringWindowToTop(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ShowWindowAsync(IntPtr window, int command);

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr window, IntPtr processId);

        [DllImport("kernel32.dll")]
        internal static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AttachThreadInput(
            uint currentThread,
            uint targetThread,
            [MarshalAs(UnmanagedType.Bool)] bool attach);

        [DllImport("user32.dll")]
        internal static extern uint MapVirtualKey(uint code, uint mapType);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint SendInput(
            uint count,
            [In] INPUT[] inputs,
            int size);

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        internal static extern int RmStartSession(
            out uint sessionHandle,
            int sessionFlags,
            StringBuilder sessionKey);

        internal static int RmStartSession(
            out uint sessionHandle,
            int sessionFlags)
        {
            return RmStartSession(
                out sessionHandle,
                sessionFlags,
                new StringBuilder(33));
        }

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        internal static extern int RmRegisterResources(
            uint sessionHandle,
            uint fileCount,
            string[] fileNames,
            uint applicationCount,
            RM_UNIQUE_PROCESS[] applications,
            uint serviceCount,
            string[] serviceNames);

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        internal static extern int RmGetList(
            uint sessionHandle,
            out uint processInfoNeeded,
            ref uint processInfoCount,
            [In, Out] RM_PROCESS_INFO[] processInfo,
            ref uint rebootReasons);

        [DllImport("rstrtmgr.dll")]
        internal static extern int RmEndSession(uint sessionHandle);
    }
}
