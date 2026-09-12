using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using EFT;
using EFT.Interactive;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using SPT.Reflection.Patching;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

namespace Manimal.Terminal
{
    // The imported components are present, but their serialized behavior data
    // was lost. Keep the native triggers and trampler; refill their authored data.
    internal sealed class TerminalVegetation : MonoBehaviour
    {
        private static TerminalVegetation _instance;
        private static JObject _data;
        private SoundBank _bank;
        private readonly List<AudioClip> _clips = new List<AudioClip>();
        private readonly List<TreeInteractive> _repaired = new List<TreeInteractive>();

        private static string DataDirectory => Path.Combine(
            Path.GetDirectoryName(typeof(Plugin).Assembly.Location), "plugin-data");

        private static JObject Data
        {
            get
            {
                if (_data != null) return _data;
                var data = JObject.Parse(File.ReadAllText(Path.Combine(DataDirectory, "terminal_vegetation.json")));
                if (data.Value<int>("Version") != 1 || !(data["GrassTrampler"] is JObject)
                    || !(data["BushBank"] is JObject) || !(data["BushClips"] is JArray clips) || clips.Count == 0)
                    throw new InvalidDataException("Invalid Terminal vegetation data");
                _data = data;
                return data;
            }
        }

        private static bool Owns(Scene scene) => scene.IsValid()
            && scene.name.StartsWith("Terminal", StringComparison.OrdinalIgnoreCase);

        internal sealed class TramplerAwakePatch : ModulePatch
        {
            protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(GrassTrampler), nameof(GrassTrampler.Awake));

            [PatchPrefix]
            private static void Prefix(GrassTrampler __instance)
            {
                if (!Owns(__instance.gameObject.scene)) return;
                try { RestoreTrampler(__instance); }
                catch (Exception e) { Plugin.Log.LogError($"[Vegetation] trampler restoration failed: {e}"); }
            }
        }

        internal sealed class RaidStartedPatch : ModulePatch
        {
            protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(GameWorld), nameof(GameWorld.OnGameStarted));

            [PatchPostfix]
            private static void Postfix(GameWorld __instance)
            {
                if (!TerminalGate.On || _instance != null) return;
                var host = new GameObject("Manimal_TerminalVegetation");
                host.transform.SetParent(__instance.transform, false);
                _instance = host.AddComponent<TerminalVegetation>();
            }
        }

        // Call before Awake, which caches the standing values and the curve.
        internal static void RestoreTrampler(GrassTrampler trampler)
        {
            if (trampler._angleCurve != null && trampler._angleCurve.length > 0
                && trampler.TransitionTimeBetweenStates > 0f
                && trampler.StandGrassValues.ReturnTime > 0f
                && trampler.DuckGrassValues.ReturnTime > 0f
                && trampler.ProneGrassValues.ReturnTime > 0f) return;
            JObject fields = (JObject)Data["GrassTrampler"];
            JsonUtility.FromJsonOverwrite(fields.ToString(), trampler);
            trampler._angleCurve = TerminalSerializedCurves.FromUnity(fields["_angleCurve"]);
            if (trampler._angleCurve == null || trampler._angleCurve.length == 0)
                throw new InvalidDataException("Terminal grass bending curve is empty");
            Plugin.Log.LogInfo("[Vegetation] restored authored grass bending for standing, crouching and prone movement");
        }

        private IEnumerator Start()
        {
            // Native bank data comes from this SPT client's TreeInteractive bank.
            // Loose WAVs make the repair independent of which donor maps were loaded.
            JObject data;
            try { data = Data; }
            catch (Exception e) { Plugin.Log.LogError($"[Vegetation] {e}"); yield break; }
            foreach (JToken entry in (JArray)data["BushClips"])
            {
                string name = (string)entry;
                if (string.IsNullOrEmpty(name) || Path.GetFileName(name) != name)
                {
                    Plugin.Log.LogError("[Vegetation] invalid bush clip filename");
                    yield break;
                }
                string path = Path.Combine(DataDirectory, "vegetation", name);
                if (!File.Exists(path))
                {
                    Plugin.Log.LogError($"[Vegetation] missing bush clip: {path}");
                    yield break;
                }
                using (var request = UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri, AudioType.WAV))
                {
                    yield return request.SendWebRequest();
                    if (request.result != UnityWebRequest.Result.Success)
                    {
                        Plugin.Log.LogError($"[Vegetation] bush clip load failed: {name}: {request.error}");
                        yield break;
                    }
                    AudioClip clip = DownloadHandlerAudioClip.GetContent(request);
                    if (clip == null || clip.samples == 0)
                    {
                        Plugin.Log.LogError($"[Vegetation] bush clip is empty: {name}");
                        yield break;
                    }
                    clip.name = Path.GetFileNameWithoutExtension(name);
                    _clips.Add(clip);
                }
            }

            _bank = ScriptableObject.CreateInstance<SoundBank>();
            JsonUtility.FromJsonOverwrite(data["BushBank"].ToString(), _bank);
            _bank.name = "TreeInteractive_TerminalRuntime";
            _bank.Environments = new[] { new EnvironmentVariety(), new EnvironmentVariety() };
            _bank.Environments[0].Clips[0].Clips = _clips.ToArray();
            // HasEnvironment=false and empty middle/far groups are the native bank.
            // SoundBank.PickClipsByDistance uses the close group without BlendOptions.
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded && Owns(scene)) RestoreBushes(scene);
                yield return null;
            }
            SceneManager.sceneLoaded += OnSceneLoaded;
            Plugin.Log.LogInfo($"[Vegetation] restored {_repaired.Count} bush sound banks with {_clips.Count} native clips; native rustle and AI tree callbacks active");
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (Owns(scene)) RestoreBushes(scene);
        }

        private void RestoreBushes(Scene scene)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
                foreach (TreeInteractive tree in root.GetComponentsInChildren<TreeInteractive>(true))
                {
                    if (HasPlayableBank(tree._soundBank)) continue;
                    tree._soundBank = _bank;
                    _repaired.Add(tree);
                }
        }

        internal static bool HasPlayableBank(SoundBank bank)
        {
            if (bank == null || bank.Rolloff <= 0f || bank.Environments == null
                || bank.Environments.Length == 0) return false;
            var environment = bank.Environments[0];
            if (environment == null || environment.Clips == null || environment.Clips.Length == 0) return false;
            var distance = environment.Clips[0];
            if (distance == null || distance.Clips == null || distance.Clips.Length == 0) return false;
            for (int i = 0; i < distance.Clips.Length; i++)
                if (distance.Clips[i] == null) return false;
            return true;
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            for (int i = 0; i < _repaired.Count; i++)
                if (_repaired[i] != null && _repaired[i]._soundBank == _bank) _repaired[i]._soundBank = null;
            if (_bank != null) Destroy(_bank);
            for (int i = 0; i < _clips.Count; i++) if (_clips[i] != null) Destroy(_clips[i]);
            if (_instance == this) _instance = null;
        }
    }
}
