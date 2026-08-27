using System;
using Comfort.Common;
using EFT.UI;
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
            // Do not fall back to an arbitrary group containing "Ambient". Some
            // third-party mixers expose similarly named groups that are not children
            // of Tarkov's AmbientOut bus and therefore ignore its volume slider.
            return FindGroup("AmbientOut");
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
            return FindGroup("CommonAmbOutEffects", "AmbientOut");
        }

        private static AudioMixerGroup FindGroup(params string[] names)
        {
            var groups = Resources.FindObjectsOfTypeAll<AudioMixerGroup>();
            foreach (string wanted in names)
                foreach (var group in groups)
                    if (group && string.Equals(group.name, wanted, StringComparison.OrdinalIgnoreCase))
                        return group;
            return null;
        }

        // Presentation audio (cinematics, epilogues, etc.) follows Tarkov's
        // overall volume only. Never silently fall back to Music: users commonly
        // lower that slider while still expecting authored video audio to play.
        internal static bool RouteMaster(AudioSource source)
        {
            if (!source) return false;

            AudioMixerGroup group = null;
            try
            {
                if (Singleton<GUISounds>.Instantiated)
                {
                    var mixer = Singleton<GUISounds>.Instance?.MasterMixer;
                    var groups = mixer?.FindMatchingGroups("Master");
                    if (groups != null && groups.Length > 0) group = groups[0];
                }
            }
            catch { }

            if (!group) group = FindGroup("Master");
            if (!group) return false;
            source.outputAudioMixerGroup = group;
            return true;
        }

        internal static bool IsMusicSource(AudioSource source)
        {
            if (!source || !source.outputAudioMixerGroup) return false;
            var assigned = source.outputAudioMixerGroup;
            if (assigned.name.IndexOf("Music", StringComparison.OrdinalIgnoreCase) >= 0) return true;

            try
            {
                if (!Singleton<GUISounds>.Instantiated) return false;
                var groups = Singleton<GUISounds>.Instance?.MasterMixer?.FindMatchingGroups("Music");
                if (groups == null) return false;
                foreach (var group in groups)
                    if (group == assigned) return true;
            }
            catch { }
            return false;
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
