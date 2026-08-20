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

        public RecentLogCollector(int capacity)
        {
            this.capacity = Mathf.Max(20, capacity);
            Application.logMessageReceivedThreaded += OnLog;
        }

        public string BuildText()
        {
            lock (entries)
            {
                var builder = new StringBuilder();
                foreach (var entry in entries)
                    builder.AppendLine(entry);
                return builder.ToString();
            }
        }

        public void Dispose() => Application.logMessageReceivedThreaded -= OnLog;

        private void OnLog(string condition, string stackTrace, LogType type)
        {
            var value = $"[{DateTime.UtcNow:O}] [{type}] {condition}";
            if ((type == LogType.Error || type == LogType.Exception || type == LogType.Assert) && !string.IsNullOrEmpty(stackTrace))
                value += "\n" + stackTrace;

            lock (entries)
            {
                entries.Enqueue(value);
                while (entries.Count > capacity)
                    entries.Dequeue();
            }
        }
    }
}
