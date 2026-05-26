using System.Collections.Generic;
using System.IO;
using ODDGames.Html2Uxml.Editor.Converter;
using UnityEditor;
using UnityEngine;

namespace ODDGames.Html2Uxml.Editor
{
    // Unity 6 has a native importer for .html/.htm, so this runs conversion
    // from asset postprocessing instead of registering a ScriptedImporter.
    public sealed class Html2UxmlHtmlImporter : AssetPostprocessor
    {
        const bool DownloadFonts = true;
        // TextCore SDF FontAssets render with proper metrics (letter-spacing,
        // italics) and save runtime memory vs raw-TTF glyph caches. The
        // converter emits `-unity-font-definition: url("Fonts/Foo SDF.asset")`
        // when this flag is on; Html2UxmlFontAssetBuilder bakes the .asset
        // next to each .ttf after Pipeline.Convert finishes.
        const bool UseTextcoreFontAssets = true;

        static bool _converting;
        static readonly HashSet<string> PendingImports = new HashSet<string>();

        static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            if (_converting)
                return;

            var htmlAssets = new HashSet<string>();
            AddHtmlAssets(importedAssets, htmlAssets);
            AddHtmlAssets(movedAssets, htmlAssets);
            AddHtmlSiblingsForCss(importedAssets, htmlAssets);
            AddHtmlSiblingsForCss(movedAssets, htmlAssets);

            if (htmlAssets.Count == 0)
                return;

            _converting = true;
            try
            {
                foreach (string assetPath in htmlAssets)
                    ConvertHtmlAsset(assetPath);
            }
            finally
            {
                _converting = false;
            }

            QueueGeneratedImports();
        }

        static void AddHtmlAssets(IEnumerable<string> assetPaths, HashSet<string> htmlAssets)
        {
            if (assetPaths == null)
                return;
            foreach (string assetPath in assetPaths)
            {
                if (IsHtmlAsset(assetPath))
                    htmlAssets.Add(assetPath.Replace('\\', '/'));
            }
        }

        static void AddHtmlSiblingsForCss(IEnumerable<string> assetPaths, HashSet<string> htmlAssets)
        {
            if (assetPaths == null)
                return;
            foreach (string assetPath in assetPaths)
            {
                if (!IsCssAsset(assetPath))
                    continue;

                string dir = Path.GetDirectoryName(assetPath);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                    continue;

                foreach (string html in Directory.GetFiles(dir, "*.html"))
                    htmlAssets.Add(html.Replace('\\', '/'));
                foreach (string html in Directory.GetFiles(dir, "*.htm"))
                    htmlAssets.Add(html.Replace('\\', '/'));
            }
        }

        static void ConvertHtmlAsset(string assetPath)
        {
            string sourcePath = Path.GetFullPath(assetPath);
            if (!File.Exists(sourcePath))
                return;

            string assetDir = Path.GetDirectoryName(assetPath);
            string name = Path.GetFileNameWithoutExtension(assetPath);
            string outDir = Path.GetFullPath(assetDir.Replace('\\', '/'));

            try
            {
                var result = Pipeline.Convert(new Pipeline.ConvertOptions
                {
                    SourceHtmlPath = sourcePath,
                    OutputDir = outDir,
                    Name = name,
                    SiblingOutput = true,
                    BundleAssets = false,
                    DownloadFonts = DownloadFonts,
                    UseTextcoreFontAssets = UseTextcoreFontAssets,
                });

                foreach (string warning in result.Warnings)
                    Debug.LogWarning("[html2uxml] " + warning);

                if (!File.Exists(result.UxmlPath))
                    Debug.LogWarning($"[html2uxml] converter ran but {result.UxmlPath} was not produced.");

                QueueImport(result.UxmlPath);
                QueueImport(result.UssPath);

                // Bake TextCore SDF FontAssets for any .ttf the converter
                // dropped into <bundle>/Fonts/. Without this step the USS
                // would reference Foo SDF.asset paths that don't exist on
                // disk and every label would fall back to the editor's
                // default font.
                if (UseTextcoreFontAssets)
                {
                    string fontsDir = Path.Combine(outDir, "Fonts");
                    if (Directory.Exists(fontsDir))
                    {
                        try { Html2UxmlFontAssetBuilder.Build(fontsDir); }
                        catch (System.Exception e)
                        {
                            Debug.LogWarning("[html2uxml] SDF build failed: " + e.Message);
                        }
                    }
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[html2uxml] C# converter failed for {assetPath}: {e.Message}\n{e.StackTrace}");
            }
        }

        static void QueueImport(string absolutePath)
        {
            if (string.IsNullOrEmpty(absolutePath))
                return;
            string projectPath = MakeProjectPath(absolutePath);
            if (projectPath.StartsWith("Assets/", System.StringComparison.OrdinalIgnoreCase))
                PendingImports.Add(projectPath);
        }

        static void QueueGeneratedImports()
        {
            if (PendingImports.Count == 0)
                return;

            var paths = new List<string>(PendingImports);
            PendingImports.Clear();
            EditorApplication.delayCall += () =>
            {
                foreach (string path in paths)
                {
                    if (File.Exists(path))
                        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                }
            };
        }

        static bool IsHtmlAsset(string assetPath)
        {
            string ext = Path.GetExtension(assetPath);
            return ext.Equals(".html", System.StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".htm", System.StringComparison.OrdinalIgnoreCase);
        }

        static bool IsCssAsset(string assetPath)
        {
            return Path.GetExtension(assetPath).Equals(".css", System.StringComparison.OrdinalIgnoreCase);
        }

        static string MakeProjectPath(string absolutePath)
        {
            string proj = Path.GetFullPath(Application.dataPath + "/..");
            string norm = Path.GetFullPath(absolutePath);
            if (norm.StartsWith(proj))
                return norm.Substring(proj.Length).TrimStart('\\', '/').Replace('\\', '/');
            return absolutePath.Replace('\\', '/');
        }
    }
}
