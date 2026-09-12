#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using EFT.Interactive;
using Newtonsoft.Json;

namespace Manimal.Terminal
{
    // The bundle contains some formerly substituted template IDs. Bind by the
    // same exact container ID used by the server before attaching its loot.
    internal static class TerminalContainerTemplates
    {
        private static Dictionary<string, string>? _templates;

        internal static bool Apply(LootableContainer? container)
        {
            if (container == null || string.IsNullOrEmpty(container.Id)) return false;
            if (_templates == null)
            {
                string path = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".",
                    "plugin-data", "terminal_container_templates.json");
                try
                {
                    _templates = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(path))
                        ?? throw new InvalidDataException("Empty container template map");
                }
                catch (Exception e)
                {
                    _templates = new Dictionary<string, string>();
                    Plugin.Log.LogError($"[Containers] template map could not load: {e.Message}");
                }
            }
            if (!_templates.TryGetValue(container.Id, out var template) || container.Template == template) return false;
            container.Template = template;
            return true;
        }

        internal static void ApplyLoadedContainers()
        {
            int bound = 0;
            foreach (var container in UnityEngine.Object.FindObjectsOfType<LootableContainer>(true))
                if (Apply(container)) bound++;
            if (bound > 0) Plugin.Log.LogInfo($"[Containers] {bound} container template(s) restored by exact server ID");
        }
    }
}
