using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;
using UnityEngine.TextCore.Text;

namespace ODDGames.Html2Uxml.Editor
{
    // Walks a Fonts/ directory inside the project, generates a sibling
    // TextCore SDF FontAsset for every .ttf / .otf, and embeds the
    // material + atlas textures as sub-assets so the .asset file is
    // self-contained.
    //
    // The auto-importer (Html2UxmlHtmlImporter) calls this after a
    // successful Pipeline.Convert so the USS's `-unity-font-definition`
    // urls (which point at `Fonts/Foo SDF.asset` when
    // UseTextcoreFontAssets=true) resolve to a real on-disk asset.
    // SDF assets are smaller at runtime than a raw TTF + glyph atlas
    // and let UI Toolkit render letter-spacing / italics with
    // TextCore's vector metrics rather than the legacy font shaper.
    public static class Html2UxmlFontAssetBuilder
    {
        const int SamplingPointSize = 90;
        const int Padding = 9;
        const int AtlasW = 1024;
        const int AtlasH = 1024;
        const string DefaultBakeCharacters =
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz" +
            "0123456789 .,:;!?'\"()[]{}<>/\\|-_=+*&^%$#@~`" +
            "→←↑↓◀▶◉◆◇●▲✕✓◰★☆";

        public static int Build(string fontsDir)
        {
            if (!Directory.Exists(fontsDir)) return 0;
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            string projRoot = Path.GetDirectoryName(Application.dataPath).Replace('\\', '/');
            int built = 0;
            foreach (var fontFile in Directory.EnumerateFiles(fontsDir, "*.*", SearchOption.TopDirectoryOnly))
            {
                string ext = Path.GetExtension(fontFile).ToLowerInvariant();
                if (ext != ".ttf" && ext != ".otf") continue;

                string normTtf = fontFile.Replace('\\', '/');
                if (!normTtf.StartsWith(projRoot + "/", StringComparison.OrdinalIgnoreCase)) continue;
                string ttfRel = normTtf.Substring(projRoot.Length + 1);
                AssetDatabase.ImportAsset(ttfRel,
                    ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                var font = AssetDatabase.LoadAssetAtPath<Font>(ttfRel);
                if (font == null) continue;

                string sdfName = Path.GetFileNameWithoutExtension(fontFile) + " SDF.asset";
                string sdfAbs = Path.Combine(Path.GetDirectoryName(fontFile), sdfName).Replace('\\', '/');
                string sdfRel = sdfAbs.Substring(projRoot.Length + 1);

                var existing = AssetDatabase.LoadAssetAtPath<FontAsset>(sdfRel);
                if (existing != null && IsUsableFontAsset(existing, font) && HasTunedMaterial(existing)) continue;
                if (existing != null) AssetDatabase.DeleteAsset(sdfRel);

                FontAsset fa = null;
                try
                {
                    fa = FontAsset.CreateFontAsset(
                        font, SamplingPointSize, Padding, GlyphRenderMode.SDFAA,
                        AtlasW, AtlasH, AtlasPopulationMode.Dynamic, true);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[html2uxml] CreateFontAsset failed for {ttfRel}: {e.Message}");
                    continue;
                }
                if (fa == null) continue;

                // Pre-bake common characters so the first frame doesn't
                // stutter pulling glyphs into the dynamic atlas. Static
                // analysis would narrow this further (only emit chars
                // that actually appear in the UXML's text="…" attrs),
                // but the baseline set covers ASCII + the geometric
                // symbols Replay uses for icons.
                fa.TryAddCharacters(UniqueChars(DefaultBakeCharacters), out _);

                var material = fa.material;
                var atlases = fa.atlasTextures?.ToArray();
                AssetDatabase.CreateAsset(fa, sdfRel);
                if (material != null)
                {
                    material.name = fa.name + " Material";
                    // TextCore's default SDF material applies a small
                    // face dilation that reads as "extra bold" at tiny
                    // pixel sizes (9-12 px UI labels). Browsers render
                    // the same TTF with hinted subpixel AA which is
                    // visually thinner. Negative _FaceDilate tightens
                    // the SDF threshold so the glyphs match a hinted
                    // raster more closely.
                    if (material.HasProperty("_FaceDilate"))
                        material.SetFloat("_FaceDilate", TunedFaceDilate);
                    if (material.HasProperty("_OutlineWidth"))
                        material.SetFloat("_OutlineWidth", 0f);
                    if (material.HasProperty("_OutlineSoftness"))
                        material.SetFloat("_OutlineSoftness", 0f);
                    AssetDatabase.AddObjectToAsset(material, fa);
                }
                if (atlases != null)
                {
                    for (int i = 0; i < atlases.Length; i++)
                    {
                        var atlas = atlases[i];
                        if (atlas == null) continue;
                        atlas.name = fa.name + " Atlas " + i;
                        AssetDatabase.AddObjectToAsset(atlas, fa);
                    }
                }
                AssetDatabase.ImportAsset(sdfRel,
                    ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                built++;
            }
            if (built > 0) AssetDatabase.SaveAssets();
            return built;
        }

        // Tag we leave on tuned materials so the next Build() pass
        // knows the SDF was built with our face-dilation tweak — older
        // SDFs from before this fix get rebuilt automatically.
        const float TunedFaceDilate = 0f;

        static bool HasTunedMaterial(FontAsset asset)
        {
            var m = asset?.material;
            if (m == null) return false;
            if (!m.HasProperty("_FaceDilate")) return true;
            return Mathf.Abs(m.GetFloat("_FaceDilate") - TunedFaceDilate) < 0.01f;
        }

        static bool IsUsableFontAsset(FontAsset asset, Font sourceFont)
        {
            try
            {
                if (asset == null || asset.sourceFontFile == null) return false;
                if (asset.sourceFontFile != sourceFont) return false;
                if (asset.atlasPopulationMode != AtlasPopulationMode.Dynamic) return false;
                return true;
            }
            catch { return false; }
        }

        static string UniqueChars(string text)
        {
            var seen = new System.Collections.Generic.HashSet<char>();
            var sb = new StringBuilder(text.Length);
            foreach (char c in text) if (seen.Add(c)) sb.Append(c);
            return sb.ToString();
        }
    }
}
