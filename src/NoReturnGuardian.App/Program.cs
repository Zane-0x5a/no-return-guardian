using NoReturnGuardian.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace NoReturnGuardian
{
    internal static class Program
    {
        private const string MutexName = "Local\\NoReturnGuardian-2FE084E5-A526-474D-9E78-22F0068C78A1";

        /// <summary>再次启动时，新进程用这个事件叫已在运行的守护器把窗口拿出来，自己随即退出。</summary>
        internal const string ShowEventName = "Local\\NoReturnGuardian-Show-2FE084E5-A526-474D-9E78-22F0068C78A1";

        /// <summary>--exit：安装程序请已在运行的守护器像托盘“退出”一样退出；没有在运行的就什么也不做。</summary>
        internal const string ExitEventName = "Local\\NoReturnGuardian-Exit-2FE084E5-A526-474D-9E78-22F0068C78A1";

        private static readonly object AssemblyLock = new object();
        private static readonly Dictionary<string, Assembly> Embedded =
            new Dictionary<string, Assembly>();

        static Program()
        {
            // WebView2 的两个托管程序集编进 exe；第一次用到时从资源加载，exe 仍可单独运行。
            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
            {
                string name = new AssemblyName(args.Name).Name;
                string resource = name == "Microsoft.Web.WebView2.Core" ? "NoReturnGuardian.WebView2.Core.dll"
                    : name == "Microsoft.Web.WebView2.WinForms" ? "NoReturnGuardian.WebView2.WinForms.dll"
                    : null;
                if (resource == null)
                {
                    return null;
                }

                lock (AssemblyLock)
                {
                    Assembly loaded;
                    if (!Embedded.TryGetValue(name, out loaded))
                    {
                        loaded = Assembly.Load(WebShell.ReadResource(resource));
                        Embedded[name] = loaded;
                    }

                    return loaded;
                }
            };
        }

        [STAThread]
        private static void Main(string[] args)
        {
            if (args.Any(value => string.Equals(value, "--exit", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    using (EventWaitHandle exit = EventWaitHandle.OpenExisting(ExitEventName))
                    {
                        exit.Set();
                    }
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                }

                return;
            }

            string warmNavigationDryRunPath = GetOptionValue(
                args,
                "--warm-navigation-dry-run");
            if (!string.IsNullOrWhiteSpace(warmNavigationDryRunPath))
            {
                RunWarmNavigationDryRun(warmNavigationDryRunPath);
                return;
            }

            string renderPath = GetOptionValue(args, "--render-ui");
            if (!string.IsNullOrWhiteSpace(renderPath))
            {
                // 截图模式：用真实状态渲染一次界面并保存 PNG，不注册托盘与快捷键，不写任何存档。
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                RunRender(new RenderOptions
                {
                    Path = renderPath,
                    Width = GetOptionInt(args, "--render-width"),
                    Height = GetOptionInt(args, "--render-height"),
                    DevUrl = GetOptionValue(args, "--ui-url"),
                    Script = GetOptionValue(args, "--render-script"),
                    WaitMs = GetOptionInt(args, "--render-wait"),
                    HideSeconds = GetOptionInt(args, "--render-hide")
                });
                return;
            }

            string warmRenderPath = GetOptionValue(args, "--render-warm-ui");
            if (!string.IsNullOrWhiteSpace(warmRenderPath))
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                string temporaryRoot = Path.Combine(
                    Path.GetTempPath(),
                    "NoReturnGuardian-WarmRender-" + Guid.NewGuid().ToString("N"));
                try
                {
                    SnapshotStore store = new SnapshotStore(
                        temporaryRoot,
                        new SystemClock());
                    WarmRestoreController controller = new WarmRestoreController(
                        store,
                        new SystemClock());
                    using (WarmRestoreDialog dialog = new WarmRestoreDialog(
                        controller,
                        new WarmRestoreAutomationController(
                            controller,
                            new WarmRestoreNavigator()),
                        "render-profile",
                        "render-snapshot",
                        true))
                    {
                        dialog.RenderToPng(
                            warmRenderPath,
                            GetOptionValue(args, "--render-warm-phase"),
                            GetOptionInt(args, "--render-dpi"));
                    }
                }
                finally
                {
                    if (Directory.Exists(temporaryRoot))
                    {
                        Directory.Delete(temporaryRoot, true);
                    }
                }
                return;
            }

            bool ownsMutex;
            using (Mutex mutex = new Mutex(true, MutexName, out ownsMutex))
            {
                if (!ownsMutex)
                {
                    // 开机自启遇到已在运行的守护器时什么也不做；手动再打开一次，就把已有的窗口拿出来。
                    if (!args.Any(value => string.Equals(value, "--minimized", StringComparison.OrdinalIgnoreCase)))
                    {
                        ShowRunningInstance();
                    }

                    return;
                }

                // 互斥量一建好就建好两个请求事件：安装程序或再次启动的进程看到互斥量时，事件已经在了。
                // 窗口建好之前到达的请求留在事件里，MainForm 开始监听后接着处理。
                using (new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName))
                using (new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName))
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    Application.ThreadException += (sender, eventArgs) =>
                        ReportUnhandled(eventArgs.Exception);
                    AppDomain.CurrentDomain.UnhandledException += (sender, eventArgs) =>
                        ReportUnhandled(eventArgs.ExceptionObject as Exception);

                    bool startMinimized = args.Any(value => string.Equals(
                        value,
                        "--minimized",
                        StringComparison.OrdinalIgnoreCase));
                    RunGuardian(startMinimized);
                }

                GC.KeepAlive(mutex);
            }
        }

        private static void ShowRunningInstance()
        {
            try
            {
                // 刚被玩家启动的这个进程有前台权限，转给已在运行的那个，它的窗口才能真正到前面来。
                AllowSetForegroundWindow(-1);
                using (EventWaitHandle show = EventWaitHandle.OpenExisting(ShowEventName))
                {
                    show.Set();
                }
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // 正在运行的是没有这个事件的旧版本。
                MessageBox.Show(
                    "赴死之旅守护器已经在运行，请查看任务栏托盘。",
                    "赴死之旅守护器",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
        }

        [DllImport("user32.dll")]
        private static extern bool AllowSetForegroundWindow(int processId);

        // 单独成方法：MainForm 引用 WebView2 类型，要在 AssemblyResolve 注册之后才编译。
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RunGuardian(bool startMinimized)
        {
            Application.Run(new MainForm(startMinimized));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RunRender(RenderOptions options)
        {
            using (var form = new MainForm(false, options))
            {
                Application.Run(form);
            }
        }

        private static string GetOptionValue(string[] args, string option)
        {
            if (args == null)
            {
                return null;
            }

            for (int index = 0; index < args.Length - 1; index++)
            {
                if (string.Equals(args[index], option, StringComparison.OrdinalIgnoreCase))
                {
                    return args[index + 1];
                }
            }

            return null;
        }

        private static int GetOptionInt(string[] args, string option)
        {
            int value;
            return int.TryParse(GetOptionValue(args, option), out value) ? value : 0;
        }

        private static void RunWarmNavigationDryRun(string outputPath)
        {
            Result<bool> result;
            try
            {
                result = new WarmRestoreNavigationDryRun(
                    new WarmRestoreNavigator()).RunRoundTrip();
            }
            catch (Exception exception)
            {
                result = Result<bool>.Fail(
                    "warm_navigation_dry_run_failed",
                    exception.Message);
            }

            string fullPath = Path.GetFullPath(outputPath);
            string directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(
                fullPath,
                DateTime.UtcNow.ToString("O") + Environment.NewLine
                + (result.Success ? "SUCCESS" : "FAILURE") + Environment.NewLine
                + result.Code + Environment.NewLine
                + result.Message + Environment.NewLine);
            Environment.ExitCode = result.Success ? 0 : 2;
        }

        private static void ReportUnhandled(Exception exception)
        {
            try
            {
                string directory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "NoReturnGuardian");
                Directory.CreateDirectory(directory);
                File.AppendAllText(
                    Path.Combine(directory, "guardian-error.log"),
                    DateTime.UtcNow.ToString("O") + Environment.NewLine
                    + (exception == null ? "Unknown error" : exception.ToString())
                    + Environment.NewLine + Environment.NewLine);
            }
            catch
            {
                // The UI error remains available even if logging is unavailable.
            }

            MessageBox.Show(
                "守护器出了意外错误。如果它表现异常，从托盘图标菜单退出后重新打开。",
                "赴死之旅守护器",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
}
