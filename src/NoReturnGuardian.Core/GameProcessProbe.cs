using System;
using System.Diagnostics;
using System.Linq;

namespace NoReturnGuardian.Core
{
    public sealed class GameProcessProbe : IGameProcessProbe
    {
        private static readonly string[] ProcessNames = { "tlou-ii", "tlou-ii-l" };

        public bool IsGameRunning()
        {
            foreach (string name in ProcessNames)
            {
                Process[] processes = Process.GetProcessesByName(name);
                try
                {
                    if (processes.Length > 0)
                    {
                        return true;
                    }
                }
                finally
                {
                    foreach (Process process in processes)
                    {
                        process.Dispose();
                    }
                }
            }

            return false;
        }

        public string FindGameExecutablePath()
        {
            foreach (string name in ProcessNames)
            {
                Process[] processes = Process.GetProcessesByName(name);
                string executablePath = null;
                try
                {
                    foreach (Process process in processes)
                    {
                        try
                        {
                            if (process.MainModule != null)
                            {
                                executablePath = process.MainModule.FileName;
                                break;
                            }
                        }
                        catch
                        {
                            // Process metadata can be inaccessible across privilege levels.
                        }
                    }
                }
                finally
                {
                    foreach (Process process in processes)
                    {
                        process.Dispose();
                    }
                }

                if (!string.IsNullOrWhiteSpace(executablePath))
                {
                    return executablePath;
                }
            }

            return null;
        }
    }
}
