using System.Collections.Generic;
using System.Diagnostics;

namespace Manimal.Terminal
{
    // per-subsystem stopwatch. Plugin.Update + TerminalCullingDriver.Update
    // wrap each subsystem call and Add(name, ticks). PerfWatch's spike log
    // grabs TopN() to name the slowest ones over the recent window, so we
    // can convict a specific system rather than guess. very cheap — one dict
    // lookup + one EMA per call.
    internal static class TerminalTickProfiler
    {
        private class Entry
        {
            public double EmaMs;    // ~1-second EMA of per-frame cost
            public double LastMs;   // most recent single-frame cost
        }

        private static readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(32);
        private static readonly double _tickToMs = 1000.0 / Stopwatch.Frequency;

        internal static void Add(string name, long elapsedTicks)
        {
            if (!_entries.TryGetValue(name, out var e))
            {
                e = new Entry();
                _entries[name] = e;
            }
            double ms = elapsedTicks * _tickToMs;
            e.LastMs = ms;
            // EMA at 0.05 => ~20-frame time constant (~333ms at 60fps, ~500ms at 45fps)
            e.EmaMs = e.EmaMs * 0.95 + ms * 0.05;
        }

        internal static void Reset()
        {
            _entries.Clear();
        }

        // returns "name=Xms/Yavg name2=..." for the top N by EMA. cheap-ish
        // (sorted only when called — spike-log path).
        internal static string TopN(int n)
        {
            if (_entries.Count == 0) return "";
            var list = new List<KeyValuePair<string, Entry>>(_entries);
            list.Sort((a, b) => b.Value.EmaMs.CompareTo(a.Value.EmaMs));
            var sb = new System.Text.StringBuilder();
            int take = System.Math.Min(n, list.Count);
            for (int i = 0; i < take; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(list[i].Key).Append('=').Append(list[i].Value.LastMs.ToString("F1")).Append("ms/").Append(list[i].Value.EmaMs.ToString("F1")).Append("avg");
            }
            return sb.ToString();
        }
    }
}
