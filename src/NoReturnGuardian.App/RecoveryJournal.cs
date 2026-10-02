using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace NoReturnGuardian
{
    /// <summary>
    /// 恢复脚本写下的一份 JSONL 日志，一次读完。凭据只认这里取出的字段：缺字段、类型不对都按“没有”算，
    /// 所以核对总是不成立而不会抛出别的异常；读不出或有坏行时，Read 抛 InvalidDataException。
    /// </summary>
    internal sealed class RecoveryJournal
    {
        private RecoveryJournal(List<Entry> entries)
        {
            Entries = entries;
        }

        public IReadOnlyList<Entry> Entries { get; private set; }

        /// <summary>日志读不出来：文件没有、被占用、没有权限，或者有坏行。</summary>
        public static bool Unreadable(Exception error)
        {
            return error is IOException || error is InvalidDataException || error is UnauthorizedAccessException;
        }

        public static RecoveryJournal Read(string path)
        {
            var serializer = new JavaScriptSerializer();
            var entries = new List<Entry>();
            foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
            {
                if (line.Length == 0)
                {
                    continue;
                }

                Dictionary<string, object> fields;
                try
                {
                    fields = serializer.Deserialize<Dictionary<string, object>>(line);
                }
                catch (ArgumentException error)
                {
                    throw new InvalidDataException("Malformed journal line in " + Path.GetFileName(path), error);
                }
                catch (InvalidOperationException error)
                {
                    throw new InvalidDataException("Malformed journal line in " + Path.GetFileName(path), error);
                }

                entries.Add(new Entry(fields ?? new Dictionary<string, object>()));
            }

            return new RecoveryJournal(entries);
        }

        public IEnumerable<Entry> OfKind(string kind)
        {
            return Entries.Where(entry => entry.Kind == kind);
        }

        public int Count(string kind)
        {
            return OfKind(kind).Count();
        }

        public bool Has(string kind)
        {
            return OfKind(kind).Any();
        }

        /// <summary>每一行都来自这个游戏进程（同一 PID 和创建时间）。</summary>
        public bool AllFrom(int pid, long birth)
        {
            return Entries.Count > 0 && Entries.All(entry => entry.From(pid, birth));
        }

        internal sealed class Entry
        {
            private readonly Dictionary<string, object> _fields;

            public Entry(Dictionary<string, object> fields)
            {
                _fields = fields;
            }

            public string Kind
            {
                get { return Text("kind"); }
            }

            public string Text(string name)
            {
                object value;
                return _fields.TryGetValue(name, out value) ? value as string : null;
            }

            public bool? Flag(string name)
            {
                object value;
                return _fields.TryGetValue(name, out value) && value is bool ? (bool?)value : null;
            }

            public bool IsTrue(string name)
            {
                return Flag(name) == true;
            }

            public long? Number(string name)
            {
                object value;
                if (!_fields.TryGetValue(name, out value))
                {
                    return null;
                }

                if (value is int)
                {
                    return (int)value;
                }

                if (value is long)
                {
                    return (long)value;
                }

                long parsed;
                // 进程创建时间超出 JavaScript 的安全整数，原生脚本把它写成字符串。
                return value is string && long.TryParse((string)value, out parsed) ? parsed : (long?)null;
            }

            public bool Has(string name)
            {
                return _fields.ContainsKey(name);
            }

            public bool From(int pid, long birth)
            {
                return Number("pid") == pid && Number("birth") == birth;
            }
        }
    }
}
