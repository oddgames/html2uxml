// SPDX-License-Identifier: MIT
//
// PathBundler — relative path / data-URI resolution for url(...) refs.
//
// Mirrors the helpers in html2uxml/assets.py:
//   _safe_name, _decode_image_data_uri, _detect_image_extension,
//   _asset_ref_to_relative_path, _truncate_for_report, _unique_name,
//   _normalise_font_name, _safe_font_name, _font_suffix_from_url,
//   _write_bytes_smart (renamed WriteBytesSmart).
//
// All operations are pure (no Unity, no network).

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ODDGames.Html2Uxml.Editor.Converter.Assets
{
    public static class PathBundler
    {
        // url("..."), url('...'), url(...).
        public static readonly Regex UrlRegex = new Regex(
            @"url\(\s*(?:""([^""]*)""|'([^']*)'|([^)\s]+))\s*\)",
            RegexOptions.Compiled);

        // Generic font-family declarations to skip.
        public static readonly HashSet<string> GenericFontFamilies = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "serif", "sans-serif", "monospace", "cursive", "fantasy",
            "system-ui", "ui-serif", "ui-sans-serif", "ui-monospace",
            "ui-rounded", "math", "emoji", "fangsong",
        };

        // Unity built-in image importer file extensions.
        public static readonly HashSet<string> UnityImageExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".bmp", ".tga", ".tif", ".tiff",
            ".exr", ".hdr", ".gif", ".psd", ".pict", ".pic", ".iff",
        };

        // Image formats Unity rejects.
        public static readonly HashSet<string> NeedsTranscodeExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".webp", ".avif", ".jxl", ".heic", ".heif",
        };

        private static readonly Regex NameRe = new Regex(@"[^A-Za-z0-9._-]+", RegexOptions.Compiled);

        // ^data:(image/...);base64?,payload
        private static readonly Regex DataUriRe = new Regex(
            @"^data:(image/[A-Za-z0-9+\-.]+)\s*(;base64)?\s*,(.*)$",
            RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

        private static readonly Dictionary<string, string> DataUriExtensions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "image/png", ".png" },
            { "image/jpeg", ".jpg" },
            { "image/jpg", ".jpg" },
            { "image/gif", ".gif" },
            { "image/webp", ".webp" },
            { "image/bmp", ".bmp" },
            { "image/svg+xml", ".svg" },
            { "image/x-icon", ".ico" },
            { "image/vnd.microsoft.icon", ".ico" },
        };

        public static string SafeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "asset";
            string cleaned = NameRe.Replace(name, "_").Trim('.', '_');
            return string.IsNullOrEmpty(cleaned) ? "asset" : cleaned;
        }

        public static string TruncateForReport(string value, int limit = 80)
        {
            if (string.IsNullOrEmpty(value)) return value ?? string.Empty;
            return value.Length <= limit ? value : value.Substring(0, limit) + "...";
        }

        // Pulls the Path component off a url-like reference whose project_subdir
        // prefix matches `projectSubdir`. Returns null when the prefix doesn't
        // line up — i.e. the ref isn't already inside our images bundle.
        public static string AssetRefToRelativePath(string reference, string projectSubdir)
        {
            if (string.IsNullOrEmpty(reference) || string.IsNullOrEmpty(projectSubdir))
                return null;
            string normalized = reference.Replace('\\', '/').TrimStart('/');
            string prefix = projectSubdir.Replace('\\', '/').Trim('/');
            string needle = prefix + "/";
            if (!normalized.StartsWith(needle, StringComparison.Ordinal))
                return null;
            return normalized.Substring(needle.Length);
        }

        // Decode a `data:image/*` URI to (bytes, extension). Returns null when
        // the URI is malformed or the MIME isn't one we recognise.
        public static (byte[] data, string ext)? DecodeImageDataUri(string uri)
        {
            if (string.IsNullOrEmpty(uri)) return null;
            var m = DataUriRe.Match(uri);
            if (!m.Success) return null;
            string mime = m.Groups[1].Value.ToLowerInvariant();
            bool isBase64 = m.Groups[2].Success;
            string payload = m.Groups[3].Value;
            byte[] data;
            try
            {
                if (isBase64)
                {
                    data = Convert.FromBase64String(StripBase64Whitespace(payload));
                }
                else
                {
                    data = UrlUnquoteToBytes(payload);
                }
            }
            catch (Exception)
            {
                return null;
            }
            string ext;
            if (!DataUriExtensions.TryGetValue(mime, out ext))
            {
                int slash = mime.IndexOf('/');
                string subtype = slash >= 0 ? mime.Substring(slash + 1) : "bin";
                subtype = Regex.Replace(subtype, "[^A-Za-z0-9]+", string.Empty);
                if (subtype.Length > 8) subtype = subtype.Substring(0, 8);
                ext = string.IsNullOrEmpty(subtype) ? ".bin" : "." + subtype;
            }
            return (data, ext);
        }

        // Sniff the leading bytes of an image to override the URL-implied
        // extension when the server hands back a different format.
        public static string DetectImageExtension(byte[] data, string fallbackName)
        {
            if (data != null)
            {
                if (data.Length >= 12 && data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F'
                    && data[8] == 'W' && data[9] == 'E' && data[10] == 'B' && data[11] == 'P')
                    return ".webp";
                if (data.Length >= 8 && data[0] == 0x89 && data[1] == 'P' && data[2] == 'N' && data[3] == 'G'
                    && data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A)
                    return ".png";
                if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
                    return ".jpg";
                if (data.Length >= 4 && data[0] == 'G' && data[1] == 'I' && data[2] == 'F' && data[3] == '8')
                    return ".gif";
                if (data.Length >= 2 && data[0] == 'B' && data[1] == 'M')
                    return ".bmp";
                if (data.Length >= 12 && data[4] == 'f' && data[5] == 't' && data[6] == 'y' && data[7] == 'p'
                    && (Match4(data, 8, "avif") || Match4(data, 8, "avis")))
                    return ".avif";
                if (data.Length >= 12 && data[0] == 0 && data[1] == 0 && data[2] == 0 && data[3] == 0x0C
                    && data[4] == 'J' && data[5] == 'X' && data[6] == 'L' && data[7] == ' ')
                    return ".jxl";
            }
            string suffix = string.IsNullOrEmpty(fallbackName)
                ? string.Empty
                : Path.GetExtension(fallbackName);
            return suffix?.ToLowerInvariant() ?? string.Empty;
        }

        private static bool Match4(byte[] data, int offset, string ascii)
        {
            for (int i = 0; i < 4; i++)
                if ((char)data[offset + i] != ascii[i]) return false;
            return true;
        }

        // Image-only sniff for decoding PNG/JPEG/GIF dimensions. Returns null
        // when the format is unknown or the file is corrupt. Mirrors
        // _image_dimensions in assets.py — used by AssetWriter to inject
        // aspect-ratio fallbacks beside background-image rules.
        public static (int width, int height)? SniffImageDimensions(string path)
        {
            try
            {
                using (var fs = File.OpenRead(path))
                {
                    byte[] head = new byte[32];
                    int read = fs.Read(head, 0, head.Length);
                    if (read >= 24 && head[0] == 0x89 && head[1] == 'P' && head[2] == 'N' && head[3] == 'G')
                    {
                        int w = (head[16] << 24) | (head[17] << 16) | (head[18] << 8) | head[19];
                        int h = (head[20] << 24) | (head[21] << 16) | (head[22] << 8) | head[23];
                        return (w, h);
                    }
                    if (read >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF)
                    {
                        return JpegDimensions(path);
                    }
                    if (read >= 10
                        && head[0] == 'G' && head[1] == 'I' && head[2] == 'F'
                        && head[3] == '8' && (head[4] == '7' || head[4] == '9') && head[5] == 'a')
                    {
                        int w = head[6] | (head[7] << 8);
                        int h = head[8] | (head[9] << 8);
                        return (w, h);
                    }
                }
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            return null;
        }

        private static (int, int)? JpegDimensions(string path)
        {
            try
            {
                using (var fs = File.OpenRead(path))
                {
                    if (fs.ReadByte() != 0xFF || fs.ReadByte() != 0xD8) return null;
                    while (true)
                    {
                        int b = fs.ReadByte();
                        if (b != 0xFF) return null;
                        int marker = fs.ReadByte();
                        while (marker == 0xFF) marker = fs.ReadByte();
                        if (marker < 0) return null;
                        if (marker == 0xD8 || marker == 0xD9) continue;
                        int hi = fs.ReadByte();
                        int lo = fs.ReadByte();
                        if (lo < 0) return null;
                        int segLen = (hi << 8) | lo;
                        if (segLen < 2) return null;
                        bool isSof = (marker >= 0xC0 && marker <= 0xC3)
                                  || (marker >= 0xC5 && marker <= 0xC7)
                                  || (marker >= 0xC9 && marker <= 0xCB)
                                  || (marker >= 0xCD && marker <= 0xCF);
                        if (isSof)
                        {
                            byte[] sof = new byte[5];
                            int r = fs.Read(sof, 0, 5);
                            if (r != 5) return null;
                            int h = (sof[1] << 8) | sof[2];
                            int w = (sof[3] << 8) | sof[4];
                            return (w, h);
                        }
                        fs.Seek(segLen - 2, SeekOrigin.Current);
                    }
                }
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        // Write `data` into `destDir/targetName`. If a file already exists with
        // identical bytes, return its name (no churn). On collision, append
        // -2/-3/... suffixes so existing references aren't silently rerouted to
        // different content. When `transcoder` is supplied and recognises the
        // payload, it transparently transcodes (e.g. WebP→PNG) and the suffix
        // is rewritten before disambiguation.
        public static string WriteBytesSmart(
            string destDir,
            string targetName,
            byte[] data,
            Func<byte[], string, (byte[] outBytes, string outExt)?> transcoder = null,
            List<string> warnings = null)
        {
            Directory.CreateDirectory(destDir);
            string safe = SafeName(targetName);
            string detectedExt = DetectImageExtension(data, safe);
            (byte[] outBytes, string outExt)? transcoded = null;
            try { transcoded = transcoder?.Invoke(data, detectedExt); }
            catch (Exception e)
            {
                warnings?.Add("transcoder failed for " + safe + ": " + e.Message);
            }
            if (transcoded.HasValue)
            {
                data = transcoded.Value.outBytes;
                safe = Path.GetFileNameWithoutExtension(safe) + transcoded.Value.outExt;
            }
            else if (!string.IsNullOrEmpty(detectedExt)
                && !string.Equals(detectedExt, Path.GetExtension(safe), StringComparison.OrdinalIgnoreCase))
            {
                safe = Path.GetFileNameWithoutExtension(safe) + detectedExt;
            }
            string stem = Path.GetFileNameWithoutExtension(safe);
            string suffix = Path.GetExtension(safe);
            string candidate = safe;
            int i = 2;
            while (true)
            {
                string target = Path.Combine(destDir, candidate);
                if (!File.Exists(target))
                {
                    File.WriteAllBytes(target, data);
                    return candidate;
                }
                try
                {
                    byte[] existing = File.ReadAllBytes(target);
                    if (BytesEqual(existing, data))
                        return candidate;
                }
                catch (IOException) { /* fall through to rename */ }
                catch (UnauthorizedAccessException) { /* fall through to rename */ }
                candidate = stem + "-" + i + suffix;
                i++;
            }
        }

        public static string CopyFileSmart(string sourcePath, string destDir, string targetName)
        {
            byte[] data = File.ReadAllBytes(sourcePath);
            return WriteBytesSmart(destDir, targetName, data);
        }

        // Strip whitespace (spaces, newlines) from base64 payloads before
        // Convert.FromBase64String, which is stricter than Python's b64decode.
        private static string StripBase64Whitespace(string payload)
        {
            var sb = new StringBuilder(payload.Length);
            for (int i = 0; i < payload.Length; i++)
            {
                char c = payload[i];
                if (!char.IsWhiteSpace(c)) sb.Append(c);
            }
            return sb.ToString();
        }

        // urllib.parse.unquote_to_bytes equivalent — decode percent-escaped
        // sequences in a non-base64 data: payload to raw bytes.
        public static byte[] UrlUnquoteToBytes(string payload)
        {
            if (string.IsNullOrEmpty(payload)) return Array.Empty<byte>();
            var bytes = new List<byte>(payload.Length);
            for (int i = 0; i < payload.Length;)
            {
                char c = payload[i];
                if (c == '%' && i + 2 < payload.Length
                    && IsHex(payload[i + 1]) && IsHex(payload[i + 2]))
                {
                    int hi = HexVal(payload[i + 1]);
                    int lo = HexVal(payload[i + 2]);
                    bytes.Add((byte)((hi << 4) | lo));
                    i += 3;
                }
                else
                {
                    bytes.Add((byte)c);
                    i++;
                }
            }
            return bytes.ToArray();
        }

        private static bool IsHex(char c)
            => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

        private static int HexVal(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return 10 + (c - 'a');
            return 10 + (c - 'A');
        }

        public static string Sha1Hex(byte[] data, int truncate = 12)
        {
            using (var sha = SHA1.Create())
            {
                byte[] hash = sha.ComputeHash(data);
                var sb = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("x2"));
                string hex = sb.ToString();
                return truncate > 0 && truncate < hex.Length ? hex.Substring(0, truncate) : hex;
            }
        }

        public static string Sha1HexUtf8(string s)
        {
            using (var sha = SHA1.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(s ?? string.Empty));
                var sb = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
        }

        // Strip everything but a-z0-9 — used for fuzzy font-family lookup.
        public static string NormaliseFontName(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9]+", string.Empty);
        }

        public static string SafeFontName(string family, string suffix, int? weight = null, bool italic = false)
        {
            if (string.IsNullOrEmpty(suffix)) suffix = ".ttf";
            string lower = suffix.ToLowerInvariant();
            if (lower != ".ttf" && lower != ".otf") suffix = ".ttf";
            var sb = new StringBuilder(family ?? string.Empty);
            if (weight.HasValue) sb.Append('-').Append(weight.Value);
            if (italic) sb.Append("-Italic");
            sb.Append(suffix);
            return SafeName(sb.ToString());
        }

        public static string FontSuffixFromUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return ".ttf";
            int q = url.IndexOf('?');
            string trimmed = q >= 0 ? url.Substring(0, q) : url;
            string suffix = Path.GetExtension(trimmed)?.ToLowerInvariant();
            return (suffix == ".ttf" || suffix == ".otf") ? suffix : ".ttf";
        }

        public static string UniqueName(string name, HashSet<string> usedNames)
        {
            string baseName = SafeName(name);
            string candidate = baseName;
            int i = 2;
            while (usedNames.Contains(candidate.ToLowerInvariant()))
            {
                string stem = Path.GetFileNameWithoutExtension(baseName);
                string suffix = Path.GetExtension(baseName);
                candidate = stem + "-" + i + suffix;
                i++;
            }
            usedNames.Add(candidate.ToLowerInvariant());
            return candidate;
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }
    }
}
