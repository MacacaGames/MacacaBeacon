using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace MacacaGames.RuntimeBugReporter
{
    internal sealed class RecentLogCollector : IDisposable
    {
        private readonly Queue<string> entries = new Queue<string>();
        private readonly int capacity;
        private readonly Queue<string> normalEntries = new Queue<string>();
        private readonly Queue<string> warningEntries = new Queue<string>();
        private readonly Queue<string> errorEntries = new Queue<string>();
        private readonly int normalCapacity;
        private readonly int warningCapacity;
        private readonly int errorCapacity;

        public RecentLogCollector(int capacity, int normalCapacity = 100, int warningCapacity = 50, int errorCapacity = 50)
        {
            this.capacity = Mathf.Max(20, capacity);
            this.normalCapacity = Mathf.Max(0, normalCapacity);
            this.warningCapacity = Mathf.Max(0, warningCapacity);
            this.errorCapacity = Mathf.Max(0, errorCapacity);
            Application.logMessageReceivedThreaded += OnLog;
        }

        public string BuildText()
        {
            lock (entries)
            {
                return FormatEntries(entries);
            }
        }

        public void Dispose() => Application.logMessageReceivedThreaded -= OnLog;

        // All log sections share one cutoff, including when saved as a draft.
        internal Snapshot CaptureSnapshot()
        {
            lock (entries)
                return new Snapshot
                {
                    All = FormatEntries(entries),
                    Normal = normalCapacity > 0 ? FormatEntries(normalEntries, true) : null,
                    Warning = warningCapacity > 0 ? FormatEntries(warningEntries, true) : null,
                    Error = errorCapacity > 0 ? FormatEntries(errorEntries, true) : null
                };
        }

        internal sealed class Snapshot
        {
            public string All;
            public string Normal;
            public string Warning;
            public string Error;
        }

        private static string FormatEntries(Queue<string> source, bool describeEmpty = false)
        {
            if (describeEmpty && source.Count == 0) return "No entries\n";
            var builder = new StringBuilder();
            foreach (var entry in source) builder.AppendLine(entry);
            return builder.ToString();
        }

        private static void Enqueue(Queue<string> target, int limit, string value)
        {
            if (limit == 0) return;
            target.Enqueue(value);
            while (target.Count > limit) target.Dequeue();
        }

        private void OnLog(string condition, string stackTrace, LogType type)
        {
            var value = $"[{DateTime.UtcNow:O}] [{type}] {condition}";
            if ((type == LogType.Error || type == LogType.Exception || type == LogType.Assert) && !string.IsNullOrEmpty(stackTrace))
                value += "\n" + stackTrace;

            lock (entries)
            {
                Enqueue(entries, capacity, value);
                switch (type)
                {
                    case LogType.Log:
                        Enqueue(normalEntries, normalCapacity, value);
                        break;
                    case LogType.Warning:
                        Enqueue(warningEntries, warningCapacity, value);
                        break;
                    case LogType.Error:
                    case LogType.Exception:
                    case LogType.Assert:
                        Enqueue(errorEntries, errorCapacity, value);
                        break;
                }
            }
        }
    }
}
