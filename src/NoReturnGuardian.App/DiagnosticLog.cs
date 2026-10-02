using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace NoReturnGuardian
{
    /// <summary>
    /// 守护器自己的诊断记录（guardian.log）。界面只给玩家看结论；原始原因、错误码和异常写在这里，
    /// 懂技术的玩家提交问题时可以附上。文件到上限就轮换成 .1，只留一份旧的，总量有界。
    /// </summary>
    internal static class DiagnosticLog
    {
        private const long Limit = 512 * 1024;
        private static readonly object Gate = new object();
        private static string _path;

        public static void Initialize(string directory)
        {
            _path = Path.Combine(directory, "guardian.log");
        }

        public static void Write(string area, string message)
        {
            if (_path == null || string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            string text = message.Trim().Replace("\r\n", "\n").Replace("\n", Environment.NewLine + "    ");
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path));
                    LogRetention.Rotate(_path, Limit);
                    File.AppendAllText(_path, DateTime.UtcNow.ToString("O") + "  " + area + "  " + text + Environment.NewLine,
                        new UTF8Encoding(false));
                }
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                // 记录不下来也不能打断守护。
            }
        }

        public static void Write(string area, Exception error)
        {
            if (error != null)
            {
                Write(area, error.GetType().Name + ": " + error.Message);
            }
        }
    }

    /// <summary>诊断记录只留有限的量：单个文件到上限就轮换，原生恢复和出发记录的日志只留最近几次。</summary>
    internal static class LogRetention
    {
        /// <summary>每类日志保留的次数：原生恢复（recovery-*，含它的各个分日志）和出发记录（departure-*）。</summary>
        public const int KeptSessions = 20;

        /// <summary>文件不小于 limit 时改名为 path.1（替换上一份旧的），之后从空文件重新写。</summary>
        public static void Rotate(string path, long limit)
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < limit)
            {
                return;
            }

            string previous = path + ".1";
            File.Delete(previous);
            File.Move(path, previous);
        }

        /// <summary>
        /// 原生恢复日志目录里每类只留最近 KeptSessions 次。一次的所有文件同名前缀（recovery-&lt;id&gt;.*），按最后写入排先后；
        /// 正被占用的文件（还在运行的记录器）删不掉就留着，下次再说。
        /// </summary>
        public static void PruneSessions(string directory, int kept = KeptSessions)
        {
            FileInfo[] files;
            try
            {
                files = new DirectoryInfo(directory).GetFiles();
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                return;
            }

            IEnumerable<IGrouping<string, FileInfo>> sessions = files
                .Where(file => file.Name.StartsWith("recovery-", StringComparison.OrdinalIgnoreCase)
                    || file.Name.StartsWith("departure-", StringComparison.OrdinalIgnoreCase))
                .GroupBy(file => file.Name.Substring(0, file.Name.IndexOf('.') < 0 ? file.Name.Length : file.Name.IndexOf('.')),
                    StringComparer.OrdinalIgnoreCase);
            foreach (var kind in sessions.GroupBy(session => session.Key.Substring(0, session.Key.IndexOf('-'))))
            {
                foreach (var session in kind.OrderByDescending(item => item.Max(file => file.LastWriteTimeUtc)).Skip(kept))
                {
                    foreach (FileInfo file in session)
                    {
                        try
                        {
                            file.Delete();
                        }
                        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                        {
                        }
                    }
                }
            }
        }
    }
}
