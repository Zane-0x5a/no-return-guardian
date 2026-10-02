using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace NoReturnGuardian.Core
{
    /// <summary>
    /// Finds the installed game for the "launch" button before it has ever been seen running: Steam
    /// libraries, Epic manifests and the Windows uninstall entries. Guardian otherwise learns the
    /// executable from the running process, and asks the player when nothing is found.
    /// </summary>
    public static class GameLocator
    {
        public const string ExecutableName = "tlou-ii.exe";
        private const string GameFolder = "The Last of Us Part II";

        public static string FindExecutable()
        {
            return Candidates().FirstOrDefault(IsGameExecutable);
        }

        public static bool IsGameExecutable(string path)
        {
            try
            {
                return !string.IsNullOrWhiteSpace(path)
                    && string.Equals(Path.GetFileName(path), ExecutableName, StringComparison.OrdinalIgnoreCase)
                    && File.Exists(path);
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private static IEnumerable<string> Candidates()
        {
            foreach (string library in SteamLibraries())
            {
                yield return Path.Combine(library, "steamapps", "common", GameFolder, ExecutableName);
            }

            foreach (string location in EpicLocations().Concat(UninstallLocations()))
            {
                yield return Path.Combine(location, ExecutableName);
                yield return Path.Combine(location, GameFolder, ExecutableName);
            }
        }

        private static IEnumerable<string> SteamLibraries()
        {
            var roots = new List<string>();
            AddRegistryValue(roots, RegistryHive.CurrentUser, @"Software\Valve\Steam", "SteamPath");
            AddRegistryValue(roots, RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
            AddRegistryValue(roots, RegistryHive.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath");
            var libraries = new List<string>();
            foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                libraries.Add(root);
                string folders = Path.Combine(root, "steamapps", "libraryfolders.vdf");
                string text = ReadText(folders);
                if (text == null)
                {
                    continue;
                }

                // Both the current ("path" "D:\\Steam") and the oldest ("1" "D:\\Steam") layouts.
                foreach (Match match in Regex.Matches(text, "\"(?:path|\\d+)\"\\s+\"([^\"]+)\""))
                {
                    libraries.Add(match.Groups[1].Value.Replace("\\\\", "\\"));
                }
            }

            return libraries.Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static IEnumerable<string> EpicLocations()
        {
            string manifests = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Epic", "EpicGamesLauncher", "Data", "Manifests");
            string[] files;
            try
            {
                files = Directory.Exists(manifests) ? Directory.GetFiles(manifests, "*.item") : new string[0];
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                yield break;
            }

            var serializer = new JavaScriptSerializer();
            foreach (string file in files)
            {
                Dictionary<string, object> item = null;
                try
                {
                    item = serializer.Deserialize<Dictionary<string, object>>(ReadText(file) ?? "{}");
                }
                catch (ArgumentException)
                {
                }
                catch (InvalidOperationException)
                {
                }

                object name, location;
                if (item != null && item.TryGetValue("DisplayName", out name) && Mentions(name as string)
                    && item.TryGetValue("InstallLocation", out location) && location is string)
                {
                    yield return (string)location;
                }
            }
        }

        private static IEnumerable<string> UninstallLocations()
        {
            var found = new List<string>();
            foreach (RegistryHive hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            {
                foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    try
                    {
                        using (RegistryKey root = RegistryKey.OpenBaseKey(hive, view))
                        using (RegistryKey uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
                        {
                            if (uninstall == null)
                            {
                                continue;
                            }

                            foreach (string name in uninstall.GetSubKeyNames())
                            {
                                using (RegistryKey entry = uninstall.OpenSubKey(name))
                                {
                                    if (entry != null && Mentions(entry.GetValue("DisplayName") as string)
                                        && entry.GetValue("InstallLocation") is string)
                                    {
                                        found.Add((string)entry.GetValue("InstallLocation"));
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception error) when (error is IOException || error is UnauthorizedAccessException
                        || error is System.Security.SecurityException)
                    {
                    }
                }
            }

            return found;
        }

        private static bool Mentions(string name)
        {
            return name != null && name.IndexOf("Last of Us Part II", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void AddRegistryValue(List<string> values, RegistryHive hive, string key, string name)
        {
            try
            {
                using (RegistryKey root = RegistryKey.OpenBaseKey(hive, RegistryView.Default))
                using (RegistryKey entry = root.OpenSubKey(key))
                {
                    string value = entry == null ? null : entry.GetValue(name) as string;
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        values.Add(value.Replace('/', '\\'));
                    }
                }
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException
                || error is System.Security.SecurityException)
            {
            }
        }

        private static string ReadText(string path)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                return null;
            }
        }
    }
}
