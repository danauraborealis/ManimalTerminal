using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Manimal.Terminal
{
    // SERVES OUR BUNDLES STRAIGHT OUT OF THE PLUGIN FOLDER — nothing is ever written
    // into EscapeFromTarkov_Data. 1:1 port of IcebreakerBundleHost.
    //
    // how it works, from EFT.AssetsManager.BundlesManager.BundleLoadOperation.smethod_0: the loaded-bundle
    // dictionary is checked FIRST, and only a miss falls through to a URL fetch (the
    // route that 404'd on the SPT manifest attempt). so we load the file ourselves
    // with AssetBundle.LoadFromFile (memory-mapped — a multi-GB bundle costs a
    // fraction of copying it) and insert it under the key the game is about to ask
    // for. the scene itself then loads BY NAME (SceneManager.LoadSceneAsync), which
    // works for any loaded bundle — no manifest entry needed.
    internal static class TerminalBundleHost
    {
        // payload root: BepInEx/plugins/ManimalTerminal/streamingassets/Windows/<key>
        private const string PayloadDir = "streamingassets";
        private const string Platform = "Windows";

        private static Dictionary<string, string> _ours;   // bundle key -> absolute file path
        // loaded once, but registered under EVERY spelling the game asks for:
        // LoadAssetAsync lowercases the name while LoadScene passes it through raw
        private static readonly Dictionary<string, AssetBundle> _loaded =
            new Dictionary<string, AssetBundle>(StringComparer.OrdinalIgnoreCase);

        private static Type _entryType;
        private static FieldInfo _loadedDict;

        internal static void Init(Harmony harmony)
        {
            try
            {
                _ours = ScanPayload();
                if (_ours.Count == 0)
                {
                    Plugin.Log.LogDebug($"[Bundles] no payload under {PayloadDir}/{Platform} — map bundles unavailable");
                    return;
                }

                _entryType = AccessTools.Inner(typeof(EFT.AssetsManager.BundlesManager), "AssetBundleReference");
                _loadedDict = AccessTools.Field(typeof(EFT.AssetsManager.BundlesManager), "_bundles");
                if (_entryType == null || _loadedDict == null)
                {
                    Plugin.Log.LogError("[Bundles] EFT.AssetsManager.BundlesManager layout changed — cannot host bundles from the plugin folder");
                    return;
                }

                harmony.Patch(
                    AccessTools.Method(typeof(EFT.AssetsManager.BundlesManager), nameof(EFT.AssetsManager.BundlesManager.LoadBundleAsync)),
                    prefix: new HarmonyMethod(typeof(TerminalBundleHost), nameof(BeforeLoadBundle)));

                Plugin.Log.LogInfo($"[Bundles] hosting {_ours.Count} bundle(s) from the plugin folder: " +
                                   string.Join(", ", new List<string>(_ours.Keys).ToArray()));
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Bundles] host init failed: {e}");
            }
        }

        // every file under streamingassets/Windows becomes a key by its relative path —
        // lowercase with forward slashes, the form the game asks for
        private static Dictionary<string, string> ScanPayload()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var root = Path.Combine(
                Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".",
                PayloadDir, Platform);
            if (!Directory.Exists(root)) return map;
            foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                if (file.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase)) continue;
                var key = file.Substring(root.Length + 1).Replace('\\', '/').ToLowerInvariant();
                map[key] = file;
            }
            return map;
        }

        // the cache entry decompiles as a public .ctor(AssetBundle) but the real IL may
        // not present that signature — bind explicitly, else build without a ctor and
        // write fields by TYPE (AssetBundle + the int refcount)
        private static object CreateEntry(AssetBundle bundle)
        {
            try
            {
                var ctor = AccessTools.Constructor(_entryType, new[] { typeof(AssetBundle) });
                if (ctor != null) return ctor.Invoke(new object[] { bundle });
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Bundles] entry ctor failed ({e.Message}) — falling back to field init"); }

            try
            {
                var inst = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(_entryType);
                foreach (var f in _entryType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (f.FieldType == typeof(AssetBundle)) f.SetValue(inst, bundle);
                    else if (f.FieldType == typeof(int)) f.SetValue(inst, 1);   // refcount starts at 1
                }
                return inst;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Bundles] could not build a cache entry: {e.Message}");
                return null;
            }
        }

        private static void BeforeLoadBundle(EFT.AssetsManager.BundlesManager __instance, string bundleName)
        {
            try
            {
                if (string.IsNullOrEmpty(bundleName)) return;
                var key = bundleName.Replace('\\', '/').ToLowerInvariant();
                if (!_ours.TryGetValue(key, out var file)) return;

                var dict = _loadedDict.GetValue(__instance) as System.Collections.IDictionary;
                if (dict == null) return;
                if (dict.Contains(bundleName)) return;      // this exact spelling is served

                if (!_loaded.TryGetValue(key, out var bundle) || bundle == null)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    bundle = AssetBundle.LoadFromFile(file);
                    if (bundle == null)
                    {
                        Plugin.Log.LogError($"[Bundles] '{key}' failed to load from {file}");
                        return;
                    }
                    _loaded[key] = bundle;
                    Plugin.Log.LogDebug($"[Bundles] loaded '{key}' from the plugin folder in {sw.ElapsedMilliseconds}ms");
                }
                var entry = CreateEntry(bundle);
                if (entry == null) return;
                dict[bundleName] = entry;
            }
            catch (Exception e)
            {
                // fall through to the game's own loader rather than break the raid
                Plugin.Log.LogError($"[Bundles] serve failed for '{bundleName}': {e.Message}");
            }
        }
    }
}
