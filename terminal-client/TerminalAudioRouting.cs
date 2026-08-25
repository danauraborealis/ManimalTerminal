using System;
using UnityEngine;
using UnityEngine.Audio;

namespace Manimal.Terminal
{
    // All raw AudioSources created/restored by Terminal must enter Tarkov's Master
    // mixer. An AudioSource with outputAudioMixerGroup == null goes straight to the
    // listener and ignores the game's exposed in-game/ambient volume parameters.
    internal static class TerminalAudioRouting
    {
        internal static AudioMixerGroup AmbientBed()
        {
            try
            {
                if (MonoBehaviourSingleton<BetterAudio>.Instantiated)
                {
                    var audio = MonoBehaviourSingleton<BetterAudio>.Instance;
                    if (audio != null && audio.AmbientOutMixer != null) return audio.AmbientOutMixer;
                }
            }
            catch { }
            return FindGroup("AmbientOut", "Ambient");
        }

        internal static AudioMixerGroup AmbientEffects()
        {
            try
            {
                if (MonoBehaviourSingleton<BetterAudio>.Instantiated)
                {
                    var audio = MonoBehaviourSingleton<BetterAudio>.Instance;
                    if (audio != null && audio.CommonAmbientOutEffectsMixer != null)
                        return audio.CommonAmbientOutEffectsMixer;
                    if (audio != null && audio.AmbientOutMixer != null) return audio.AmbientOutMixer;
                }
            }
            catch { }
            return FindGroup("CommonAmbOutEffects", "AmbientOut", "Ambient");
        }

        private static AudioMixerGroup FindGroup(params string[] names)
        {
            var groups = Resources.FindObjectsOfTypeAll<AudioMixerGroup>();
            foreach (string wanted in names)
                foreach (var group in groups)
                    if (group && string.Equals(group.name, wanted, StringComparison.OrdinalIgnoreCase))
                        return group;
            foreach (string wanted in names)
                foreach (var group in groups)
                    if (group && group.name.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0)
                        return group;
            return null;
        }

        internal static bool Route(AudioSource source, bool effects = false)
        {
            if (!source) return false;
            var group = effects ? AmbientEffects() : AmbientBed();
            if (!group) return false;
            source.outputAudioMixerGroup = group;
            return true;
        }

        internal static int RouteTree(GameObject root, bool effects = false)
        {
            if (!root) return 0;
            int routed = 0;
            foreach (var source in root.GetComponentsInChildren<AudioSource>(true))
                if (Route(source, effects)) routed++;
            return routed;
        }
    }
}
