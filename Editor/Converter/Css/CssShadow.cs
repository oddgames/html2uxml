using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ODDGames.Html2Uxml.Editor.Converter.Css
{
    // Box-shadow parsing + emission. Mirrors mappings.py:
    //   _parse_box_shadow, _parse_shadow_layer, _shadow_record,
    //   _emit_shadow_props, _box_shadow_filter_call, _filter_number,
    //   _filter_shadow_radius, _filter_color, _map_box_shadow,
    //   plus the _ODDGAMES_FILTER_ASSET_ROOT custom-filter helpers.
    public static class CssShadow
    {
        public const string ODDGAMES_PACKAGE_ID = "au.com.oddgames.html2uxml";
        public static readonly string ODDGAMES_FILTER_ASSET_ROOT =
            "/Packages/" + ODDGAMES_PACKAGE_ID + "/Runtime/Filters";

        // Toggle to route single outer shadows to the custom box-shadow filter
        // asset. Disabled by default to match Python `_CUSTOM_FILTER_BRIDGES_ENABLED`.
        public const bool CUSTOM_FILTER_BRIDGES_ENABLED = false;

        static readonly Regex InsetRe   = new Regex(@"\binset\b", RegexOptions.IgnoreCase);
        static readonly Regex FuncWipe  = new Regex(@"(rgba?|hsla?)\s*\([^)]*\)", RegexOptions.IgnoreCase);
        static readonly Regex HexWipe   = new Regex(@"#[0-9a-fA-F]{3,8}\b");
        static readonly Regex InsetWipe = new Regex(@"\binset\b", RegexOptions.IgnoreCase);
        static readonly Regex NumRe     = new Regex(@"-?\d*\.?\d+(?:px|em|rem|%)?");

        public sealed class ShadowLayer
        {
            public bool Inset;
            public string Ox, Oy, Blur, Spread, Color;
        }

        public static (string ox, string oy, string blur, string color)? ParseBoxShadowFirstOuter(string value)
        {
            foreach (var layer in CssText.SplitTopLevelCommas(value))
            {
                var parsed = ParseShadowLayer(layer);
                if (parsed != null && !parsed.Inset)
                    return (parsed.Ox, parsed.Oy, parsed.Blur, parsed.Color);
            }
            return null;
        }

        public static ShadowLayer ParseShadowLayer(string layer)
        {
            if (string.IsNullOrEmpty(layer)) return null;
            string color = CssColor.ExtractColor(layer);
            if (color == null) return null;
            bool inset = InsetRe.IsMatch(layer);
            string scrubbed = FuncWipe.Replace(layer, " ");
            scrubbed = HexWipe.Replace(scrubbed, " ");
            scrubbed = InsetWipe.Replace(scrubbed, " ");
            var nums = new List<string>();
            foreach (Match m in NumRe.Matches(scrubbed))
                if (!string.IsNullOrWhiteSpace(m.Value)) nums.Add(m.Value);
            if (nums.Count < 2) return null;
            return new ShadowLayer
            {
                Inset = inset,
                Ox = CssText.StripUnit(nums[0]),
                Oy = CssText.StripUnit(nums[1]),
                Blur = nums.Count >= 3 ? CssText.StripUnit(nums[2]) : "0",
                Spread = nums.Count >= 4 ? CssText.StripUnit(nums[3]) : "0",
                Color = color,
            };
        }

        public static string ShadowRecord(string kind, string ox, string oy, string blur, string spread, string color)
            => string.Join("|", kind, ox, oy, blur, spread, color);

        public static List<KeyValuePair<string, string>> EmitShadowProps(List<string> records)
        {
            var outDecls = new List<KeyValuePair<string, string>>();
            if (records == null || records.Count == 0) return outDecls;
            outDecls.Add(new KeyValuePair<string, string>("--odd-box-shadows", CssText.QuoteForUss(string.Join(";", records))));

            foreach (var record in records)
            {
                var parts = record.Split(new[] { '|' }, 6);
                if (parts.Length < 6) continue;
                if (parts[0] == "outset")
                {
                    outDecls.Add(new KeyValuePair<string, string>("--odd-shadow-offset-x", parts[1]));
                    outDecls.Add(new KeyValuePair<string, string>("--odd-shadow-offset-y", parts[2]));
                    outDecls.Add(new KeyValuePair<string, string>("--odd-shadow-blur",     parts[3]));
                    outDecls.Add(new KeyValuePair<string, string>("--odd-shadow-color",    parts[5]));
                    break;
                }
            }
            foreach (var record in records)
            {
                var parts = record.Split(new[] { '|' }, 6);
                if (parts.Length < 6) continue;
                if (parts[0] == "inset")
                {
                    outDecls.Add(new KeyValuePair<string, string>("--odd-inner-shadow-offset-x", parts[1]));
                    outDecls.Add(new KeyValuePair<string, string>("--odd-inner-shadow-offset-y", parts[2]));
                    outDecls.Add(new KeyValuePair<string, string>("--odd-inner-shadow-blur",     parts[3]));
                    outDecls.Add(new KeyValuePair<string, string>("--odd-inner-shadow-spread",   parts[4]));
                    outDecls.Add(new KeyValuePair<string, string>("--odd-inner-shadow-color",    parts[5]));
                    break;
                }
            }
            return outDecls;
        }

        public static List<KeyValuePair<string, string>> MapBoxShadow(string value, List<string> warnings)
        {
            bool sawSupported = false;
            int skippedLayers = 0;
            var records = new List<string>();
            var parsedLayers = new List<ShadowLayer>();
            foreach (var layer in CssText.SplitTopLevelCommas(value))
            {
                var parsed = ParseShadowLayer(layer);
                if (parsed == null) { skippedLayers++; continue; }
                if (parsedLayers.Count >= 8) { skippedLayers++; continue; }
                parsedLayers.Add(parsed);
                records.Add(ShadowRecord(parsed.Inset ? "inset" : "outset",
                    parsed.Ox, parsed.Oy, parsed.Blur, parsed.Spread, parsed.Color));
                sawSupported = true;
            }
            if (skippedLayers > 0)
                warnings.Add($"box-shadow: {value} -- additional/complex shadow layers ignored after 8 supported layers");
            if (!sawSupported)
            {
                warnings.Add($"box-shadow: {value} -- complex shadow not bridged");
                return null;
            }
            if (CUSTOM_FILTER_BRIDGES_ENABLED && parsedLayers.Count == 1 && !parsedLayers[0].Inset)
            {
                var p = parsedLayers[0];
                warnings.Add(
                    "box-shadow uses ODDGames package custom filter asset; install "
                  + "au.com.oddgames.html2uxml before loading the USS");
                return new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>("filter",
                        BoxShadowFilterCall(p.Ox, p.Oy, p.Blur, p.Spread, p.Color)),
                };
            }
            warnings.Add("box-shadow uses Html2UxmlPanel fallback");
            return EmitShadowProps(records);
        }

        public static string BoxShadowFilterCall(string ox, string oy, string blur, string spread, string color)
        {
            string radius = FilterShadowRadius(blur, spread);
            return CustomFilterCall("ODDGamesBoxShadow",
                FilterNumber(ox), FilterNumber(oy), radius, FilterColor(color));
        }

        public static string CustomFilterCall(string assetName, params string[] args)
        {
            string suffix = args.Length > 0 ? " " + string.Join(" ", args) : "";
            return $"filter(\"{ODDGamesFilterAsset(assetName)}\"{suffix})";
        }

        public static string ODDGamesFilterAsset(string name)
            => $"{ODDGAMES_FILTER_ASSET_ROOT}/{name}.asset";

        public static string FilterNumber(string value)
        {
            string raw = (value ?? "").Trim().ToLowerInvariant();
            if (raw.EndsWith("px")) raw = raw.Substring(0, raw.Length - 2);
            return raw.Length == 0 ? "0" : raw;
        }

        public static string FilterShadowRadius(string blur, string spread)
        {
            if (!double.TryParse(FilterNumber(blur), NumberStyles.Float, CultureInfo.InvariantCulture, out var b)
                || !double.TryParse(FilterNumber(spread), NumberStyles.Float, CultureInfo.InvariantCulture, out var s))
            {
                return FilterNumber(blur);
            }
            return System.Math.Max(0.0, b + System.Math.Max(0.0, s)).ToString("g", CultureInfo.InvariantCulture);
        }

        public static string FilterColor(string value)
            => CssColor.CssColorToHex(value) ?? value;
    }
}
