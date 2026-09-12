using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Manimal.Terminal
{
    internal static class TerminalShoreAuthoring
    {
        internal static HashSet<long> ConfiguredPlayerIds(JObject sound)
        {
            var players = new HashSet<long>();
            if (!(sound?["AmbientSoundPlayerGroup"] is JArray groups)) return players;
            foreach (var group in groups)
            {
                var path = group.Value<string>("go") ?? "";
                if (!path.EndsWith("/AmbientSplineEmitterSeaGroup", StringComparison.Ordinal)) continue;
                if (!(group["fields"]?["_soundPlayers"] is JArray members)) continue;
                foreach (var member in members)
                {
                    long id = member.Value<long?>("ref") ?? 0;
                    if (id != 0) players.Add(id);
                }
            }
            return players;
        }
    }
}
