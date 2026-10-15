using System;
using System.Collections.Generic;

namespace Valheim_Workers
{
    internal static class BackgroundMonitorRuntime
    {
        private const int MaxEvents = 48;
        private static readonly Queue<string> Events = new Queue<string>();

        internal static void Note(string message, bool writeLog = true)
        {
            string text = (message ?? string.Empty).Trim();
            if (text.Length == 0) return;
            string line = $"{DateTime.Now:HH:mm:ss}  {text}";
            lock (Events)
            {
                Events.Enqueue(line);
                while (Events.Count > MaxEvents) Events.Dequeue();
            }
            if (writeLog) Plugin.Log?.LogInfo($"[Background] {text}");
        }

        internal static void GetRecent(List<string> destination, int max = 12)
        {
            if (destination == null) return;
            destination.Clear();
            string[] snapshot;
            lock (Events) snapshot = Events.ToArray();
            int start = Math.Max(0, snapshot.Length - Math.Max(1, max));
            for (int i = snapshot.Length - 1; i >= start; i--) destination.Add(snapshot[i]);
        }

        internal static void Clear()
        {
            lock (Events) Events.Clear();
        }
    }
}
