using System;
using System.Collections.Generic;

namespace Manimal.Terminal.Fika
{
    // Kept free of Unity/network types so replay ordering and idempotence can be tested.
    internal sealed class TerminalStateLedger
    {
        internal sealed class Entry
        {
            internal long Revision;
            internal byte Kind;
            internal string Key, Actor;
            internal int Number;
            internal double Time;
        }

        private readonly Dictionary<string, Entry> _state = new Dictionary<string, Entry>(StringComparer.Ordinal);
        internal long Revision { get; private set; }
        private static string Id(byte kind, string key) => kind + ":" + (key ?? "");
        internal Entry Get(byte kind, string key = "") => _state.TryGetValue(Id(kind, key), out var e) ? e : null;
        internal Entry Put(byte kind, string key, string actor, int number, double time, bool replace = false)
        {
            string id = Id(kind, key);
            if (_state.ContainsKey(id) && !replace) return null;
            var entry = new Entry { Revision = ++Revision, Kind = kind, Key = key ?? "", Actor = actor ?? "", Number = number, Time = time };
            _state[id] = entry;
            return entry;
        }
        internal Entry[] Snapshot()
        {
            var entries = new List<Entry>(_state.Values);
            entries.Sort((a, b) => a.Revision.CompareTo(b.Revision));
            return entries.ToArray();
        }
        internal void Clear() { _state.Clear(); Revision = 0; }
    }

    internal sealed class TerminalAppliedState
    {
        private readonly Dictionary<string, long> _revisions = new Dictionary<string, long>(StringComparer.Ordinal);
        private static string Id(byte kind, string key) => kind + ":" + (key ?? "");
        internal bool Contains(byte kind, string key, long revision)
            => _revisions.TryGetValue(Id(kind, key), out long current) && current >= revision;
        internal void Mark(byte kind, string key, long revision)
        {
            if (!Contains(kind, key, revision)) _revisions[Id(kind, key)] = revision;
        }
        internal void Clear() => _revisions.Clear();
    }
}
