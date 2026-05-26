// SPDX-License-Identifier: MIT
//
// FontFetcher — Google Fonts download + embedded @font-face extraction +
// local-system font fallback. Port of html2uxml/assets.py:
//   collect_font_families, extract_embedded_font_faces, download_google_fonts,
//   _font_face_weight, _font_face_italic, _local_font_*, _font_search_prefixes.
//
// DEFERRED:
// * WOFF / WOFF2 decoding. The Python code uses fontTools (+ brotli for WOFF2)
//   to decompress those into TTF/OTF. There is no comparable C# library
//   shipping with Unity. WOFF/WOFF2 sources are surfaced in
//   `EmbeddedFontReport.Skipped` so the caller can fall back to Google Fonts.
// * "Wanted variants" narrowing — the CLI plumbs a per-family
//   (weight, italic) filter to skip unused variants. We don't have access to
//   element-usage info inside this module, so the API takes an optional set
//   and applies it the same way; callers without the data pass null.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace ODDGames.Html2Uxml.Editor.Converter.Assets
{
    public sealed class FontVariant
    {
        public string Path;
        public int Weight = 400;
        public bool Italic;
        public string Source = string.Empty;

        public override string ToString() => $"{Path} w={Weight} italic={Italic} ({Source})";
    }

    public sealed class EmbeddedFontReport
    {
        public List<(string family, string projectRelativePath)> Extracted = new List<(string, string)>();
        public List<(string subject, string reason)> Failed = new List<(string, string)>();
        public List<(string url, string reason)> Skipped = new List<(string, string)>();
    }

    public sealed class FontReport
    {
        public List<string> FontsSeen = new List<string>();
        public List<(string family, string path)> FontsDownloaded = new List<(string, string)>();
        public List<(string subject, string reason)> Failed = new List<(string, string)>();
    }

    public static class FontFetcher
    {
        // --- shared HttpClient (HttpClient is intended to be shared; one per
        //     module avoids the socket-exhaustion footgun).
        private static readonly Lazy<HttpClient> _httpLazy = new Lazy<HttpClient>(() =>
        {
            var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("html2uxml/0.2");
            return client;
        });
        public static HttpClient Http => _httpLazy.Value;

        // --- regexes
        private static readonly Regex FontFamilyDeclRe = new Regex(
            @"font-family\s*:\s*([^;}]+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex AtFontFaceRe = new Regex(
            @"@font-face\s*\{([^}]*)\}",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

        private static readonly Regex FaceFamilyRe = new Regex(
            @"font-family\s*:\s*([^;]+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex FaceSrcRe = new Regex(
            @"src\s*:\s*((?:[^;]|;(?=base64))+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex FaceSrcEntryRe = new Regex(
            @"url\(\s*(?:""((?:[^""\\]|\\.)*)""|'((?:[^'\\]|\\.)*)'|([^)\s]+))\s*\)" +
            @"(?:\s*format\(\s*(?:""([^""]*)""|'([^']*)'|([^)\s]+))\s*\))?",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

        private static readonly Regex FontDataUriRe = new Regex(
            @"^data:(?<mime>[^;,]*)(?<base64>;base64)?,(?<payload>.*)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

        private static readonly Regex FontWeightRe = new Regex(
            @"font-weight\s*:\s*([^;]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex FontStyleRe = new Regex(
            @"font-style\s*:\s*([^;]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex UnicodeRangeRe = new Regex(
            @"unicode-range\s*:\s*([^;]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly HashSet<string> TtfOtfFormats = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "truetype", "opentype", "ttf", "otf" };
        private static readonly HashSet<string> TtfOtfMimes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "font/ttf", "font/otf",
            "application/x-font-ttf", "application/x-font-otf",
            "application/x-font-truetype", "application/x-font-opentype",
            "application/font-ttf", "application/font-otf",
            "application/font-sfnt",
            "application/octet-stream",
        };
        private static readonly HashSet<string> WoffFormats = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "woff", "woff2" };
        private static readonly HashSet<string> WoffMimes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "font/woff", "font/woff2",
            "application/font-woff", "application/font-woff2",
        };
        private static readonly HashSet<string> UnsupportedFormats = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "embedded-opentype", "svg" };

        // SFNT magic numbers — confirms the bytes are already TTF/OTF.
        private static readonly byte[][] SfntTags = new[]
        {
            new byte[] { 0x00, 0x01, 0x00, 0x00 }, // TTF (TrueType)
            Encoding.ASCII.GetBytes("OTTO"),       // OTF
            Encoding.ASCII.GetBytes("true"),
            Encoding.ASCII.GetBytes("typ1"),
        };

        public static List<string> CollectFontFamilies(string cssText)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(cssText)) return result;
            foreach (Match m in FontFamilyDeclRe.Matches(cssText))
            {
                foreach (var tok in m.Groups[1].Value.Split(','))
                {
                    string name = tok.Trim().Trim('"').Trim('\'').Trim();
                    if (string.IsNullOrEmpty(name)) continue;
                    if (PathBundler.GenericFontFamilies.Contains(name)) continue;
                    if (!seen.Contains(name))
                    {
                        seen.Add(name);
                        result.Add(name);
                    }
                    break;
                }
            }
            return result;
        }

        // ---------- Embedded @font-face ----------

        public sealed class ExtractEmbeddedOptions
        {
            public string CssText;
            public string BaseDir;
            public string FontsDir;
            public string ProjectSubdir = "Fonts";
            public TimeSpan Timeout = TimeSpan.FromSeconds(8);
            public bool DownloadRemote = true;
            public HashSet<string> FamiliesFilter;  // null = no filter
        }

        public static (Dictionary<string, List<FontVariant>> mapping, EmbeddedFontReport report)
            ExtractEmbeddedFontFaces(ExtractEmbeddedOptions opt)
        {
            var report = new EmbeddedFontReport();
            var mapping = new Dictionary<string, List<FontVariant>>(StringComparer.Ordinal);
            if (opt == null || string.IsNullOrEmpty(opt.CssText)) return (mapping, report);

            Directory.CreateDirectory(opt.FontsDir);

            var candidates = new Dictionary<(string family, int weight, bool italic), FaceCandidate>();
            foreach (Match face in AtFontFaceRe.Matches(opt.CssText))
            {
                string body = face.Groups[1].Value;
                var familyMatch = FaceFamilyRe.Match(body);
                var srcMatch = FaceSrcRe.Match(body);
                if (!familyMatch.Success || !srcMatch.Success) continue;
                string family = CleanFaceFamily(familyMatch.Groups[1].Value);
                if (string.IsNullOrEmpty(family)) continue;
                if (opt.FamiliesFilter != null && !opt.FamiliesFilter.Contains(family)) continue;
                int weight = FontFaceWeight(body);
                bool italic = FontFaceItalic(body);
                var chosen = ChooseFaceSrcEntry(srcMatch.Groups[1].Value);
                if (chosen == null)
                {
                    report.Failed.Add((family, "no usable src in @font-face"));
                    continue;
                }
                string unicodeRange = FontFaceUnicodeRange(body);
                int score = EmbeddedFaceUnicodeScore(unicodeRange);
                var key = (family, weight, italic);
                if (!candidates.TryGetValue(key, out var existing) || score < existing.Score)
                {
                    candidates[key] = new FaceCandidate
                    {
                        Family = family,
                        Weight = weight,
                        Italic = italic,
                        Url = chosen.Value.url,
                        Format = chosen.Value.fmt,
                        Score = score,
                    };
                }
            }

            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sortedKeys = new List<(string family, int weight, bool italic)>(candidates.Keys);
            sortedKeys.Sort((a, b) =>
            {
                int cmp = string.CompareOrdinal(a.family, b.family);
                if (cmp != 0) return cmp;
                cmp = a.weight.CompareTo(b.weight);
                if (cmp != 0) return cmp;
                return a.italic.CompareTo(b.italic);
            });

            foreach (var key in sortedKeys)
            {
                var c = candidates[key];
                byte[] data; string suffix;
                try
                {
                    var resolved = ResolveFaceSrc(c.Url, c.Format, opt, report);
                    if (resolved == null) continue;
                    data = resolved.Value.bytes;
                    suffix = resolved.Value.suffix;
                }
                catch (FaceSrcException e)
                {
                    report.Failed.Add((c.Family, e.Message));
                    continue;
                }

                string targetName = PathBundler.UniqueName(
                    PathBundler.SafeFontName(c.Family, suffix, c.Weight, c.Italic),
                    usedNames);
                targetName = PathBundler.WriteBytesSmart(opt.FontsDir, targetName, data);
                string relpath = opt.ProjectSubdir + "/" + targetName;
                if (!mapping.TryGetValue(c.Family, out var list))
                {
                    list = new List<FontVariant>();
                    mapping[c.Family] = list;
                }
                list.Add(new FontVariant
                {
                    Path = relpath,
                    Weight = c.Weight,
                    Italic = c.Italic,
                    Source = "embedded",
                });
                report.Extracted.Add((c.Family, relpath));
            }

            return (mapping, report);
        }

        private sealed class FaceCandidate
        {
            public string Family;
            public int Weight;
            public bool Italic;
            public string Url;
            public string Format;
            public int Score;
        }

        private sealed class FaceSrcException : Exception
        {
            public FaceSrcException(string msg) : base(msg) { }
        }

        // Returns (bytes, suffix) or null when intentionally skipped.
        private static (byte[] bytes, string suffix)? ResolveFaceSrc(
            string url, string fmt, ExtractEmbeddedOptions opt, EmbeddedFontReport report)
        {
            if (UnsupportedFormats.Contains(fmt ?? string.Empty))
            {
                report.Skipped.Add((url, $"format '{fmt}' not supported by Unity TextCore"));
                return null;
            }

            if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var (data, suffix) = ReadDataUriFont(url);
                return MaybeDecodeWoff(data, suffix, report, source: Truncate(url, 60) + "...");
            }

            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                if (!opt.DownloadRemote)
                {
                    report.Skipped.Add((url, "remote (download disabled)"));
                    return null;
                }
                byte[] bytes;
                try
                {
                    bytes = HttpDownload(url, opt.Timeout);
                }
                catch (Exception e)
                {
                    throw new FaceSrcException("download failed: " + e.Message);
                }
                string suffix = PathBundler.FontSuffixFromUrl(url);
                return MaybeDecodeWoff(bytes, suffix, report, source: url);
            }

            // Local relative or absolute filesystem path.
            string srcPath = url;
            if (!Path.IsPathRooted(srcPath) && !string.IsNullOrEmpty(opt.BaseDir))
                srcPath = Path.Combine(opt.BaseDir, srcPath);
            byte[] data2;
            try
            {
                data2 = File.ReadAllBytes(srcPath);
            }
            catch (Exception e)
            {
                throw new FaceSrcException("file not found: " + url + " (" + e.Message + ")");
            }
            string suffix2 = Path.GetExtension(srcPath)?.ToLowerInvariant() ?? string.Empty;
            if (suffix2 != ".ttf" && suffix2 != ".otf" && suffix2 != ".woff" && suffix2 != ".woff2")
                suffix2 = ".ttf";
            return MaybeDecodeWoff(data2, suffix2, report, source: srcPath);
        }

        // The Python code decodes WOFF/WOFF2 with fontTools; we can't, so we
        // surface the bytes only when they're already SFNT (TTF/OTF). WOFF
        // payloads land in `report.Skipped`.
        private static (byte[] bytes, string suffix)? MaybeDecodeWoff(
            byte[] data, string suffix, EmbeddedFontReport report, string source)
        {
            if (data == null || data.Length < 4)
                return (data ?? Array.Empty<byte>(), NormaliseSfntSuffix(suffix));
            if (HasSfntHeader(data))
                return (data, NormaliseSfntSuffix(suffix));
            if (data[0] == 'w' && data[1] == 'O' && data[2] == 'F' && data[3] == 'F')
            {
                report.Skipped.Add((source, "WOFF decode requires fontTools (deferred in C# port)"));
                return null;
            }
            if (data[0] == 'w' && data[1] == 'O' && data[2] == 'F' && data[3] == '2')
            {
                report.Skipped.Add((source, "WOFF2 decode requires fontTools+brotli (deferred in C# port)"));
                return null;
            }
            // Unknown header — pass through; TextCore will surface the error.
            return (data, NormaliseSfntSuffix(suffix));
        }

        private static bool HasSfntHeader(byte[] data)
        {
            if (data == null || data.Length < 4) return false;
            foreach (var tag in SfntTags)
            {
                bool match = true;
                for (int i = 0; i < 4; i++)
                    if (data[i] != tag[i]) { match = false; break; }
                if (match) return true;
            }
            return false;
        }

        private static string NormaliseSfntSuffix(string s)
            => (s == ".ttf" || s == ".otf") ? s : ".ttf";

        private static (byte[] bytes, string suffix) ReadDataUriFont(string url)
        {
            var m = FontDataUriRe.Match(url);
            if (!m.Success) throw new FaceSrcException("malformed data: URI");
            string mime = (m.Groups["mime"].Value ?? string.Empty).ToLowerInvariant();
            string payload = m.Groups["payload"].Value;
            byte[] data;
            try
            {
                if (m.Groups["base64"].Success)
                    data = Convert.FromBase64String(StripWhitespace(payload));
                else
                    data = PathBundler.UrlUnquoteToBytes(payload);
            }
            catch (Exception e)
            {
                throw new FaceSrcException("base64 decode failed: " + e.Message);
            }
            string suffix;
            if (mime.Contains("woff2")) suffix = ".woff2";
            else if (mime.Contains("woff")) suffix = ".woff";
            else if (mime.Contains("otf") || mime.Contains("opentype")) suffix = ".otf";
            else suffix = ".ttf";
            return (data, suffix);
        }

        private static (string url, string fmt)? ChooseFaceSrcEntry(string src)
        {
            var entries = new List<(int rank, string url, string fmt)>();
            foreach (Match m in FaceSrcEntryRe.Matches(src))
            {
                string url = (m.Groups[1].Value + m.Groups[2].Value + m.Groups[3].Value).Trim();
                if (string.IsNullOrEmpty(url))
                {
                    if (!string.IsNullOrEmpty(m.Groups[3].Value)) url = m.Groups[3].Value.Trim();
                    else continue;
                }
                string fmt = (m.Groups[4].Value + m.Groups[5].Value + m.Groups[6].Value).Trim().ToLowerInvariant();
                int rank = FaceSrcRank(url, fmt);
                entries.Add((rank, url, fmt));
            }
            if (entries.Count == 0) return null;
            entries.Sort((a, b) => a.rank.CompareTo(b.rank));
            var best = entries[0];
            return best.rank >= 100 ? null : (best.url, best.fmt);
        }

        private static int FaceSrcRank(string url, string fmt)
        {
            if (UnsupportedFormats.Contains(fmt)) return 100;
            string trimmed = url;
            int q = trimmed.IndexOf('?'); if (q >= 0) trimmed = trimmed.Substring(0, q);
            int h = trimmed.IndexOf('#'); if (h >= 0) trimmed = trimmed.Substring(0, h);
            string suffix = Path.GetExtension(trimmed)?.ToLowerInvariant() ?? string.Empty;
            if (suffix == ".eot") return 100;
            if (TtfOtfFormats.Contains(fmt)) return 0;
            if (suffix == ".ttf" || suffix == ".otf") return 1;
            if (WoffFormats.Contains(fmt)) return 50;
            if (suffix == ".woff" || suffix == ".woff2") return 51;
            if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var m = FontDataUriRe.Match(url);
                if (m.Success)
                {
                    string mime = (m.Groups["mime"].Value ?? string.Empty).ToLowerInvariant();
                    if (TtfOtfMimes.Contains(mime)) return 0;
                    if (WoffMimes.Contains(mime) || mime.Contains("woff")) return 50;
                }
            }
            return 5;
        }

        private static string CleanFaceFamily(string raw)
        {
            string name = (raw ?? string.Empty).Trim().TrimEnd(',').Trim();
            int comma = name.IndexOf(',');
            if (comma >= 0) name = name.Substring(0, comma);
            name = name.Trim().Trim('"').Trim('\'');
            if (string.IsNullOrEmpty(name)) return null;
            if (PathBundler.GenericFontFamilies.Contains(name)) return null;
            return name;
        }

        private static int EmbeddedFaceUnicodeScore(string unicodeRange)
        {
            if (string.IsNullOrEmpty(unicodeRange)) return 0;
            string r = unicodeRange.ToLowerInvariant();
            if (r.Contains("u+0000-00ff") || r.Contains("u+0000-007f") || r.Contains("u+0020-007f"))
                return 1;
            if (r.Contains("u+0100-02af")) return 5;
            return 10;
        }

        private static int FontFaceWeight(string face)
        {
            var m = FontWeightRe.Match(face);
            if (!m.Success) return 400;
            string value = m.Groups[1].Value.Trim().ToLowerInvariant();
            if (value == "normal") return 400;
            if (value == "bold") return 700;
            int best = -1;
            foreach (Match num in Regex.Matches(value, @"\d+(?:\.\d+)?"))
            {
                if (double.TryParse(num.Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double d))
                {
                    int n = (int)d;
                    if (n > best) best = n;
                }
            }
            return best > 0 ? best : 400;
        }

        private static bool FontFaceItalic(string face)
        {
            var m = FontStyleRe.Match(face);
            if (!m.Success) return false;
            string value = m.Groups[1].Value.Trim().ToLowerInvariant();
            return value == "italic" || value == "oblique";
        }

        private static string FontFaceUnicodeRange(string face)
        {
            var m = UnicodeRangeRe.Match(face);
            return m.Success ? m.Groups[1].Value.Trim() : string.Empty;
        }

        // ---------- Google Fonts ----------

        public sealed class DownloadGoogleFontsOptions
        {
            public IEnumerable<string> Families;
            public string FontsDir;
            public string ProjectSubdir = "Fonts";
            public TimeSpan Timeout = TimeSpan.FromSeconds(8);
            public Dictionary<string, HashSet<(int weight, bool italic)>> Wanted; // null = broad
            public Dictionary<string, List<FontVariant>> Seed;                    // already-resolved variants
        }

        public static (Dictionary<string, List<FontVariant>> mapping, FontReport report)
            DownloadGoogleFonts(DownloadGoogleFontsOptions opt)
        {
            var report = new FontReport();
            var output = new Dictionary<string, List<FontVariant>>(StringComparer.Ordinal);
            if (opt?.Families == null) return (output, report);

            Directory.CreateDirectory(opt.FontsDir);

            foreach (string family in opt.Families)
            {
                report.FontsSeen.Add(family);

                HashSet<(int weight, bool italic)> wantedSet = null;
                if (opt.Wanted != null && opt.Wanted.TryGetValue(family, out var w))
                {
                    wantedSet = w;
                    if (wantedSet != null && wantedSet.Count == 0)
                        continue; // caller said no usage
                }

                List<FontVariant> seeded = null;
                if (opt.Seed != null && opt.Seed.TryGetValue(family, out var seedList))
                    seeded = new List<FontVariant>(seedList);
                if (seeded != null && seeded.Count > 0)
                {
                    // Embedded payloads are authoritative — don't probe Google.
                    output[family] = seeded;
                    continue;
                }

                var variants = new List<FontVariant>();
                variants.AddRange(CopyLocalFontVariants(family, opt.FontsDir, opt.ProjectSubdir, report, wantedSet));

                if (wantedSet != null)
                {
                    var covered = new HashSet<(int, bool)>();
                    foreach (var v in variants) covered.Add((v.Weight, v.Italic));
                    if (IsSubsetOf(wantedSet, covered))
                    {
                        output[family] = variants;
                        continue;
                    }
                }
                else if (HasCommonAxisCoverage(variants))
                {
                    output[family] = variants;
                    continue;
                }

                // Try Google Fonts CSS API. Force ttf by spoofing an old UA;
                // otherwise Google returns woff2 which Unity can't load.
                string css = null;
                var cssErrors = new List<string>();
                foreach (string cssUrl in GoogleFontCssUrls(family))
                {
                    try
                    {
                        css = HttpDownloadString(cssUrl, opt.Timeout, "Mozilla/4.0 (Windows NT 5.1)");
                        if (!string.IsNullOrEmpty(css)) break;
                    }
                    catch (Exception e)
                    {
                        cssErrors.Add(e.Message);
                    }
                }

                var faces = string.IsNullOrEmpty(css)
                    ? new List<GoogleFace>()
                    : PreferredGoogleFaces(GoogleTtfFaces(css));
                if (wantedSet != null)
                {
                    faces.RemoveAll(f => !wantedSet.Contains((f.Weight, f.Italic)));
                }

                var existingKeys = new HashSet<(int, bool)>();
                foreach (var v in variants) existingKeys.Add((v.Weight, v.Italic));
                var seenUrls = new HashSet<string>(StringComparer.Ordinal);
                var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var v in variants) usedNames.Add(Path.GetFileName(v.Path).ToLowerInvariant());

                foreach (var face in faces)
                {
                    if (!seenUrls.Add(face.Url)) continue;
                    if (existingKeys.Contains((face.Weight, face.Italic))) continue;
                    string targetName = PathBundler.UniqueName(
                        PathBundler.SafeFontName(family, PathBundler.FontSuffixFromUrl(face.Url),
                            face.Weight, face.Italic),
                        usedNames);
                    string local;
                    try
                    {
                        byte[] data = HttpDownload(face.Url, opt.Timeout);
                        local = PathBundler.WriteBytesSmart(opt.FontsDir, targetName, data);
                    }
                    catch (Exception e)
                    {
                        report.Failed.Add((family, "download failed: " + e.Message));
                        continue;
                    }
                    string relpath = opt.ProjectSubdir + "/" + local;
                    variants.Add(new FontVariant
                    {
                        Path = relpath,
                        Weight = face.Weight,
                        Italic = face.Italic,
                        Source = "google",
                    });
                    existingKeys.Add((face.Weight, face.Italic));
                    report.FontsDownloaded.Add((family, relpath));
                }

                if (variants.Count > 0)
                {
                    output[family] = variants;
                }
                else if (cssErrors.Count > 0)
                {
                    report.Failed.Add((family, string.Join("; ", cssErrors.Count > 2 ? cssErrors.GetRange(0, 2) : cssErrors)));
                }
                else
                {
                    report.Failed.Add((family, "no ttf url in CSS response; no local ttf/otf match"));
                }
            }

            return (output, report);
        }

        private sealed class GoogleFace
        {
            public string Url;
            public int Weight = 400;
            public bool Italic;
            public string UnicodeRange = string.Empty;
        }

        private static IEnumerable<string> GoogleFontCssUrls(string family)
        {
            string spec = (family ?? string.Empty).Replace(" ", "+");
            const string weights = "400;500;600;700;800;900";
            const string italicWeights = "0,400;0,500;0,600;0,700;0,800;0,900;1,400;1,500;1,600;1,700;1,800;1,900";
            yield return $"https://fonts.googleapis.com/css2?family={spec}:ital,wght@{italicWeights}&display=swap";
            yield return $"https://fonts.googleapis.com/css2?family={spec}:wght@{weights}&display=swap";
            yield return $"https://fonts.googleapis.com/css2?family={spec}&display=swap";
        }

        private static List<GoogleFace> PreferredGoogleFaces(List<GoogleFace> faces)
        {
            var selected = new Dictionary<(int, bool), GoogleFace>();
            foreach (var face in faces)
            {
                var key = (face.Weight, face.Italic);
                if (!selected.TryGetValue(key, out var current)
                    || GoogleFaceScore(face) > GoogleFaceScore(current))
                    selected[key] = face;
            }
            var list = new List<GoogleFace>(selected.Values);
            list.Sort((a, b) =>
            {
                int cmp = a.Weight.CompareTo(b.Weight);
                return cmp != 0 ? cmp : a.Italic.CompareTo(b.Italic);
            });
            return list;
        }

        private static int GoogleFaceScore(GoogleFace face)
        {
            string r = (face.UnicodeRange ?? string.Empty).ToLowerInvariant();
            if (string.IsNullOrEmpty(r)) return 5;
            if (r.Contains("u+0000-00ff") || r.Contains("u+0020-007f")) return 20;
            if (r.Contains("u+0100-02af")) return 10;
            return 1;
        }

        private static List<GoogleFace> GoogleTtfFaces(string css)
        {
            var list = new List<GoogleFace>();
            var faceRe = new Regex(@"@font-face\s*\{([^}]*)\}",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            var ttfUrlInFace = new Regex(@"url\((https?://[^\)]+\.ttf)\)", RegexOptions.IgnoreCase);
            foreach (Match face in faceRe.Matches(css))
            {
                string body = face.Groups[1].Value;
                var u = ttfUrlInFace.Match(body);
                if (!u.Success) continue;
                list.Add(new GoogleFace
                {
                    Url = u.Groups[1].Value,
                    Weight = FontFaceWeight(body),
                    Italic = FontFaceItalic(body),
                    UnicodeRange = FontFaceUnicodeRange(body),
                });
            }
            if (list.Count > 0) return list;
            // Fallback: any ttf url in the whole CSS doc.
            foreach (Match m in Regex.Matches(css, @"url\((https?://[^\)]+\.ttf)\)", RegexOptions.IgnoreCase))
            {
                list.Add(new GoogleFace { Url = m.Groups[1].Value, Weight = 400, Italic = false });
            }
            return list;
        }

        private static bool HasCommonAxisCoverage(List<FontVariant> variants)
        {
            var keys = new HashSet<(int, bool)>();
            foreach (var v in variants) keys.Add((v.Weight, v.Italic));
            int[] expected = { 400, 500, 600, 700, 800, 900 };
            foreach (int w in expected)
            {
                if (!keys.Contains((w, false))) return false;
                if (!keys.Contains((w, true))) return false;
            }
            return true;
        }

        // ---------- Local-system fonts ----------

        // Aliases for Windows / installed fonts whose filename doesn't match
        // the family name (e.g. "Comic Sans MS" -> comic.ttf).
        private static readonly Dictionary<string, string[]> LocalFontAliases =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "arial", new[] { "arial" } },
            { "barlowcondensed", new[] { "barlowcondensed", "barlow" } },
            { "calibri", new[] { "calibri" } },
            { "cambria", new[] { "cambria" } },
            { "comic sans ms", new[] { "comic", "comicsansms" } },
            { "consolas", new[] { "consola", "consolas" } },
            { "courier new", new[] { "cour", "couriernew" } },
            { "georgia", new[] { "georgia" } },
            { "impact", new[] { "impact" } },
            { "inter", new[] { "inter" } },
            { "saira condensed", new[] { "sairacondensed", "saira" } },
            { "segoe ui", new[] { "segoeui", "segui", "segoe" } },
            { "tahoma", new[] { "tahoma" } },
            { "times new roman", new[] { "times", "timesnewroman" } },
            { "trebuchet ms", new[] { "trebuc", "trebuchetms" } },
            { "verdana", new[] { "verdana" } },
        };

        private static List<FontVariant> CopyLocalFontVariants(
            string family, string fontsDir, string projectSubdir, FontReport report,
            HashSet<(int weight, bool italic)> wanted)
        {
            var localFonts = FindLocalFontFiles(family);
            var variants = new List<FontVariant>();
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var localFont in localFonts)
            {
                var (weight, italic) = LocalFontVariant(localFont);
                if (wanted != null && !wanted.Contains((weight, italic))) continue;
                string targetName = PathBundler.UniqueName(
                    PathBundler.SafeFontName(family, Path.GetExtension(localFont), weight, italic),
                    usedNames);
                string written;
                try
                {
                    written = PathBundler.CopyFileSmart(localFont, fontsDir, targetName);
                }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                string relpath = projectSubdir + "/" + written;
                variants.Add(new FontVariant
                {
                    Path = relpath,
                    Weight = weight,
                    Italic = italic,
                    Source = "local",
                });
                report.FontsDownloaded.Add((family, relpath));
            }
            return variants;
        }

        private static List<string> FindLocalFontFiles(string family)
        {
            string[] prefixes = FontSearchPrefixes(family);
            var candidates = new List<string>();
            foreach (string dir in LocalFontDirs())
            {
                if (!Directory.Exists(dir)) continue;
                string[] files;
                try { files = Directory.GetFiles(dir); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                foreach (var file in files)
                {
                    string ext = Path.GetExtension(file).ToLowerInvariant();
                    if (ext != ".ttf" && ext != ".otf") continue;
                    string stem = PathBundler.NormaliseFontName(Path.GetFileNameWithoutExtension(file));
                    bool matches = false;
                    foreach (var prefix in prefixes)
                    {
                        if (stem.StartsWith(prefix, StringComparison.Ordinal) || stem.Contains(prefix))
                        {
                            matches = true;
                            break;
                        }
                    }
                    if (matches) candidates.Add(file);
                }
            }
            if (candidates.Count == 0) return candidates;
            candidates.Sort((a, b) => CompareLocalFontScore(b, a, prefixes)); // descending
            var selected = new Dictionary<(int weight, bool italic), string>();
            foreach (var candidate in candidates)
            {
                var key = LocalFontVariant(candidate);
                if (!selected.ContainsKey(key)) selected[key] = candidate;
            }
            return new List<string>(selected.Values);
        }

        private static int CompareLocalFontScore(string a, string b, string[] prefixes)
        {
            var sa = LocalFontScore(a, prefixes);
            var sb = LocalFontScore(b, prefixes);
            int cmp = sa.exact.CompareTo(sb.exact); if (cmp != 0) return cmp;
            cmp = sa.style.CompareTo(sb.style); if (cmp != 0) return cmp;
            return string.Compare(sa.name, sb.name, StringComparison.OrdinalIgnoreCase);
        }

        private static (int exact, int style, string name) LocalFontScore(string path, string[] prefixes)
        {
            string stem = PathBundler.NormaliseFontName(Path.GetFileNameWithoutExtension(path));
            int exact = 0;
            foreach (var p in prefixes) if (stem == p) { exact = 2; break; }
            if (exact == 0)
            {
                foreach (var p in prefixes)
                    if (stem.StartsWith(p, StringComparison.Ordinal)) { exact = 1; break; }
            }
            int style = 0;
            string[] heavyTokens = { "black", "heavy", "extrabold", "semibold", "bold", "bd" };
            foreach (var t in heavyTokens) if (stem.Contains(t)) { style += 10; break; }
            string[] italicTokens = { "italic", "oblique", "bi", "z" };
            foreach (var t in italicTokens) if (stem.Contains(t)) { style += 6; break; }
            if (Path.GetExtension(path).Equals(".ttf", StringComparison.OrdinalIgnoreCase)) style += 1;
            return (exact, style, Path.GetFileName(path).ToLowerInvariant());
        }

        private static (int weight, bool italic) LocalFontVariant(string path)
        {
            string stem = PathBundler.NormaliseFontName(Path.GetFileNameWithoutExtension(path));
            bool italic = stem.Contains("italic") || stem.Contains("oblique")
                          || stem.EndsWith("i", StringComparison.Ordinal)
                          || stem.EndsWith("bi", StringComparison.Ordinal)
                          || stem.EndsWith("z", StringComparison.Ordinal);
            int weight = -1;
            foreach (Match m in Regex.Matches(stem, @"(?<!\d)([1-9]00)(?!\d)"))
            {
                if (int.TryParse(m.Groups[1].Value, out int n) && n >= 100 && n <= 900 && n > weight)
                    weight = n;
            }
            if (weight < 0)
            {
                if (stem.Contains("black") || stem.Contains("heavy")) weight = 900;
                else if (stem.Contains("extrabold") || stem.Contains("xbold")) weight = 800;
                else if (stem.Contains("bold") || stem.Contains("semibold")
                    || stem.EndsWith("bd", StringComparison.Ordinal)
                    || stem.EndsWith("bi", StringComparison.Ordinal)
                    || stem.EndsWith("b", StringComparison.Ordinal))
                    weight = 700;
                else if (stem.Contains("medium") || stem.Contains("med")) weight = 500;
                else if (stem.Contains("light") || stem.Contains("thin")) weight = 300;
                else weight = 400;
            }
            return (weight, italic);
        }

        private static string[] FontSearchPrefixes(string family)
        {
            string key = (family ?? string.Empty).Trim().ToLowerInvariant();
            string normalized = PathBundler.NormaliseFontName(family);
            var aliases = LocalFontAliases.TryGetValue(key, out var aliasList) ? aliasList : Array.Empty<string>();
            var output = new List<string>();
            string normalisedNormal = PathBundler.NormaliseFontName(normalized);
            if (!string.IsNullOrEmpty(normalisedNormal)) output.Add(normalisedNormal);
            foreach (var alias in aliases)
            {
                string n = PathBundler.NormaliseFontName(alias);
                if (!string.IsNullOrEmpty(n) && !output.Contains(n)) output.Add(n);
            }
            return output.ToArray();
        }

        private static IEnumerable<string> LocalFontDirs()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string envDirs = Environment.GetEnvironmentVariable("H2U_FONT_DIRS");
            if (!string.IsNullOrEmpty(envDirs))
            {
                foreach (var raw in envDirs.Split(Path.PathSeparator))
                {
                    string trimmed = raw.Trim();
                    if (!string.IsNullOrEmpty(trimmed) && seen.Add(trimmed)) yield return trimmed;
                }
            }
            string windir = Environment.GetEnvironmentVariable("WINDIR")
                          ?? Environment.GetEnvironmentVariable("SystemRoot");
            if (!string.IsNullOrEmpty(windir))
            {
                string p = Path.Combine(windir, "Fonts");
                if (seen.Add(p)) yield return p;
            }
            string localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (!string.IsNullOrEmpty(localAppData))
            {
                string p = Path.Combine(localAppData, "Microsoft", "Windows", "Fonts");
                if (seen.Add(p)) yield return p;
            }
            string home = Environment.GetEnvironmentVariable("USERPROFILE")
                       ?? Environment.GetEnvironmentVariable("HOME")
                       ?? string.Empty;
            string[] homePaths = string.IsNullOrEmpty(home)
                ? Array.Empty<string>()
                : new[]
                {
                    Path.Combine(home, ".fonts"),
                    Path.Combine(home, ".local", "share", "fonts"),
                    Path.Combine(home, "Library", "Fonts"),
                };
            foreach (var p in homePaths)
                if (seen.Add(p)) yield return p;
            string[] systemPaths =
            {
                "/Library/Fonts",
                "/System/Library/Fonts",
                "/usr/share/fonts",
                "/usr/local/share/fonts",
            };
            foreach (var p in systemPaths)
                if (seen.Add(p)) yield return p;
        }

        // ---------- Helpers ----------

        // Synchronous HTTP download. We block on the task; this code runs in
        // the editor (importer / window) where async-await isn't worth the
        // complication and the call sites are already synchronous. Cancellation
        // is handled by the per-request CancellationTokenSource.
        public static byte[] HttpDownload(string url, TimeSpan timeout)
        {
            using (var cts = new CancellationTokenSource(timeout))
            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                req.Headers.UserAgent.Clear();
                req.Headers.UserAgent.ParseAdd("html2uxml/0.2");
                using (var resp = Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token)
                    .GetAwaiter().GetResult())
                {
                    resp.EnsureSuccessStatusCode();
                    return resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                }
            }
        }

        public static string HttpDownloadString(string url, TimeSpan timeout, string userAgent = null)
        {
            using (var cts = new CancellationTokenSource(timeout))
            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                if (!string.IsNullOrEmpty(userAgent))
                {
                    req.Headers.UserAgent.Clear();
                    req.Headers.UserAgent.ParseAdd(userAgent);
                }
                using (var resp = Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token)
                    .GetAwaiter().GetResult())
                {
                    resp.EnsureSuccessStatusCode();
                    return resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                }
            }
        }

        private static bool IsSubsetOf<T>(HashSet<T> needle, HashSet<T> haystack)
        {
            foreach (var item in needle) if (!haystack.Contains(item)) return false;
            return true;
        }

        private static string Truncate(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return s ?? string.Empty;
            return s.Length <= n ? s : s.Substring(0, n);
        }

        private static string StripWhitespace(string s)
        {
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
                if (!char.IsWhiteSpace(s[i])) sb.Append(s[i]);
            return sb.ToString();
        }
    }
}
