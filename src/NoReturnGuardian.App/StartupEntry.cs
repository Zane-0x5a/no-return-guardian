using Microsoft.Win32;
using System;
using System.IO;
using System.Windows.Forms;

namespace NoReturnGuardian
{
    /// <summary>开机启动：当前用户的 Run 项，值是这份 exe 加 --minimized。</summary>
    internal static class StartupEntry
    {
        private const string RunKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
        private const string ValueName = "NoReturnGuardian";

        private static string Command
        {
            get { return "\"" + Application.ExecutablePath + "\" --minimized"; }
        }

        public static bool Exists()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    return key != null && key.GetValue(ValueName) != null;
                }
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException
                || error is System.Security.SecurityException)
            {
                return false;
            }
        }

        /// <summary>打开或关闭；失败时抛出注册表的异常。</summary>
        public static void Set(bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enabled)
                {
                    key.SetValue(ValueName, Command, RegistryValueKind.String);
                }
                else
                {
                    key.DeleteValue(ValueName, false);
                }
            }
        }

        /// <summary>守护器被移动或装到别处后，已开着的开机启动还指向旧位置；每次启动改成当前这份 exe。</summary>
        public static void Refresh()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    string current = key == null ? null : key.GetValue(ValueName) as string;
                    if (current != null && !string.Equals(current, Command, StringComparison.OrdinalIgnoreCase))
                    {
                        key.SetValue(ValueName, Command, RegistryValueKind.String);
                    }
                }
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException
                || error is System.Security.SecurityException)
            {
            }
        }
    }
}
