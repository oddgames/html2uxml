using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ODDGames.Html2Uxml.Editor.Converter.Assets;

namespace ODDGames.Html2Uxml.Editor.Converter
{
    // Top-level orchestrator — port of html2uxml/cli.py's `convert(...)` entry.
    // Stitches HtmlLoader → CssLoader → Resolver → StyleMapper → Converter →
    // AssetWriter into a single Editor-callable Convert(opt).
    public static class Pipeline
    {
        public sealed class ConvertOptions
        {
            public string SourceHtmlPath;
            public string OutputDir;
            public string Name;
            public bool BundleAssets = true;
            public bool DownloadFonts = true;
            public bool UseTextcoreFontAssets = true;
            // When true, write <OutputDir>/<Name>.uxml + .uss directly
            // (no UI/Src/Images/Fonts subfolders). Asset bundling +
            // source-html copy are skipped. Identical files are not
            // re-written; differing files are overwritten with a warning.
            public bool SiblingOutput;
        }

        public sealed class ConvertResult
        {
            public string UxmlPath;
            public string UssPath;
            public string SourceCopyPath;
            public List<string> Warnings = new List<string>();
        }

        public static ConvertResult Convert(ConvertOptions opt)
        {
            if (opt == null) throw new ArgumentNullException(nameof(opt));
            if (string.IsNullOrEmpty(opt.SourceHtmlPath))
                throw new ArgumentException("SourceHtmlPath required");
            if (string.IsNullOrEmpty(opt.OutputDir))
                throw new ArgumentException("OutputDir required");
            string baseName = string.IsNullOrEmpty(opt.Name)
                ? Path.GetFileNameWithoutExtension(opt.SourceHtmlPath)
                : opt.Name;

            // Don't add the 0.1px text-outline compensation. Both raw-TTF
            // and SDF render paths in UI Toolkit produce strokes that
            // already match Chrome closely; the outline trick makes every
            // glyph look smeared/bold. Keep the field for opt-in later.
            Css.StyleMapper.ApplyChromeRasterCompensation = false;

            var convertResult = ConvertEngine.Convert(new ConvertEngine.ConvertOptions
            {
                SourceHtmlPath = opt.SourceHtmlPath,
            });

            // Font injection — replace --odd-font-family decls with
            // -unity-font-definition url(...). When DownloadFonts is on,
            // first probe Google Fonts for every family the document
            // mentions and write the resulting .ttf into Fonts/, then
            // hand the populated mapping to FontInjector so the emitted
            // url() points at the file we actually wrote. Without this
            // pass the USS would reference Fonts/X.ttf paths that no
            // file ever materialises — the symptom is a wall of
            // "Invalid asset path: Assets/UI/<bundle>/Fonts/*.ttf"
            // warnings on import.
            Dictionary<string, List<FontVariant>> fontMapping = null;
            if (opt.DownloadFonts)
            {
                var families = FontInjector.CollectFamilies(convertResult.Uss).ToList();
                if (families.Count > 0)
                {
                    string fontsDir = opt.SiblingOutput
                        ? Path.Combine(opt.OutputDir, "Fonts")
                        : Path.Combine(opt.OutputDir, "UI", "Fonts");
                    try
                    {
                        var (mapping, _) = FontFetcher.DownloadGoogleFonts(
                            new FontFetcher.DownloadGoogleFontsOptions
                            {
                                Families = families,
                                FontsDir = fontsDir,
                                ProjectSubdir = "Fonts",
                            });
                        fontMapping = mapping;
                    }
                    catch (Exception e)
                    {
                        // Don't fail conversion if font fetch errors out — the
                        // USS will just reference Fonts/<synth>.ttf with no
                        // backing file, same fail-mode as before this pass.
                        Console.Error.WriteLine("[html2uxml] font fetch failed: " + e.Message);
                    }
                }
            }

            convertResult.Uss = FontInjector.Inject(
                convertResult.Uss,
                mapping: fontMapping,
                useTextcoreFontAssets: opt.UseTextcoreFontAssets,
                uxml: convertResult.Uxml);

            string sourceBaseDir = Path.GetDirectoryName(Path.GetFullPath(opt.SourceHtmlPath));

            var write = Assets.AssetWriter.Write(new Assets.AssetWriter.WriteOptions
            {
                OutputDir = opt.OutputDir,
                BaseName = baseName,
                SourceHtmlPath = opt.SourceHtmlPath,
                BundleAssets = opt.BundleAssets && !opt.SiblingOutput,
                DownloadFonts = opt.DownloadFonts,
                UseTextcoreFontAssets = opt.UseTextcoreFontAssets,
                SiblingOutput = opt.SiblingOutput,
            }, convertResult.Uxml, convertResult.Uss, sourceBaseDir);

            // Write any inline-extracted assets (e.g. <svg> blocks). In bundle
            // mode they land under <OutputDir>/UI/Images/...; sibling mode
            // drops them directly into <OutputDir>/Images/... since there's
            // no UI/ wrapper and USS references `Images/...` relative paths.
            if (convertResult.ExtraAssetFiles != null && convertResult.ExtraAssetFiles.Count > 0)
            {
                string assetRoot = opt.SiblingOutput
                    ? opt.OutputDir
                    : Path.Combine(opt.OutputDir, "UI");
                foreach (var kv in convertResult.ExtraAssetFiles)
                {
                    string target = Path.Combine(assetRoot, kv.Key.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    string svgText = kv.Value;
                    if (target.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                        svgText = ODDGames.Html2Uxml.Editor.Html2UxmlUrlFetcher.SanitizeSvgText(svgText);
                    File.WriteAllText(target, svgText);
                }
            }

            var result = new ConvertResult
            {
                UxmlPath = write.UxmlPath,
                UssPath = write.UssPath,
                SourceCopyPath = write.SourceCopyPath,
            };
            result.Warnings.AddRange(convertResult.Warnings);
            if (write.Warnings != null) result.Warnings.AddRange(write.Warnings);
            return result;
        }
    }
}
