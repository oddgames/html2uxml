using System.IO;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.Text;

namespace ODDGames.Html2Uxml.Editor
{
    /// Creates TextCore FontAsset files for html2uxml generated fonts.
    ///
    /// The CLI keeps source .ttf/.otf files in UI/Fonts and emits USS
    /// -unity-font-definition references to sibling "<font> SDF.asset" files.
    /// This postprocessor materializes those assets automatically when Unity
    /// imports or refreshes the generated UI folder.
    public class TextCoreFontAssetImporter : AssetPostprocessor
    {
        static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            bool createdAny = false;
            var processedFonts = new HashSet<string>();
            foreach (var path in importedAssets)
            {
                if (IsGeneratedFontPath(path) && processedFonts.Add(path))
                {
                    createdAny |= EnsureFontAsset(path);
                    continue;
                }

                if (IsFontManifestPath(path))
                {
                    foreach (var fontPath in FontPathsForManifest(path))
                    {
                        if (processedFonts.Add(fontPath))
                            createdAny |= EnsureFontAsset(fontPath);
                    }
                }
            }

            if (createdAny)
                AssetDatabase.SaveAssets();
        }

        static bool IsGeneratedFontPath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;
            string normalized = path.Replace('\\', '/');
            string ext = Path.GetExtension(normalized).ToLowerInvariant();
            if (ext != ".ttf" && ext != ".otf")
                return false;
            string dir = Path.GetDirectoryName(normalized)?.Replace('\\', '/') ?? "";
            if (string.IsNullOrEmpty(dir))
                return false;
            return File.Exists($"{dir}/html2uxml-fonts.json");
        }

        static bool IsFontManifestPath(string path)
        {
            string normalized = (path ?? "").Replace('\\', '/');
            return normalized.EndsWith("/html2uxml-fonts.json", System.StringComparison.OrdinalIgnoreCase);
        }

        static IEnumerable<string> FontPathsForManifest(string manifestPath)
        {
            string dir = Path.GetDirectoryName(manifestPath)?.Replace('\\', '/') ?? "";
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                yield break;

            foreach (var path in Directory.GetFiles(dir))
            {
                string normalized = path.Replace('\\', '/');
                if (IsGeneratedFontPath(normalized))
                    yield return normalized;
            }
        }

        static bool EnsureFontAsset(string fontPath)
        {
            string assetPath = FontAssetPathFor(fontPath);
            var existing = AssetDatabase.LoadAssetAtPath<FontAsset>(assetPath);
            var sourceFont = AssetDatabase.LoadAssetAtPath<Font>(fontPath);
            if (sourceFont == null)
                return false;

            var manifest = FontManifest.LoadForFont(fontPath);
            var manifestEntry = manifest.EntryFor(fontPath, assetPath);
            var characterSet = UniqueCharacters((manifest.defaultCharacters ?? DefaultStaticCharacters) + (manifestEntry?.characters ?? ""));
            var atlasMode = FontAtlasModeFor(manifestEntry);

            if (existing != null)
            {
                if (existing.sourceFontFile == null)
                {
                    var field = typeof(FontAsset).GetField(
                        "m_SourceFontFile",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    if (field != null)
                        field.SetValue(existing, sourceFont);
                }
                ConfigureFontAsset(existing, atlasMode, characterSet, fontPath);
                EditorUtility.SetDirty(existing);
                return true;
            }

            var asset = FontAsset.CreateFontAsset(sourceFont);
            if (asset == null)
            {
                Debug.LogWarning($"[Html2Uxml] failed to create TextCore FontAsset for {fontPath}");
                return false;
            }

            asset.name = Path.GetFileNameWithoutExtension(assetPath);
            ConfigureFontAsset(asset, atlasMode, characterSet, fontPath);
            AssetDatabase.CreateAsset(asset, assetPath);
            return true;
        }

        static AtlasPopulationMode FontAtlasModeFor(FontManifestEntry entry)
        {
            // Always Dynamic. Unity 6 Advanced Text Generator rejects Static
            // font assets, and Dynamic loads glyphs on demand without an
            // atlas-size cap, which removes the need to predict every
            // character at conversion time.
            return AtlasPopulationMode.Dynamic;
        }

        static void ConfigureFontAsset(FontAsset asset, AtlasPopulationMode atlasMode, string characters, string fontPath)
        {
            if (atlasMode == AtlasPopulationMode.Static && !string.IsNullOrEmpty(characters))
            {
                asset.atlasPopulationMode = AtlasPopulationMode.Dynamic;
                if (!TryAddCharacters(asset, characters, out var missing) && !string.IsNullOrEmpty(missing))
                    Debug.LogWarning($"[Html2Uxml] FontAsset for {fontPath} is missing {missing.Length} requested characters.");
            }

            asset.atlasPopulationMode = atlasMode;
        }

        static bool TryAddCharacters(FontAsset asset, string characters, out string missing)
        {
            missing = "";
            var type = typeof(FontAsset);
            MethodInfo method = type.GetMethod(
                "TryAddCharacters",
                new[] { typeof(string), typeof(string).MakeByRefType(), typeof(bool) });
            if (method != null)
            {
                try
                {
                    object[] args = { characters, null, false };
                    var result = method.Invoke(asset, args);
                    missing = args[1] as string ?? "";
                    return result is bool ok && ok;
                }
                catch (System.Exception ex)
                {
                    Debug.LogWarning($"[Html2Uxml] failed to populate TextCore FontAsset characters: {ex.Message}");
                    return false;
                }
            }

            method = type.GetMethod(
                "TryAddCharacters",
                new[] { typeof(string), typeof(string).MakeByRefType() });
            if (method != null)
            {
                try
                {
                    object[] args = { characters, null };
                    var result = method.Invoke(asset, args);
                    missing = args[1] as string ?? "";
                    return result is bool ok && ok;
                }
                catch (System.Exception ex)
                {
                    Debug.LogWarning($"[Html2Uxml] failed to populate TextCore FontAsset characters: {ex.Message}");
                    return false;
                }
            }

            method = type.GetMethod("TryAddCharacters", new[] { typeof(string) });
            if (method != null)
            {
                try
                {
                    var result = method.Invoke(asset, new object[] { characters });
                    return result is bool ok && ok;
                }
                catch (System.Exception ex)
                {
                    Debug.LogWarning($"[Html2Uxml] failed to populate TextCore FontAsset characters: {ex.Message}");
                    return false;
                }
            }

            Debug.LogWarning("[Html2Uxml] Unity TextCore FontAsset.TryAddCharacters API was not found; generated SDF asset may need manual glyph population.");
            return false;
        }

        static string UniqueCharacters(string characters)
        {
            var seen = new HashSet<char>();
            var output = new System.Text.StringBuilder();
            foreach (char ch in characters ?? "")
            {
                if (seen.Add(ch))
                    output.Append(ch);
            }
            return output.ToString();
        }

        static string FontAssetPathFor(string fontPath)
        {
            string dir = Path.GetDirectoryName(fontPath)?.Replace('\\', '/') ?? "";
            string stem = Path.GetFileNameWithoutExtension(fontPath);
            return $"{dir}/{stem} SDF.asset";
        }

        const string DefaultStaticCharacters =
            " !\"#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`abcdefghijklmnopqrstuvwxyz{|}~" +
            "\n\t" +
            "•·–—…“”‘’©®™°×÷±←→↑↓✓✕★☆○●◉□■▲▼△▽";

        [System.Serializable]
        class FontManifest
        {
            public string defaultCharacters;
            public FontManifestEntry[] fonts;

            public static FontManifest LoadForFont(string fontPath)
            {
                string dir = Path.GetDirectoryName(fontPath)?.Replace('\\', '/') ?? "";
                string manifestPath = $"{dir}/html2uxml-fonts.json";
                if (!File.Exists(manifestPath))
                    return new FontManifest();

                try
                {
                    return JsonUtility.FromJson<FontManifest>(File.ReadAllText(manifestPath)) ?? new FontManifest();
                }
                catch (System.Exception ex)
                {
                    Debug.LogWarning($"[Html2Uxml] failed to read font manifest {manifestPath}: {ex.Message}");
                    return new FontManifest();
                }
            }

            public FontManifestEntry EntryFor(string fontPath, string assetPath)
            {
                if (fonts == null)
                    return null;
                string fontFile = Path.GetFileName(fontPath);
                string assetFile = Path.GetFileName(assetPath);
                foreach (var entry in fonts)
                {
                    if (entry == null)
                        continue;
                    if (SameName(entry.fontFile, fontFile) || SameName(entry.fontAsset, assetFile))
                        return entry;
                }
                return null;
            }

            static bool SameName(string a, string b)
            {
                return string.Equals(a ?? "", b ?? "", System.StringComparison.OrdinalIgnoreCase);
            }
        }

        [System.Serializable]
        class FontManifestEntry
        {
            public string fontFile;
            public string fontAsset;
            public string atlasMode;
            public string characters;
        }
    }
}
