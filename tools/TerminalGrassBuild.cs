using System.IO;
using UnityEditor;
using UnityEngine;

// Install under Assets/IcebreakerTools/Editor in WTT-SDK-2022. The source folder
// is deliberately separate from Terminal_Import: this produces a ~grass-only
// payload and never rebuilds the multi-gigabyte Terminal scene bundle.
public static class TerminalGrassBuild
{
    const string SourcePrefab = "Assets/IcebreakerTools/TerminalGrassSource/crossGrass.prefab";
    const string CleanDir = "Assets/IcebreakerTools/TerminalGrassBuild";
    const string CleanPrefab = CleanDir + "/crossGrass_clean.prefab";
    const string BundleName = "terminal_grass.bundle";

    static readonly string[] Installs = { @"D:\SPTDev", @"D:\SPTDevFika" };

    [MenuItem("Terminal/Build Grass Bundle")]
    public static void Build()
    {
        GameObject source = AssetDatabase.LoadAssetAtPath<GameObject>(SourcePrefab);
        if (source == null)
        {
            Debug.LogError($"[TerminalGrassBuild] source prefab missing: {SourcePrefab}");
            return;
        }

        if (!AssetDatabase.IsValidFolder(CleanDir))
        {
            if (!AssetDatabase.IsValidFolder("Assets/IcebreakerTools"))
                AssetDatabase.CreateFolder("Assets", "IcebreakerTools");
            AssetDatabase.CreateFolder("Assets/IcebreakerTools", "TerminalGrassBuild");
        }

        GameObject instance = PrefabUtility.InstantiatePrefab(source) as GameObject;
        if (instance == null)
        {
            Debug.LogError("[TerminalGrassBuild] could not instantiate the source prefab");
            return;
        }

        try
        {
            instance.name = "crossGrass_clean";
            instance.transform.position = Vector3.zero;
            instance.transform.rotation = Quaternion.identity;
            instance.transform.localScale = Vector3.one;
            int stripped = StripAuthoringScripts(instance);
            PrefabUtility.SaveAsPrefabAsset(instance, CleanPrefab, out bool saved);
            if (!saved)
            {
                Debug.LogError($"[TerminalGrassBuild] clean prefab save failed: {CleanPrefab}");
                return;
            }
            Debug.Log($"[TerminalGrassBuild] clean prefab ready; stripped {stripped} unresolved component(s)");
        }
        finally
        {
            Object.DestroyImmediate(instance);
        }

        string outputDir = Path.Combine(Path.GetDirectoryName(Application.dataPath), "TerminalBundles");
        Directory.CreateDirectory(outputDir);
        var build = new AssetBundleBuild
        {
            assetBundleName = BundleName,
            assetNames = new[] { CleanPrefab }
        };
        AssetBundleManifest manifest = BuildPipeline.BuildAssetBundles(
            outputDir, new[] { build },
            BuildAssetBundleOptions.ChunkBasedCompression,
            BuildTarget.StandaloneWindows64);
        if (manifest == null)
        {
            Debug.LogError("[TerminalGrassBuild] bundle build FAILED");
            return;
        }

        string builtFile = Path.Combine(outputDir, BundleName);
        Debug.Log($"[TerminalGrassBuild] built {new FileInfo(builtFile).Length / 1024f / 1024f:F1}MB -> {builtFile}");
        foreach (string install in Installs)
        {
            if (!Directory.Exists(install)) continue;
            string destination = Path.Combine(install, "BepInEx", "plugins", "ManimalTerminal", BundleName);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                File.Copy(builtFile, destination, true);
                Debug.Log($"[TerminalGrassBuild] deployed -> {destination}");
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[TerminalGrassBuild] deploy failed -> {destination}: {e.Message}");
            }
        }
    }

    private static int StripAuthoringScripts(GameObject root)
    {
        int count = GameObjectUtility.RemoveMonoBehavioursWithMissingScript(root);
        // AssetRipper may have generated compilable fieldless stand-ins instead of
        // genuinely missing scripts. The runtime deliberately adds SPT's native
        // GPUInstancerPrefab after loading, so no MonoBehaviour belongs in this
        // mesh/material-only payload.
        MonoBehaviour[] scripts = root.GetComponents<MonoBehaviour>();
        for (int i = 0; i < scripts.Length; i++)
        {
            if (scripts[i] == null) continue;
            Object.DestroyImmediate(scripts[i], true);
            count++;
        }
        for (int i = 0; i < root.transform.childCount; i++)
            count += StripAuthoringScripts(root.transform.GetChild(i).gameObject);
        return count;
    }
}
