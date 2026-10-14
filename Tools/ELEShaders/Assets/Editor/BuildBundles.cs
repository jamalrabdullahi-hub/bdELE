using System.IO;
using UnityEditor;

// Builds the ELE shader bundle for KSP 1.12 (Windows, DX11).
// Headless: Unity.exe -batchmode -quit -projectPath <this project> -executeMethod BuildBundles.Build -logFile build.log
public static class BuildBundles
{
    const string Out = "Bundles";

    [MenuItem("ELE/Build shader bundle")]
    public static void Build()
    {
        Directory.CreateDirectory(Out);
        var build = new AssetBundleBuild
        {
            assetBundleName = "eleshaders.ksp",
            assetNames = new[] { "Assets/Shaders/ELEVolumetric.shader", "Assets/Shaders/ELEDistort.shader", "Assets/Shaders/ELEThermal.shader", "Assets/Shaders/ELEThermalBlit.shader" }
        };
        BuildPipeline.BuildAssetBundles(Out, new[] { build }, BuildAssetBundleOptions.ChunkBasedCompression, BuildTarget.StandaloneWindows64);
    }
}
