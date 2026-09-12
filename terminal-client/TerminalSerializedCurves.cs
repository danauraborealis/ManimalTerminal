using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Manimal.Terminal
{
    internal static class TerminalSerializedCurves
    {
        [Serializable]
        private sealed class Container
        {
            public AnimationCurve curve = null;
        }

        // Unity's serialized infinity modes are NOT the public WrapMode enum.
        // In Unity 2022, serialized 2 means clamp, while WrapMode.Loop == 2.
        // Let the engine deserialize its own format, including weighted tangents.
        // The extractor omits version tags; supply the versions used by Unity 2022.
        internal static AnimationCurve FromUnity(JToken token)
        {
            if (!(token is JObject source) || !(source["m_Curve"] is JArray)) return null;
            var curve = (JObject)source.DeepClone();
            curve["serializedVersion"] = "2";
            foreach (var key in (JArray)curve["m_Curve"])
            {
                if (!(key is JObject row)) throw new JsonException("Curve key must be an object");
                row["serializedVersion"] = "3";
            }
            var json = new JObject { ["curve"] = curve }.ToString(Formatting.None);
            return JsonUtility.FromJson<Container>(json)?.curve;
        }

        // Donors were captured through the public API, so their pre/post values
        // already ARE WrapMode values. Keep that format distinct from asset data.
        internal static AnimationCurve FromDonor(JToken token)
        {
            if (!(token?["curve"] is JArray rows)) return null;
            var keys = new Keyframe[rows.Count];
            for (int i = 0; i < keys.Length; i++)
            {
                var row = rows[i];
                keys[i] = new Keyframe(row.Value<float>("t"), row.Value<float>("v"),
                    row.Value<float>("i"), row.Value<float>("o"))
                {
                    weightedMode = (WeightedMode)(row.Value<int?>("weightedMode") ?? 0),
                    inWeight = row.Value<float?>("inWeight") ?? 1f / 3f,
                    outWeight = row.Value<float?>("outWeight") ?? 1f / 3f,
                };
            }
            return new AnimationCurve(keys)
            {
                preWrapMode = (WrapMode)(token.Value<int?>("pre") ?? (int)WrapMode.ClampForever),
                postWrapMode = (WrapMode)(token.Value<int?>("post") ?? (int)WrapMode.ClampForever),
            };
        }

        internal static JObject ToDonor(AnimationCurve curve)
        {
            var keys = new JArray();
            foreach (var key in curve.keys)
                keys.Add(new JObject
                {
                    ["t"] = key.time, ["v"] = key.value,
                    ["i"] = key.inTangent, ["o"] = key.outTangent,
                    ["weightedMode"] = (int)key.weightedMode,
                    ["inWeight"] = key.inWeight, ["outWeight"] = key.outWeight,
                });
            return new JObject { ["curve"] = keys,
                ["pre"] = (int)curve.preWrapMode, ["post"] = (int)curve.postWrapMode };
        }
    }
}
