using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ODDGames.Html2Uxml.Editor.Converter.Css
{
    // CSS transform parsing (translate / rotate / scale / matrix). Mirrors:
    //   _split_transform, _split_transform_args, _parse_transform_matrix,
    //   _format_matrix_*
    public static class CssTransform
    {
        static readonly Regex FnRe = new Regex(@"([a-zA-Z][a-zA-Z0-9-]*)\s*\(([^)]*)\)");

        public static List<string> SplitTransformArgs(string args)
        {
            var commaParts = CssText.SplitTopLevelCommas(args);
            if (commaParts.Count > 1) return commaParts;
            var result = new List<string>();
            foreach (var p in Regex.Split((args ?? "").Trim(), @"\s+"))
                if (!string.IsNullOrEmpty(p)) result.Add(p);
            return result;
        }

        public static List<KeyValuePair<string, string>> SplitTransform(string value, List<string> warnings)
        {
            string[] outTranslate = { "0", "0" };
            string outRotate = null;
            bool hasTranslate = false;
            string scaleX = "1", scaleY = "1";
            bool hasScale = false;
            bool forceScalePair = false;

            foreach (Match m in FnRe.Matches(value ?? ""))
            {
                string fn = m.Groups[1].Value.ToLowerInvariant();
                var parts = SplitTransformArgs(m.Groups[2].Value);
                switch (fn)
                {
                    case "translatex":
                        if (parts.Count > 0) { outTranslate[0] = parts[0]; hasTranslate = true; }
                        break;
                    case "translatey":
                        if (parts.Count > 0) { outTranslate[1] = parts[0]; hasTranslate = true; }
                        break;
                    case "translate":
                        if (parts.Count > 0) outTranslate[0] = parts[0];
                        if (parts.Count > 1) outTranslate[1] = parts[1];
                        hasTranslate = true;
                        break;
                    case "translate3d":
                        if (parts.Count >= 2) { outTranslate[0] = parts[0]; outTranslate[1] = parts[1]; hasTranslate = true; }
                        warnings.Add("transform function translate3d() flattened to 2D; z component ignored");
                        break;
                    case "rotate":
                    case "rotatez":
                        if (parts.Count > 0) outRotate = parts[0];
                        break;
                    case "scale":
                        if (parts.Count == 1) { scaleX = parts[0]; scaleY = parts[0]; hasScale = true; }
                        else if (parts.Count >= 2) { scaleX = parts[0]; scaleY = parts[1]; hasScale = true; forceScalePair = true; }
                        break;
                    case "scalex":
                        if (parts.Count > 0) { scaleX = parts[0]; hasScale = true; forceScalePair = true; }
                        break;
                    case "scaley":
                        if (parts.Count > 0) { scaleY = parts[0]; hasScale = true; forceScalePair = true; }
                        break;
                    case "matrix":
                        var matrix = ParseTransformMatrix(parts);
                        if (matrix == null)
                        {
                            warnings.Add("transform function matrix() not supported in USS, ignored");
                            continue;
                        }
                        var (a, b, c, d, e, f) = matrix.Value;
                        if (!CssLength.NearZero(e) || !CssLength.NearZero(f))
                        {
                            outTranslate[0] = FormatMatrixPx(e);
                            outTranslate[1] = FormatMatrixPx(f);
                            hasTranslate = true;
                        }
                        double sx = System.Math.Sqrt(a * a + b * b);
                        double sy = System.Math.Sqrt(c * c + d * d);
                        double shear = a * c + b * d;
                        if (sx > 0.00001 && sy > 0.00001 && System.Math.Abs(shear) <= 0.0001)
                        {
                            double angle = System.Math.Atan2(b, a) * 180.0 / System.Math.PI;
                            if (!CssLength.NearZero(angle))
                                outRotate = FormatMatrixDegrees(angle);
                            double det = a * d - b * c;
                            if (det < 0 && !(CssLength.NearZero(b) && CssLength.NearZero(c)))
                            {
                                warnings.Add("transform matrix() reflection/skew approximated; translate preserved");
                            }
                            else
                            {
                                if (CssLength.NearZero(b) && CssLength.NearZero(c)) { sx = a; sy = d; }
                                if (!CssLength.Near(sx, 1.0) || !CssLength.Near(sy, 1.0))
                                {
                                    scaleX = FormatMatrixScalar(sx);
                                    scaleY = FormatMatrixScalar(sy);
                                    hasScale = true;
                                    forceScalePair = !CssLength.Near(sx, sy);
                                }
                            }
                        }
                        else if (!(CssLength.Near(a, 1.0) && CssLength.NearZero(b) && CssLength.NearZero(c) && CssLength.Near(d, 1.0)))
                        {
                            warnings.Add("transform matrix() with skew could not be represented in USS; translate preserved");
                        }
                        break;
                    case "rotatex":
                    case "rotatey":
                    case "translatez":
                    case "skew":
                    case "skewx":
                    case "skewy":
                    case "perspective":
                    case "matrix3d":
                        warnings.Add($"transform function {fn}() not supported in USS, ignored");
                        break;
                }
            }

            var pairs = new List<KeyValuePair<string, string>>();
            if (hasTranslate) pairs.Add(new KeyValuePair<string, string>("translate", $"{outTranslate[0]} {outTranslate[1]}"));
            if (outRotate != null) pairs.Add(new KeyValuePair<string, string>("rotate", outRotate));
            if (hasScale)
            {
                pairs.Add(scaleX == scaleY && !forceScalePair
                    ? new KeyValuePair<string, string>("scale", scaleX)
                    : new KeyValuePair<string, string>("scale", $"{scaleX} {scaleY}"));
            }
            return pairs.Count > 0 ? pairs : null;
        }

        public static (double a, double b, double c, double d, double e, double f)? ParseTransformMatrix(List<string> parts)
        {
            if (parts.Count != 6) return null;
            var p = new double[6];
            for (int i = 0; i < 6; i++)
            {
                string raw = parts[i].Trim().ToLowerInvariant();
                if (raw.EndsWith("px")) raw = raw.Substring(0, raw.Length - 2).Trim();
                if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return null;
                p[i] = v;
            }
            return (p[0], p[1], p[2], p[3], p[4], p[5]);
        }

        public static string FormatMatrixScalar(double v)
        {
            if (CssLength.NearZero(v)) v = 0.0;
            return v.ToString("g", CultureInfo.InvariantCulture);
        }

        public static string FormatMatrixPx(double v)
        {
            if (CssLength.NearZero(v)) v = 0.0;
            return v.ToString("g", CultureInfo.InvariantCulture) + "px";
        }

        public static string FormatMatrixDegrees(double v)
        {
            if (CssLength.NearZero(v)) v = 0.0;
            return v.ToString("g", CultureInfo.InvariantCulture) + "deg";
        }
    }
}
