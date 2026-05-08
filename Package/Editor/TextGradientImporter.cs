using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.Text;

namespace ODDGames.Html2Uxml.Editor
{
    /// Materializes html2uxml-emitted text-gradient sidecars into TextCore
    /// TextColorGradient assets. The converter writes
    /// "<name>.h2utg.json" with mode + 4 corner colors; this postprocessor
    /// creates or updates a sibling "<name>.asset" the rich-text tag
    /// <gradient="<name>"> resolves at runtime.
    public class TextGradientImporter : AssetPostprocessor
    {
        const string Suffix = ".h2utg.json";

        static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            foreach (var path in importedAssets)
            {
                if (!path.EndsWith(Suffix, System.StringComparison.OrdinalIgnoreCase))
                    continue;
                Materialize(path);
            }
        }

        static void Materialize(string jsonPath)
        {
            string raw;
            try { raw = File.ReadAllText(jsonPath); }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[Html2Uxml] failed to read {jsonPath}: {e.Message}");
                return;
            }

            TextGradientData data;
            try { data = JsonUtility.FromJson<TextGradientData>(raw); }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[Html2Uxml] invalid text gradient JSON at {jsonPath}: {e.Message}");
                return;
            }
            if (data == null)
                return;

            string assetPath = jsonPath.Substring(0, jsonPath.Length - Suffix.Length) + ".asset";
            var asset = AssetDatabase.LoadAssetAtPath<TextColorGradient>(assetPath);
            bool created = false;
            if (asset == null)
            {
                asset = ScriptableObject.CreateInstance<TextColorGradient>();
                created = true;
            }
            asset.colorMode = ParseMode(data.mode);
            asset.topLeft     = data.topLeft.ToColor();
            asset.topRight    = data.topRight.ToColor();
            asset.bottomLeft  = data.bottomLeft.ToColor();
            asset.bottomRight = data.bottomRight.ToColor();

            if (created)
                AssetDatabase.CreateAsset(asset, assetPath);
            else
                EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
        }

        static ColorGradientMode ParseMode(string mode)
        {
            if (string.IsNullOrEmpty(mode)) return ColorGradientMode.Single;
            switch (mode)
            {
                case "Single": return ColorGradientMode.Single;
                case "Horizontal": return ColorGradientMode.HorizontalGradient;
                case "Vertical": return ColorGradientMode.VerticalGradient;
                case "FourCornersGradient": return ColorGradientMode.FourCornersGradient;
                default: return ColorGradientMode.Single;
            }
        }

        [System.Serializable]
        class TextGradientData
        {
            public string name;
            public string mode;
            public RGBA topLeft;
            public RGBA topRight;
            public RGBA bottomLeft;
            public RGBA bottomRight;
        }

        [System.Serializable]
        class RGBA
        {
            public float r;
            public float g;
            public float b;
            public float a;
            public Color ToColor() => new Color(r, g, b, a);
        }
    }
}
