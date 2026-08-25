using System;
using System.Collections.Generic;
using UnityEngine;

namespace Manimal.Terminal
{
    // EFT clothing is not guaranteed to use only the common humanoid skeleton.
    // Some tops/pants carry weighted helper transforms below a normal spine/leg
    // bone. The cutscene actor does not own those helpers, so a name-only remap can
    // leave their vertices attached to an undriven transform and draw long spikes.
    // Preserve the actor's packed bones as the animated core and reproduce only the
    // source clothing's auxiliary chains beneath the nearest core bone.
    internal static class TerminalCutsceneClothing
    {
        internal static bool TryBind(
            SkinnedMeshRenderer source,
            SkinnedMeshRenderer destination,
            Transform actorRoot,
            string logLabel,
            out Transform[] mapped,
            out Transform mappedRoot,
            out int auxiliaryBones,
            out string failure)
        {
            mapped = null;
            mappedRoot = null;
            auxiliaryBones = 0;
            failure = null;
            if (!source || !destination || !actorRoot)
            {
                failure = "source, destination, or actor root missing";
                return false;
            }

            var sourceBones = source.bones;
            if (sourceBones == null || sourceBones.Length == 0)
            {
                failure = "source mesh has no bones";
                return false;
            }

            // The bones already used by the packed cutscene mesh are the safest
            // possible animation targets. They are the exact transforms its timeline
            // was authored against, and take priority over same-named dead armatures.
            var coreByName = new Dictionary<string, Transform>(StringComparer.Ordinal);
            if (destination.bones != null)
            {
                foreach (var bone in destination.bones)
                    if (bone && !coreByName.ContainsKey(bone.name)) coreByName.Add(bone.name, bone);
            }
            if (destination.rootBone && !coreByName.ContainsKey(destination.rootBone.name))
                coreByName.Add(destination.rootBone.name, destination.rootBone);

            // Root fallback only. We deliberately do not treat every transform below
            // Actor_Player as a core clothing bone: ripped cutscene scenes can contain
            // duplicate, non-animated armatures with the same names.
            var actorByName = new Dictionary<string, Transform>(StringComparer.Ordinal);
            foreach (var transform in actorRoot.GetComponentsInChildren<Transform>(true))
                if (!actorByName.ContainsKey(transform.name)) actorByName.Add(transform.name, transform);

            var sourceToActor = new Dictionary<Transform, Transform>();
            foreach (var sourceBone in sourceBones)
            {
                if (!sourceBone) continue;
                if (coreByName.TryGetValue(sourceBone.name, out var core))
                    sourceToActor[sourceBone] = core;
            }

            Transform sourceRoot = source.rootBone;
            Transform actorClothingRoot = null;
            if (sourceRoot)
            {
                if (!sourceToActor.TryGetValue(sourceRoot, out actorClothingRoot))
                {
                    if (!coreByName.TryGetValue(sourceRoot.name, out actorClothingRoot)
                        && !actorByName.TryGetValue(sourceRoot.name, out actorClothingRoot))
                    {
                        failure = $"root bone '{sourceRoot.name}' is absent from the animated actor";
                        return false;
                    }
                    sourceToActor[sourceRoot] = actorClothingRoot;
                }
            }
            else
            {
                actorClothingRoot = destination.rootBone ? destination.rootBone : actorRoot;
            }

            var cloned = new Dictionary<Transform, Transform>();
            int clonedCount = 0;
            Transform EnsureAuxiliary(Transform sourceTransform)
            {
                if (!sourceTransform) return null;
                if (sourceToActor.TryGetValue(sourceTransform, out var existing)) return existing;
                if (cloned.TryGetValue(sourceTransform, out existing)) return existing;
                // Unweighted intermediary transforms do not appear in source.bones,
                // but may still be a normal animated actor bone. Reuse the packed
                // counterpart before deciding this is a clothing-owned helper.
                if (coreByName.TryGetValue(sourceTransform.name, out existing))
                {
                    sourceToActor[sourceTransform] = existing;
                    return existing;
                }

                Transform targetParent;
                if (sourceRoot && sourceTransform.IsChildOf(sourceRoot))
                {
                    targetParent = sourceTransform.parent == sourceRoot
                        ? actorClothingRoot
                        : EnsureAuxiliary(sourceTransform.parent);
                }
                else
                {
                    // Unexpected detached helper. Anchor it to the clothing root and
                    // preserve its transform relative to that root where possible.
                    targetParent = actorClothingRoot;
                }
                if (!targetParent) return null;

                var go = new GameObject(sourceTransform.name + "__TerminalClothingBone");
                go.layer = destination.gameObject.layer;
                var target = go.transform;
                target.SetParent(targetParent, false);
                target.localPosition = sourceTransform.localPosition;
                target.localRotation = sourceTransform.localRotation;
                target.localScale = sourceTransform.localScale;
                cloned[sourceTransform] = target;
                sourceToActor[sourceTransform] = target;
                clonedCount++;
                return target;
            }

            mapped = new Transform[sourceBones.Length];
            var unresolved = new List<string>();
            for (int i = 0; i < sourceBones.Length; i++)
            {
                var sourceBone = sourceBones[i];
                if (!sourceBone)
                {
                    unresolved.Add("#" + i);
                    continue;
                }
                if (!sourceToActor.TryGetValue(sourceBone, out mapped[i]))
                    mapped[i] = EnsureAuxiliary(sourceBone);
                if (!mapped[i]) unresolved.Add(sourceBone.name);
            }

            if (unresolved.Count > 0)
            {
                foreach (var clone in cloned.Values)
                    if (clone) UnityEngine.Object.Destroy(clone.gameObject);
                mapped = null;
                failure = $"{unresolved.Count} bone(s) could not be anchored ({string.Join(", ", unresolved.GetRange(0, Math.Min(8, unresolved.Count))) + (unresolved.Count > 8 ? "..." : "")})";
                return false;
            }

            mappedRoot = actorClothingRoot;
            auxiliaryBones = clonedCount;
            Plugin.Log.LogDebug($"[{logLabel}] clothing bind remapped {sourceBones.Length} weighted bone(s) and carried {auxiliaryBones} auxiliary transform(s)");
            return true;
        }
    }
}
