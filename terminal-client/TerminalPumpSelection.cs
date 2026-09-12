using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Manimal.Terminal
{
    // Pure selection/data logic, also exercised by the pump regression harness.
    internal static class TerminalPumpSelection
    {
        internal static string HumanKey(bool isAi, string nickname, string profileId, int playerId)
        {
            if (isAi || (nickname?.StartsWith("headless_", StringComparison.OrdinalIgnoreCase) ?? false)) return null;
            return string.IsNullOrEmpty(profileId) ? "player:" + playerId : profileId;
        }

        internal static JObject Configuration(JArray rows, int playerCount)
        {
            JArray configurations = null;
            foreach (var row in rows)
                if (row.Value<string>("cls") == "ElectricalCabinetSelector")
                {
                    configurations = row["fields"]?["_groupConfigurations"] as JArray;
                    break;
                }
            JObject best = null, largest = null;
            foreach (JObject cfg in configurations ?? new JArray())
            {
                int max = cfg.Value<int>("MaxGroupSize");
                if (largest == null || max > largest.Value<int>("MaxGroupSize")) largest = cfg;
                if (max >= Math.Max(1, playerCount) && (best == null || max < best.Value<int>("MaxGroupSize")))
                    best = cfg;
            }
            // Retail supports up to five; larger custom groups use that last bracket.
            return best ?? largest ?? throw new InvalidOperationException("Pump group configurations missing");
        }

        internal static List<string> Pool(JObject configuration)
        {
            var pool = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in (configuration["AvailableCabinets"] as JArray) ?? new JArray())
            {
                var path = entry["$ref"]?.Value<string>("path");
                if (string.IsNullOrEmpty(path)) throw new InvalidOperationException("Pump pool contains an empty cabinet reference");
                if (seen.Add(path)) pool.Add(path);
            }
            return pool;
        }

        internal static List<string> Choose(IReadOnlyList<string> pool, int count, Random random)
        {
            var remaining = new List<string>(pool);
            if (count < 1 || count > remaining.Count)
                throw new InvalidOperationException($"Cannot break {count} cabinets from a {remaining.Count}-cabinet pool");
            var chosen = new List<string>(count);
            for (int i = 0; i < count; i++)
            {
                int pick = random.Next(remaining.Count);
                chosen.Add(remaining[pick]);
                remaining.RemoveAt(pick);
            }
            return chosen;
        }

        internal static int RaidSeed(string raidKey)
        {
            // Stable across runtimes/peers, unlike string.GetHashCode(). Solo callers
            // supply a new GUID each raid; co-op callers supply the shared match key.
            unchecked
            {
                uint hash = 2166136261;
                foreach (char c in "TerminalPumpCabinets|" + raidKey) hash = (hash ^ c) * 16777619;
                return (int)hash;
            }
        }

        internal static Dictionary<string, bool> InitialStates(JArray rows, string cabinetPath, bool broken)
        {
            JObject controller = null;
            foreach (var row in rows)
                if (row.Value<string>("cls") == "ElectricalCabinetController" && row.Value<string>("path") == cabinetPath)
                    controller = row["fields"] as JObject;
            if (controller == null) throw new InvalidOperationException($"Pump controller missing: {cabinetPath}");
            string selectTrigger = controller.Value<string>(broken ? "_triggerSetActive" : "_triggerSetDisabled");
            string repairedTrigger = controller.Value<string>("_onCabinetFixedTriggerId");
            var states = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                var fields = row["fields"];
                if (fields == null) continue;
                if (row.Value<string>("cls") == "HandlerGameObjectState" && fields.Value<string>("_triggerId") == selectTrigger)
                    foreach (var target in (fields["_controlledGameObjects"] as JArray) ?? new JArray())
                        states[target["$ref"].Value<string>("path")] = fields.Value<int>("_targetState") != 0;

                // Restore the broken indicators and repair switch even if the scene
                // was reused after a prior repair. Do not rely on saved activeSelf.
                if (broken && row.Value<string>("cls") == "HandlerGOState")
                {
                    var target = fields["_target"]?["$ref"]?.Value<string>("path");
                    if (target == null) continue;
                    if (fields.Value<string>("_triggerIdEnable") == repairedTrigger) states[target] = false;
                    if (fields.Value<string>("_triggerIdDisable") == repairedTrigger) states[target] = true;
                }
            }
            states[cabinetPath] = broken;
            return states;
        }
    }
}
