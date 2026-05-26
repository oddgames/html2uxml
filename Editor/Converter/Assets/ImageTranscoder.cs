// SPDX-License-Identifier: MIT
//
// ImageTranscoder — best-effort WebP -> PNG.
//
// Two build targets share this file:
//   1. Unity Editor build (UNITY_5_3_OR_NEWER) uses Texture2D.LoadImage,
//      identical to the original behaviour. Tracks Python's
//      assets.py::_maybe_transcode_to_png best-effort approach: try the
//      built-in path, fall through quietly if Unity refuses the bytes.
//   2. Server build (netstandard2.1 via Server/Html2Uxml.Converter.csproj)
//      uses SixLabors.ImageSharp so the container can run without Unity.
//
// AVIF / JXL / HEIC are deferred in both paths — Unity has no decoder
// and ImageSharp's default codecs don't cover them either.

using System;
#if !UNITY_5_3_OR_NEWER
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
#endif

namespace ODDGames.Html2Uxml.Editor.Converter.Assets
{
    public static class ImageTranscoder
    {
        // Returns (newBytes, ".png") on success, null when the input is not a
        // format we want to transcode or the decoder refused it.
        public static (byte[] bytes, string ext)? MaybeTranscodeToPng(byte[] data, string ext)
        {
            if (data == null || data.Length == 0) return null;
            if (string.IsNullOrEmpty(ext)) return null;
            if (!PathBundler.NeedsTranscodeExts.Contains(ext)) return null;

            string lower = ext.ToLowerInvariant();
            if (lower != ".webp")
                return null;

#if UNITY_5_3_OR_NEWER
            return TranscodeUnity(data);
#else
            return TranscodeImageSharp(data);
#endif
        }

#if UNITY_5_3_OR_NEWER
        static (byte[] bytes, string ext)? TranscodeUnity(byte[] data)
        {
            UnityEngine.Texture2D tex = null;
            try
            {
                tex = new UnityEngine.Texture2D(2, 2, UnityEngine.TextureFormat.RGBA32, false);
                bool loaded = UnityEngine.ImageConversion.LoadImage(tex, data, markNonReadable: false);
                if (!loaded || tex.width <= 0 || tex.height <= 0)
                    return null;
                byte[] png = UnityEngine.ImageConversion.EncodeToPNG(tex);
                if (png == null || png.Length == 0)
                    return null;
                return (png, ".png");
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                if (tex != null)
                {
                    if (UnityEngine.Application.isPlaying)
                        UnityEngine.Object.Destroy(tex);
                    else
                        UnityEngine.Object.DestroyImmediate(tex);
                }
            }
        }
#else
        static (byte[] bytes, string ext)? TranscodeImageSharp(byte[] data)
        {
            try
            {
                using var image = Image.Load(data);
                using var ms = new System.IO.MemoryStream();
                image.Save(ms, new PngEncoder());
                byte[] png = ms.ToArray();
                if (png.Length == 0) return null;
                return (png, ".png");
            }
            catch (Exception)
            {
                return null;
            }
        }
#endif
    }
}
