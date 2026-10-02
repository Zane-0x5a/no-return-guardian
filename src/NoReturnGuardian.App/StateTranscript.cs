using NoReturnGuardian.Core;
using System;
using System.IO;
using System.Text;

namespace NoReturnGuardian
{
    internal sealed class StateTranscript
    {
        private const long Limit = 2 * 1024 * 1024;
        private readonly string _path;
        private string _lastSignature;

        public StateTranscript(string path)
        {
            _path = path;
        }

        public void Record(MonitorStatus status)
        {
            if (status == null || string.IsNullOrWhiteSpace(_path))
            {
                return;
            }

            string signature = string.Join("|", new[]
            {
                status.GameRunning ? "1" : "0",
                status.WorkingRunStateSignal ?? string.Empty,
                status.WorkingRunStateCode ?? string.Empty,
                status.RunStateComplete ? "1" : "0",
                status.ManualCaptureReady ? "1" : "0",
                status.ManualCaptureStatus ?? string.Empty
            });
            if (string.Equals(signature, _lastSignature, StringComparison.Ordinal))
            {
                return;
            }

            try
            {
                string directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // 只留有限的量：到上限就换成 .1，从带表头的新文件重新写。
                LogRetention.Rotate(_path, Limit);
                if (!File.Exists(_path))
                {
                    File.AppendAllText(
                        _path,
                        "utc\tgame_running\tstate_signal\tworking_code\tstate_word\tcounter"
                        + "\tcomplete\tmanual_capture_ready\tmanual_capture_status\r\n",
                        new UTF8Encoding(false));
                }

                File.AppendAllText(
                    _path,
                    DateTime.UtcNow.ToString("O")
                    + "\t" + (status.GameRunning ? "1" : "0")
                    + "\t" + Safe(status.WorkingRunStateSignal)
                    + "\t" + Safe(status.WorkingRunStateCode)
                    + "\t" + Safe(status.WorkingRunStateWord)
                    + "\t" + status.WorkingRunCounter
                    + "\t" + (status.RunStateComplete ? "1" : "0")
                    + "\t" + (status.ManualCaptureReady ? "1" : "0")
                    + "\t" + Safe(status.ManualCaptureStatus)
                    + "\r\n",
                    new UTF8Encoding(false));
                _lastSignature = signature;
            }
            catch
            {
                // Evidence logging must never disrupt monitoring or save protection.
            }
        }

        private static string Safe(string value)
        {
            return (value ?? string.Empty).Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        }
    }
}
