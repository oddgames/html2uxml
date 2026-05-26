using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace ODDGames.Html2Uxml.Editor
{
    // Per-class layout diff between an HTML fixture (via AngleSharp.Css
    // resolved cascade) and the converter's UXML render (via a hidden
    // EditorWindow + UI Toolkit's real layout). Outputs a sorted report
    // surfacing classes whose width / height drift the most.
    //
    // Limitations:
    //   * HTML side comes from CSS resolution, not Chrome layout — `auto`
    //     widths show as NaN. For absolute-positioned widgets the values
    //     line up with what a browser would compute.
    //   * UXML side is the resolvedStyle after the panel lays out at the
    //     specified viewport. The first non-`unity-` / non-`h2u-*` class
    //     on the element keys the bucket; if two elements share a class
    //     the report averages them.
    public static class Html2UxmlLayoutCompare
    {
        // Menu entry — prompts for the HTML/UXML pair and dumps the
        // sorted top-30 per-class size diff into the Editor console.
        [MenuItem("Tools/Html2Uxml/Layout compare…")]
        static void MenuCompare()
        {
            string htmlPath = EditorUtility.OpenFilePanel(
                "Source HTML",
                Application.dataPath,
                "html");
            if (string.IsNullOrEmpty(htmlPath)) return;

            string uxmlPath = EditorUtility.OpenFilePanel(
                "Generated UXML",
                Path.GetDirectoryName(htmlPath),
                "uxml");
            if (string.IsNullOrEmpty(uxmlPath)) return;

            // Convert absolute UXML path to project-relative for AssetDatabase.
            string projRoot = Path.GetDirectoryName(Application.dataPath).Replace('\\', '/');
            string norm = uxmlPath.Replace('\\', '/');
            string rel = norm.StartsWith(projRoot + "/", StringComparison.OrdinalIgnoreCase)
                ? norm.Substring(projRoot.Length + 1)
                : norm;

            try
            {
                var diffs = Compare(htmlPath, rel);
                string report = FormatReport(diffs, 30);
                UnityEngine.Debug.Log("[html2uxml layout-compare]\n" + report);
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogException(e);
            }
        }


        public sealed class Sample
        {
            public int Count;
            public double W, H;
        }

        public sealed class Diff
        {
            public string Class;
            public Sample Html;
            public Sample Uxml;
            public double Score; // |Δw| + |Δh|, sort key
        }

        public static List<Diff> Compare(
            string htmlPath, string uxmlPath,
            int viewportWidth = 1438, int viewportHeight = 788)
        {
            if (!File.Exists(htmlPath))
                throw new FileNotFoundException("HTML not found", htmlPath);
            if (string.IsNullOrEmpty(uxmlPath))
                throw new ArgumentException("uxml path required");

            // Prefer real Chrome layout (CDP via System.Net.WebSockets) so
            // we get true getBoundingClientRect numbers, including elements
            // whose width / height are `auto` or percentage-based. Fall
            // back to the AngleSharp.Css cascade dump if Chrome can't be
            // located or refuses to start — that path returns NaN for
            // anything CSS leaves unresolved.
            var htmlLayout = new Dictionary<string, Html2UxmlCssInliner.ClassLayout>(
                StringComparer.Ordinal);
            bool gotChrome = false;
            try
            {
                var chromeDump = Html2UxmlChromeLayoutDump.Dump(
                    Path.GetFullPath(htmlPath), viewportWidth, viewportHeight);
                foreach (var kv in chromeDump)
                {
                    htmlLayout[kv.Key] = new Html2UxmlCssInliner.ClassLayout
                    {
                        Count = kv.Value.Count,
                        W = kv.Value.W,
                        H = kv.Value.H,
                    };
                }
                gotChrome = htmlLayout.Count > 0;
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning(
                    "[html2uxml] Chrome layout dump failed, falling back to AngleSharp.Css: " + e.Message);
            }

            if (!gotChrome)
            {
                string htmlText = File.ReadAllText(htmlPath);
                string baseDir = Path.GetDirectoryName(Path.GetFullPath(htmlPath));
                htmlLayout = Html2UxmlCssInliner.DumpLayoutByClass(
                    htmlText, baseDir, viewportWidth, viewportHeight);
            }

            var uxmlLayout = DumpUxmlLayout(uxmlPath, viewportWidth, viewportHeight);

            var allClasses = new HashSet<string>(StringComparer.Ordinal);
            foreach (var k in htmlLayout.Keys) allClasses.Add(k);
            foreach (var k in uxmlLayout.Keys) allClasses.Add(k);

            var diffs = new List<Diff>();
            foreach (string cls in allClasses)
            {
                Sample h = null, u = null;
                if (htmlLayout.TryGetValue(cls, out var hc))
                    h = new Sample { Count = hc.Count, W = hc.W, H = hc.H };
                if (uxmlLayout.TryGetValue(cls, out var us))
                    u = us;
                double dw = Math.Abs(ZeroIfNaN(h?.W) - ZeroIfNaN(u?.W));
                double dh = Math.Abs(ZeroIfNaN(h?.H) - ZeroIfNaN(u?.H));
                diffs.Add(new Diff { Class = cls, Html = h, Uxml = u, Score = dw + dh });
            }
            diffs.Sort((a, b) => b.Score.CompareTo(a.Score));
            return diffs;
        }

        public static string FormatReport(List<Diff> diffs, int top = 30)
        {
            var sb = new StringBuilder();
            sb.AppendLine(
                $"{"score",6} {"class",-40} {"html(w,h)",-20} {"uxml(w,h)",-20} h# u#");
            for (int i = 0; i < Math.Min(top, diffs.Count); i++)
            {
                var d = diffs[i];
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "{0,6:0} {1,-40} {2,-20} {3,-20} {4,2} {5,2}",
                    d.Score, Truncate(d.Class, 40),
                    Pair(d.Html?.W, d.Html?.H), Pair(d.Uxml?.W, d.Uxml?.H),
                    d.Html?.Count ?? 0, d.Uxml?.Count ?? 0));
            }
            return sb.ToString();
        }

        // ---------- UXML side ----------

        static Dictionary<string, Sample> DumpUxmlLayout(
            string uxmlPath, int viewportWidth, int viewportHeight)
        {
            var byClass = new Dictionary<string, (int n, double w, double h)>(StringComparer.Ordinal);
            // Force reimport so the in-memory VTA reflects the on-disk
            // text, otherwise we'd be measuring stale geometry whenever
            // the converter re-emitted UXML right before this call.
            AssetDatabase.ImportAsset(uxmlPath, ImportAssetOptions.ForceUpdate);
            var vta = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(uxmlPath);
            if (vta == null) return new Dictionary<string, Sample>();

            // The reliable way to drive UI Toolkit layout at a known size
            // in the editor: open a hidden EditorWindow, set the root to
            // that exact size, clone the tree, then call ValidateLayout
            // on the panel. Same approach the screenshot bridge uses.
            var window = ScriptableObject.CreateInstance<EditorWindow>();
            window.titleContent = new GUIContent("h2u-layout-cmp");
            try
            {
                window.position = new Rect(20000, 20000, viewportWidth, viewportHeight);
                window.minSize = new Vector2(viewportWidth, viewportHeight);
                window.maxSize = new Vector2(viewportWidth, viewportHeight);
                window.ShowAuxWindow();

                var root = window.rootVisualElement;
                root.style.width = viewportWidth;
                root.style.height = viewportHeight;
                vta.CloneTree(root);

                // Force layout. The IPanel.UpdateForRepaint route requires
                // reflection in some Unity versions; visualTree.IncrementVersion
                // (via MarkDirtyRepaint) plus a synchronous Validate is the
                // safest cross-version path.
                root.MarkDirtyRepaint();
                // UI Toolkit lays out lazily — a hidden EditorWindow won't
                // fire the layout/repaint pass on its own. Drive it by
                // calling Panel.UpdateForRepaint via reflection (the same
                // entry point Unity's screenshot tooling uses) plus a few
                // editor update pumps to flush scheduled work.
                var pumpMi = typeof(EditorApplication).GetMethod(
                    "Internal_CallUpdateFunctions",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                var panel = root.panel as object;
                System.Reflection.MethodInfo updateMi = null;
                if (panel != null)
                {
                    updateMi = panel.GetType().GetMethod(
                        "UpdateForRepaint",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (updateMi == null)
                        updateMi = panel.GetType().GetMethod(
                            "ValidateLayout",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                }
                for (int i = 0; i < 30; i++)
                {
                    try { updateMi?.Invoke(panel, null); } catch { }
                    pumpMi?.Invoke(null, null);
                    // Accessing layout is the property that triggers
                    // resolution if any version bit is dirty.
                    var _ = root.layout;
                }

                Walk(root, byClass);
            }
            finally
            {
                if (window != null) window.Close();
            }

            var output = new Dictionary<string, Sample>(StringComparer.Ordinal);
            foreach (var kv in byClass)
            {
                output[kv.Key] = new Sample
                {
                    Count = kv.Value.n,
                    W = kv.Value.w / kv.Value.n,
                    H = kv.Value.h / kv.Value.n,
                };
            }
            return output;
        }

        static void Walk(VisualElement el, Dictionary<string, (int n, double w, double h)> bucket)
        {
            Rect r = el.worldBound;
            if (!float.IsNaN(r.width) && r.width > 0 && r.height > 0)
            {
                foreach (var cls in el.GetClasses())
                {
                    if (string.IsNullOrEmpty(cls)) continue;
                    if (cls.StartsWith("unity-", StringComparison.Ordinal)) continue;
                    if (cls.StartsWith("h2u-", StringComparison.Ordinal)
                        && !cls.StartsWith("h2u-tag-", StringComparison.Ordinal)) continue;
                    if (!bucket.TryGetValue(cls, out var t)) t = (0, 0, 0);
                    bucket[cls] = (t.n + 1, t.w + r.width, t.h + r.height);
                }
            }
            for (int i = 0; i < el.childCount; i++) Walk(el[i], bucket);
        }

        // ---------- helpers ----------

        static double ZeroIfNaN(double? v) => (v == null || double.IsNaN(v.Value)) ? 0 : v.Value;

        static string Pair(double? a, double? b)
        {
            string fa = a == null || double.IsNaN(a.Value) ? "-" : a.Value.ToString("0", CultureInfo.InvariantCulture);
            string fb = b == null || double.IsNaN(b.Value) ? "-" : b.Value.ToString("0", CultureInfo.InvariantCulture);
            return $"({fa},{fb})";
        }

        static string Truncate(string s, int n)
            => string.IsNullOrEmpty(s) || s.Length <= n ? s : s.Substring(0, n);
    }
}
