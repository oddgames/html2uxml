using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.Text;
using Debug = UnityEngine.Debug;

namespace ODDGames.Html2Uxml.Editor
{
    // One-shot HTML -> UXML/USS importer. Shells out to the html2uxml Python
    // CLI, copies the bundle into Assets/, builds TextCore FontAssets in the
    // same pass. No AssetPostprocessor, no stale-state recovery paths.
    public class Html2UxmlImporterWindow : EditorWindow
    {
        const string Pref = "Html2Uxml.Importer.";

        // Source
        string _python = "python";
        string _source = "";
        string _selector = "";

        // Output
        string _name = "";
        string _outDir = "Assets/UI";

        // Layout
        enum LayoutMode { ScaleToFit = 0, Fixed = 2 }
        LayoutMode _layoutMode = LayoutMode.ScaleToFit;
        int _refPreset;
        int _refWidth = 1920;
        int _refHeight = 1080;
        Color _letterboxColor = Color.black;
        enum FitMode { Contain = 0, Cover = 1, Stretch = 2, Fill = 3 }
        FitMode _fitMode = FitMode.Contain;

        // Assets
        bool _downloadAssets = true;
        bool _downloadFonts = true;

        // Font merges
        readonly List<FontFamilyEntry> _fontFamilies = new List<FontFamilyEntry>();
        Vector2 _fontScroll;

        // UI state
        bool _advancedOpen;
        Vector2 _logScroll;
        readonly StringBuilder _log = new StringBuilder();
        bool _running;

        [Serializable]
        class FontFamilyEntry
        {
            public string family;
            public string mergeTo;   // empty / equal-to-family = keep separate
            public string variants;  // display: "400, 700i"
            public int charCount;
            public bool dynamic;
            public string role;      // "primary" | "fallback"
        }

        [Serializable] class FontFamilyList { public FontFamilyEntry[] families; }
        [Serializable] class CliFamilyVariant { public int weight; public bool italic; }
        [Serializable] class CliFamilyEntry
        {
            public string family;
            public CliFamilyVariant[] variants;
            public string characters;
            public int characterCount;
            public bool dynamic;
            public string role;
        }
        [Serializable] class CliFamilyList { public CliFamilyEntry[] families; }

        struct Preset { public string label; public int w; public int h; }

        static readonly Preset[] _refPresets = new[]
        {
            new Preset { label = "1920 × 1080  (Full HD)",      w = 1920, h = 1080 },
            new Preset { label = "1280 × 720   (HD)",           w = 1280, h = 720  },
            new Preset { label = "2560 × 1440  (QHD)",          w = 2560, h = 1440 },
            new Preset { label = "3840 × 2160  (4K UHD)",       w = 3840, h = 2160 },
            new Preset { label = "1080 × 1920  (Portrait HD)",  w = 1080, h = 1920 },
            new Preset { label = "Custom…",                     w = -1,   h = -1   },
        };

        [MenuItem("Tools/Html2Uxml/Import HTML…")]
        static void Open()
        {
            var w = GetWindow<Html2UxmlImporterWindow>("Html2Uxml Importer");
            w.minSize = new Vector2(560, 540);
        }

        const string SettingsFile = "ProjectSettings/Html2Uxml.json";

        void OnEnable()
        {
            _python         = EditorPrefs.GetString(Pref + "Python", "python");
            _source         = EditorPrefs.GetString(Pref + "Source", "");
            _selector       = EditorPrefs.GetString(Pref + "Selector", "");
            _name           = EditorPrefs.GetString(Pref + "Name", "");
            _downloadAssets = EditorPrefs.GetBool(Pref + "DownloadAssets", true);
            _downloadFonts  = EditorPrefs.GetBool(Pref + "DownloadFonts", true);
            int storedLayout = EditorPrefs.GetInt(Pref + "LayoutMode", -1);
            // Stretch (1) was a useless mode for fixed-design exports — root
            // stretched but pixel-positioned children stayed put. Both first
            // runs and any saved Stretch pref collapse to ScaleToFit.
            _layoutMode = storedLayout == (int)LayoutMode.Fixed
                ? LayoutMode.Fixed
                : LayoutMode.ScaleToFit;
            _refWidth       = EditorPrefs.GetInt(Pref + "RefWidth", 1920);
            _refHeight      = EditorPrefs.GetInt(Pref + "RefHeight", 1080);
            string lbHex    = EditorPrefs.GetString(Pref + "LetterboxColor", "#000000FF");
            if (!ColorUtility.TryParseHtmlString(lbHex, out _letterboxColor)) _letterboxColor = Color.black;
            _fitMode        = (FitMode)Mathf.Clamp(EditorPrefs.GetInt(Pref + "FitMode", 0), 0, 3);
            _advancedOpen   = EditorPrefs.GetBool(Pref + "Advanced", false);
            _refPreset      = ResolvePresetIndex(_refWidth, _refHeight);
            _outDir         = LoadProjectUiRoot();
            LoadFontMerges();
        }

        void OnDisable()
        {
            EditorPrefs.SetString(Pref + "Python", _python);
            EditorPrefs.SetString(Pref + "Source", _source);
            EditorPrefs.SetString(Pref + "Selector", _selector);
            EditorPrefs.SetString(Pref + "Name", _name);
            EditorPrefs.SetBool(Pref + "DownloadAssets", _downloadAssets);
            EditorPrefs.SetBool(Pref + "DownloadFonts", _downloadFonts);
            EditorPrefs.SetInt(Pref + "LayoutMode", (int)_layoutMode);
            EditorPrefs.SetInt(Pref + "RefWidth", _refWidth);
            EditorPrefs.SetInt(Pref + "RefHeight", _refHeight);
            EditorPrefs.SetString(Pref + "LetterboxColor", "#" + ColorUtility.ToHtmlStringRGBA(_letterboxColor));
            EditorPrefs.SetInt(Pref + "FitMode", (int)_fitMode);
            EditorPrefs.SetBool(Pref + "Advanced", _advancedOpen);
            SaveFontMerges();
            SaveProjectUiRoot(_outDir);
        }

        void LoadFontMerges()
        {
            _fontFamilies.Clear();
            string raw = EditorPrefs.GetString(Pref + "FontMerges", "");
            if (string.IsNullOrEmpty(raw)) return;
            try
            {
                var data = JsonUtility.FromJson<FontFamilyList>(raw);
                if (data?.families != null) _fontFamilies.AddRange(data.families);
            }
            catch { /* corrupted prefs — discard */ }
        }

        void SaveFontMerges()
        {
            var data = new FontFamilyList { families = _fontFamilies.ToArray() };
            EditorPrefs.SetString(Pref + "FontMerges", JsonUtility.ToJson(data));
        }

        static string LoadProjectUiRoot()
        {
            try
            {
                if (File.Exists(SettingsFile))
                {
                    var data = JsonUtility.FromJson<Settings>(File.ReadAllText(SettingsFile));
                    if (data != null && !string.IsNullOrWhiteSpace(data.uiRoot)) return data.uiRoot;
                }
            }
            catch { /* fall through to default */ }
            return "Assets/UI";
        }

        static void SaveProjectUiRoot(string uiRoot)
        {
            try
            {
                var data = new Settings { uiRoot = uiRoot };
                File.WriteAllText(SettingsFile, JsonUtility.ToJson(data, prettyPrint: true));
            }
            catch (Exception e) { Debug.LogWarning($"[Html2Uxml] failed to persist UI root: {e.Message}"); }
        }

        [Serializable] class Settings { public string uiRoot; }

        void OnGUI()
        {
            using (new EditorGUI.DisabledScope(_running))
            {
                Section("Source");
                _source = TextRow(
                    new GUIContent("HTML File or URL", "Path to a local .html file or an http(s):// URL."),
                    _source, browseFile: true);
                using (new EditorGUILayout.HorizontalScope())
                {
                    _selector = EditorGUILayout.TextField(
                        new GUIContent("CSS Selector",
                            "Optional. Limits export to one subtree, e.g. #screen-chat-overlay. " +
                            "Use Discover… to pick from selectors found in the source."),
                        _selector);
                    using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(_source)))
                        if (GUILayout.Button("Discover…", GUILayout.Width(96)))
                            DiscoverSelectors();
                }

                Section("Output");
                _name = EditorGUILayout.TextField(
                    new GUIContent("UI Name",
                        "Base filename for the generated .uxml/.uss (and prefix for shared assets). " +
                        "Defaults to the input file stem when empty."),
                    _name);
                _outDir = TextRow(
                    new GUIContent("UI Folder",
                        "Project folder under Assets/. Shared by every imported screen. " +
                        "Stored in ProjectSettings/Html2Uxml.json."),
                    _outDir, browseFile: false);

                Section("Layout");
                int currentIdx = _layoutMode == LayoutMode.Fixed ? 1 : 0;
                int nextIdx = EditorGUILayout.Popup(
                    new GUIContent("Layout Mode",
                        "How the imported UI fits the runtime panel:\n" +
                        " • Scale to Fit: keep design size, uniform-scale the whole UI to fit (recommended).\n" +
                        " • Fixed: root pinned at design size; rely on PanelSettings for any scaling."),
                    currentIdx,
                    new[] {
                        "Scale to Fit  (uniform scale wrapper)",
                        "Fixed  (root pinned at design size)",
                    });
                _layoutMode = nextIdx == 1 ? LayoutMode.Fixed : LayoutMode.ScaleToFit;

                bool needsRefSize = _layoutMode == LayoutMode.Fixed;
                using (new EditorGUI.DisabledScope(!needsRefSize))
                {
                    int newPreset = EditorGUILayout.Popup(
                        new GUIContent("Reference Size",
                            "Fixed pixel size applied to the UXML root in Fixed mode. " +
                            "Scale to Fit reads the design size from the converted CSS at runtime, " +
                            "so this control stays disabled in that mode."),
                        _refPreset, PresetLabels());
                    if (newPreset != _refPreset)
                    {
                        _refPreset = newPreset;
                        if (_refPresets[newPreset].w > 0)
                        {
                            _refWidth  = _refPresets[newPreset].w;
                            _refHeight = _refPresets[newPreset].h;
                        }
                    }

                    bool isCustom = _refPresets[_refPreset].w < 0;
                    using (new EditorGUI.DisabledScope(!isCustom))
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.PrefixLabel(new GUIContent("Custom Size", "Width × Height in pixels."));
                        _refWidth  = Mathf.Max(1, EditorGUILayout.IntField(_refWidth, GUILayout.MaxWidth(80)));
                        GUILayout.Label("×", GUILayout.Width(14));
                        _refHeight = Mathf.Max(1, EditorGUILayout.IntField(_refHeight, GUILayout.MaxWidth(80)));
                        GUILayout.FlexibleSpace();
                    }
                }

                using (new EditorGUI.DisabledScope(_layoutMode != LayoutMode.ScaleToFit))
                {
                    _fitMode = (FitMode)EditorGUILayout.Popup(
                        new GUIContent("Fit Mode",
                            "How the design fits the panel inside Scale to Fit:\n" +
                            " • Contain: uniform scale; letterbox where aspects differ.\n" +
                            " • Cover: uniform scale that fills the panel; edges may clip.\n" +
                            " • Stretch: non-uniform scale; distorts but fills exactly.\n" +
                            " • Fill: resize the design root to the panel without scaling. " +
                            "Backgrounds (gradient, carbon fiber) extend to the panel edges; " +
                            "absolute-positioned children stay anchored to their design offsets. " +
                            "Best when the bracket frame should reach the edges without " +
                            "distorting the items inside."),
                        (int)_fitMode,
                        new[] { "Contain", "Cover", "Stretch", "Fill" });
                }

                _letterboxColor = EditorGUILayout.ColorField(
                    new GUIContent("Letterbox Color",
                        "Background color filled around the design when the panel aspect " +
                        "doesn't match the design aspect. Used by Contain mode only. " +
                        "Pick a color that matches the page's outer frame; transparent " +
                        "leaves the panel chrome visible."),
                    _letterboxColor);

                Section("Assets");
                _downloadAssets = EditorGUILayout.Toggle(
                    new GUIContent("Download Remote Images",
                        "Pull http(s) image URLs into UI/Images/ and rewrite USS to use the local copies."),
                    _downloadAssets);
                _downloadFonts = EditorGUILayout.Toggle(
                    new GUIContent("Download Web Fonts",
                        "Bundle Google Fonts / system TTFs into UI/Fonts/ and emit TextCore FontAssets."),
                    _downloadFonts);

                using (new EditorGUI.DisabledScope(!_downloadFonts))
                    DrawFontMergesSection();

                EditorGUILayout.Space(4);
                _advancedOpen = EditorGUILayout.Foldout(_advancedOpen, "Advanced", true);
                if (_advancedOpen)
                {
                    using (new EditorGUI.IndentLevelScope())
                    {
                        _python = EditorGUILayout.TextField(
                            new GUIContent("Python", "Interpreter used to run html2uxml.cli."),
                            _python);
                    }
                }

                EditorGUILayout.Space(8);
                using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(_source) || string.IsNullOrWhiteSpace(_outDir)))
                    if (GUILayout.Button(_running ? "Converting…" : "Convert", GUILayout.Height(30)))
                        RunImport();
            }

            EditorGUILayout.Space(6);
            Section("Log");
            using (var s = new EditorGUILayout.ScrollViewScope(_logScroll, GUILayout.MinHeight(140)))
            {
                _logScroll = s.scrollPosition;
                EditorGUILayout.TextArea(_log.ToString(), GUILayout.ExpandHeight(true));
            }
        }

        void DrawFontMergesSection()
        {
            Section("Font Merges");
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(
                    _fontFamilies.Count == 0
                        ? "No fonts detected. Click Detect to scan the source."
                        : $"{_fontFamilies.Count} font famil{(_fontFamilies.Count == 1 ? "y" : "ies")} detected.",
                    EditorStyles.miniLabel);
                using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(_source)))
                    if (GUILayout.Button("Detect…", GUILayout.Width(96)))
                        DetectFonts();
                using (new EditorGUI.DisabledScope(_fontFamilies.Count == 0))
                    if (GUILayout.Button("Clear", GUILayout.Width(60)))
                    {
                        _fontFamilies.Clear();
                        Repaint();
                    }
            }

            if (_fontFamilies.Count == 0) return;

            string[] mergeOptions = BuildMergeOptions();
            float rowHeight = EditorGUIUtility.singleLineHeight + 4f;
            float maxHeight = Mathf.Min(rowHeight * 6f + 12f, rowHeight * _fontFamilies.Count + 12f);
            using (var s = new EditorGUILayout.ScrollViewScope(_fontScroll, GUILayout.MinHeight(rowHeight + 8f), GUILayout.MaxHeight(maxHeight)))
            {
                _fontScroll = s.scrollPosition;
                for (int i = 0; i < _fontFamilies.Count; i++)
                {
                    var f = _fontFamilies[i];
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        bool isFallback = f.role == "fallback";
                        string suffix = isFallback ? "  (fallback)" : "";
                        var labelContent = new GUIContent(
                            f.family + suffix,
                            $"role: {f.role}\nvariants: {f.variants}\ncharacters: {f.charCount}" +
                            (f.dynamic ? "\ndynamic atlas" : "") +
                            (isFallback ? "\n\nReferenced only as a fallback in a font-family stack. " +
                                "Pick a merge target to fold it into a primary family." : ""));
                        var labelStyle = isFallback ? EditorStyles.miniLabel : EditorStyles.label;
                        EditorGUILayout.LabelField(labelContent, labelStyle, GUILayout.MinWidth(160));
                        GUILayout.Label("→", GUILayout.Width(16));

                        int currentIndex = ResolveMergeIndex(f, mergeOptions);
                        int nextIndex = EditorGUILayout.Popup(currentIndex, mergeOptions);
                        if (nextIndex != currentIndex)
                        {
                            f.mergeTo = (nextIndex == 0 || nextIndex >= mergeOptions.Length)
                                ? ""
                                : mergeOptions[nextIndex];
                            if (f.mergeTo == f.family) f.mergeTo = "";
                        }
                        EditorGUILayout.LabelField(VariantsSummary(f), EditorStyles.miniLabel, GUILayout.Width(140));
                    }
                }
            }
        }

        string[] BuildMergeOptions()
        {
            var opts = new List<string> { "(keep separate)" };
            foreach (var f in _fontFamilies)
                if (!opts.Contains(f.family))
                    opts.Add(f.family);
            return opts.ToArray();
        }

        static int ResolveMergeIndex(FontFamilyEntry entry, string[] options)
        {
            if (string.IsNullOrEmpty(entry.mergeTo) || entry.mergeTo == entry.family) return 0;
            for (int i = 1; i < options.Length; i++)
                if (options[i] == entry.mergeTo) return i;
            return 0;
        }

        static string VariantsSummary(FontFamilyEntry f)
        {
            if (string.IsNullOrEmpty(f.variants)) return "";
            return f.variants;
        }

        void DetectFonts()
        {
            _log.Clear();
            try
            {
                string tempRoot = Path.Combine(Path.GetTempPath(), "html2uxml_fonts_" + DateTime.Now.Ticks);
                Directory.CreateDirectory(tempRoot);

                var args = new StringBuilder();
                args.Append("-m html2uxml.cli ").Append('"').Append(_source).Append('"');
                args.Append(" -o ").Append('"').Append(tempRoot).Append('"');
                if (!string.IsNullOrWhiteSpace(_selector))
                    args.Append(" --selector ").Append('"').Append(_selector).Append('"');
                args.Append(" --list-families");

                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = _python, Arguments = args.ToString(),
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    UseShellExecute = false, CreateNoWindow = true,
                });
                string outText = p.StandardOutput.ReadToEnd();
                string errText = p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0)
                {
                    Append($"detect failed: {errText.Trim()}");
                    return;
                }

                CliFamilyList list;
                try { list = JsonUtility.FromJson<CliFamilyList>(outText); }
                catch (Exception e) { Append($"detect parse error: {e.Message}"); return; }

                if (list?.families == null || list.families.Length == 0)
                {
                    _fontFamilies.Clear();
                    Append("no font families found in source.");
                    return;
                }

                var existingMerges = _fontFamilies.ToDictionary(e => e.family, e => e.mergeTo);
                _fontFamilies.Clear();
                foreach (var item in list.families)
                {
                    var entry = new FontFamilyEntry
                    {
                        family = item.family,
                        variants = FormatVariants(item.variants),
                        charCount = item.characterCount,
                        dynamic = item.dynamic,
                        role = string.IsNullOrEmpty(item.role) ? "primary" : item.role,
                        mergeTo = existingMerges.TryGetValue(item.family, out var prev) ? prev : "",
                    };
                    _fontFamilies.Add(entry);
                }
                // drop merge targets that no longer exist as detected families
                var detected = new HashSet<string>(_fontFamilies.Select(f => f.family));
                foreach (var f in _fontFamilies)
                    if (!string.IsNullOrEmpty(f.mergeTo) && !detected.Contains(f.mergeTo))
                        f.mergeTo = "";
                Append($"detected {_fontFamilies.Count} font famil{(_fontFamilies.Count == 1 ? "y" : "ies")}.");
            }
            catch (Exception e) { Append($"detect error: {e.Message}"); }
        }

        static string FormatVariants(CliFamilyVariant[] variants)
        {
            if (variants == null || variants.Length == 0) return "";
            var parts = new List<string>(variants.Length);
            foreach (var v in variants) parts.Add($"{v.weight}{(v.italic ? "i" : "")}");
            return string.Join(", ", parts);
        }

        IEnumerable<string> CollectFontAliasArgs()
        {
            foreach (var f in _fontFamilies)
            {
                if (string.IsNullOrEmpty(f.mergeTo) || f.mergeTo == f.family) continue;
                yield return f.family + "=" + f.mergeTo;
            }
        }

        static void Section(string label)
        {
            EditorGUILayout.Space(6);
            var rect = EditorGUILayout.GetControlRect(false, 1);
            EditorGUI.DrawRect(rect, new Color(0f, 0f, 0f, 0.18f));
            EditorGUILayout.LabelField(label, EditorStyles.miniBoldLabel);
        }

        string[] PresetLabels()
        {
            var arr = new string[_refPresets.Length];
            for (int i = 0; i < arr.Length; i++) arr[i] = _refPresets[i].label;
            return arr;
        }

        static int ResolvePresetIndex(int w, int h)
        {
            for (int i = 0; i < _refPresets.Length; i++)
                if (_refPresets[i].w == w && _refPresets[i].h == h) return i;
            return _refPresets.Length - 1; // Custom
        }

        string TextRow(string label, string value, bool browseFile) =>
            TextRow(new GUIContent(label), value, browseFile);

        string TextRow(GUIContent label, string value, bool browseFile)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                value = EditorGUILayout.TextField(label, value);
                if (GUILayout.Button("Browse…", GUILayout.Width(80)))
                {
                    string picked = browseFile
                        ? EditorUtility.OpenFilePanel("Choose HTML", "", "html,htm")
                        : EditorUtility.OpenFolderPanel("Choose folder", Application.dataPath, "");
                    if (!string.IsNullOrEmpty(picked))
                    {
                        if (browseFile) value = picked;
                        else
                        {
                            string rel = MakeProjectRelative(picked);
                            if (rel != null) value = rel;
                            else EditorUtility.DisplayDialog("Invalid folder", "Pick a folder inside Assets/.", "OK");
                        }
                    }
                }
            }
            return value;
        }

        void RunImport()
        {
            _log.Clear();
            _running = true;
            Repaint();
            try
            {
                string projectRoot = Path.GetDirectoryName(Application.dataPath);
                string targetDir = ResolveOutDir(projectRoot);
                if (targetDir == null) { Append("error: output must be under Assets/."); return; }

                string tempRoot = Path.Combine(Path.GetTempPath(), "html2uxml_import_" + DateTime.Now.Ticks);
                Directory.CreateDirectory(tempRoot);

                int code = RunCli(tempRoot, out string stdout, out string stderr);
                if (!string.IsNullOrEmpty(stdout)) Append(stdout.Trim());
                if (!string.IsNullOrEmpty(stderr)) Append(stderr.Trim());
                if (code != 0) { Append($"CLI exit {code}."); return; }

                string bundleDir = LocateBundle(tempRoot);
                if (bundleDir == null) { Append("error: no UXML produced."); return; }

                foreach (var uxml in Directory.EnumerateFiles(bundleDir, "*.uxml", SearchOption.TopDirectoryOnly))
                    ApplyLayout(uxml, _layoutMode, _refWidth, _refHeight, _letterboxColor, _fitMode);
                Append(_layoutMode == LayoutMode.Fixed
                    ? $"layout: fixed {_refWidth} × {_refHeight} root."
                    : "layout: scale-to-fit wrapper (uniform scale, design size from CSS).");

                CopyBundle(bundleDir, targetDir);

                int fonts = BuildFontAssets(Path.Combine(targetDir, "Fonts"));
                Append($"built {fonts} font asset(s).");

                int gradients = BuildTextGradients(bundleDir, targetDir);
                if (gradients > 0) Append($"built {gradients} text gradient asset(s).");

                AssetDatabase.Refresh();
                string firstUxml = FindFirstUxml(targetDir);
                if (firstUxml != null)
                {
                    var asset = AssetDatabase.LoadMainAssetAtPath(firstUxml);
                    if (asset != null) { Selection.activeObject = asset; EditorGUIUtility.PingObject(asset); }
                    Append($"done: {firstUxml}");
                }
            }
            catch (Exception e) { Append($"error: {e.Message}"); Debug.LogException(e); }
            finally { _running = false; Repaint(); }
        }

        void DiscoverSelectors()
        {
            try
            {
                string tempRoot = Path.Combine(Path.GetTempPath(), "html2uxml_list_" + DateTime.Now.Ticks);
                Directory.CreateDirectory(tempRoot);

                var args = new StringBuilder();
                args.Append("-m html2uxml.cli ").Append('"').Append(_source).Append('"');
                args.Append(" -o ").Append('"').Append(tempRoot).Append('"');
                args.Append(" --list-selectors");

                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = _python, Arguments = args.ToString(),
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    UseShellExecute = false, CreateNoWindow = true,
                });
                string outText = p.StandardOutput.ReadToEnd();
                string errText = p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0)
                {
                    Append($"discover failed: {errText}".Trim());
                    return;
                }

                var list = JsonUtility.FromJson<SelectorList>(outText);
                if (list?.selectors == null || list.selectors.Length == 0)
                {
                    Append("no selectors found in source.");
                    return;
                }

                var menu = new GenericMenu();
                foreach (var s in list.selectors)
                {
                    string label = s.kind == "id"
                        ? $"ids/{s.selector}  ({s.tag})"
                        : $"classes/{s.selector}  ({s.tag})";
                    string sel = s.selector;
                    menu.AddItem(new GUIContent(label), _selector == sel, () => { _selector = sel; Repaint(); });
                }
                menu.ShowAsContext();
            }
            catch (Exception e) { Append($"discover error: {e.Message}"); }
        }

        [Serializable] class SelectorList { public SelectorEntry[] selectors; }
        [Serializable] class SelectorEntry { public string selector; public string kind; public string tag; public string text; }

        int RunCli(string tempRoot, out string stdout, out string stderr)
        {
            var args = new StringBuilder();
            args.Append("-m html2uxml.cli ").Append('"').Append(_source).Append('"');
            args.Append(" -o ").Append('"').Append(tempRoot).Append('"');
            if (!string.IsNullOrWhiteSpace(_name))     args.Append(" --name ").Append('"').Append(_name).Append('"');
            if (!string.IsNullOrWhiteSpace(_selector)) args.Append(" --selector ").Append('"').Append(_selector).Append('"');
            args.Append(" --bundle-assets");
            if (_downloadAssets) args.Append(" --download-assets");
            args.Append(_downloadFonts ? " --download-fonts" : " --no-download-fonts");
            foreach (var alias in CollectFontAliasArgs())
                args.Append(" --font-alias ").Append('"').Append(alias).Append('"');
            args.Append(" -q");
            Append($"$ {_python} {args}");

            try
            {
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = _python, Arguments = args.ToString(),
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    UseShellExecute = false, CreateNoWindow = true,
                });
                stdout = p.StandardOutput.ReadToEnd();
                stderr = p.StandardError.ReadToEnd();
                p.WaitForExit();
                return p.ExitCode;
            }
            catch (Exception e) { stdout = ""; stderr = $"failed to launch python: {e.Message}"; return -1; }
        }

        static string LocateBundle(string root)
        {
            string ui = Path.Combine(root, "UI");
            if (Directory.Exists(ui) && Directory.GetFiles(ui, "*.uxml").Length > 0) return ui;
            return Directory.GetFiles(root, "*.uxml").Length > 0 ? root : null;
        }

        string ResolveOutDir(string projectRoot)
        {
            string rel = _outDir.Replace('\\', '/').Trim('/');
            if (rel != "Assets" && !rel.StartsWith("Assets/", StringComparison.Ordinal)) return null;
            string abs = Path.Combine(projectRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(abs);
            return abs;
        }

        static string MakeProjectRelative(string abs)
        {
            string root = Path.GetDirectoryName(Application.dataPath)?.Replace('\\', '/');
            string norm = abs.Replace('\\', '/');
            if (string.IsNullOrEmpty(root) || !norm.StartsWith(root + "/", StringComparison.Ordinal)) return null;
            return norm.Substring(root.Length + 1);
        }

        static readonly string[] _allowedExt = { ".uxml", ".uss", ".ttf", ".otf", ".png", ".jpg", ".jpeg", ".webp", ".gif", ".svg" };

        static void CopyBundle(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (var f in Directory.EnumerateFiles(source))
            {
                if (HasAllowedExt(f))
                    File.Copy(f, Path.Combine(target, Path.GetFileName(f)), overwrite: true);
            }
            CopyTree(Path.Combine(source, "Fonts"), Path.Combine(target, "Fonts"));
            CopyTree(Path.Combine(source, "Images"), Path.Combine(target, "Images"));
        }

        static void CopyTree(string source, string target)
        {
            if (!Directory.Exists(source)) return;
            Directory.CreateDirectory(target);
            foreach (var f in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                if (!HasAllowedExt(f)) continue; // drop sidecar JSON / .meta / scratch
                string dest = Path.Combine(target, Path.GetRelativePath(source, f));
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                File.Copy(f, dest, overwrite: true);
            }
        }

        static bool HasAllowedExt(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            foreach (var a in _allowedExt) if (a == ext) return true;
            return false;
        }

        // Match an opening tag for a UXML element (ui: or odd: namespace).
        // The <Style src="..."/> and <ui:UXML> wrappers are skipped.
        static readonly Regex _elementOpenRx = new Regex(
            @"<(?<tag>(?:ui|odd):[A-Za-z][A-Za-z0-9]*)(?<attrs>[^>]*)>",
            RegexOptions.Singleline);

        static readonly Regex _styleAttrRx = new Regex(
            @"\sstyle\s*=\s*""(?<value>[^""]*)""",
            RegexOptions.IgnoreCase);

        static readonly Regex _uxmlOpenTagRx = new Regex(
            @"<ui:UXML\b[^>]*>",
            RegexOptions.IgnoreCase);

        static void ApplyLayout(string uxmlPath, LayoutMode mode, int refWidth, int refHeight, Color letterboxColor, FitMode fitMode)
        {
            string text;
            try { text = File.ReadAllText(uxmlPath); }
            catch { return; }

            string updated = text;
            switch (mode)
            {
                case LayoutMode.Fixed:
                    updated = InjectRootStyle(text, $"width: {refWidth}px; height: {refHeight}px;");
                    break;
                case LayoutMode.ScaleToFit:
                    // Let the converter's CSS sizing flow through unchanged.
                    // The wrapper's runtime scaler reads the inner element's
                    // resolved size and applies a uniform `style.scale` so
                    // gradients/back-panels and pixel-positioned children all
                    // grow together. Forcing a size on the inner here would
                    // bloat the gradient host past the actual design canvas
                    // (e.g. body becomes 1920x1080 while the dc-card it
                    // contains is only 760x360, leaving stretched gradient
                    // around a tiny bracket in the top-left).
                    updated = WrapScaleRoot(text, letterboxColor, fitMode);
                    break;
            }
            if (updated != text)
                File.WriteAllText(uxmlPath, updated);
        }

        // Wraps the body of the UXML doc in <odd:Html2UxmlScaleRoot>. The
        // wrapper is the root visual element so it flex-fills the panel; its
        // single child (the converted page root) keeps its design size and
        // gets uniform-scaled by the runtime Html2UxmlScaleRoot component.
        // The letterbox color paints the wrapper background so aspect-mismatch
        // bands don't show panel chrome / checkerboard underneath.
        static string WrapScaleRoot(string uxml, Color letterboxColor, FitMode fitMode)
        {
            uxml = EnsureOddNamespace(uxml);

            // Locate the body span: end of the last "<Style ... />" tag (or
            // the end of <ui:UXML> if no Style) up to the closing </ui:UXML>.
            int bodyStart = -1;
            var styleRx = new Regex(@"<Style\b[^>]*/>", RegexOptions.IgnoreCase);
            var styleMatches = styleRx.Matches(uxml);
            if (styleMatches.Count > 0)
                bodyStart = styleMatches[styleMatches.Count - 1].Index + styleMatches[styleMatches.Count - 1].Length;
            else
            {
                var openMatch = _uxmlOpenTagRx.Match(uxml);
                if (!openMatch.Success) return uxml;
                bodyStart = openMatch.Index + openMatch.Length;
            }

            int bodyEnd = uxml.LastIndexOf("</ui:UXML>", StringComparison.Ordinal);
            if (bodyEnd < 0 || bodyEnd <= bodyStart) return uxml;

            string before = uxml.Substring(0, bodyStart);
            string body = uxml.Substring(bodyStart, bodyEnd - bodyStart);
            string after = uxml.Substring(bodyEnd);

            string trimmed = body.Trim('\r', '\n');
            // Inline style ensures layout works even if Html2UxmlScaleRoot
            // hasn't been recompiled yet (Unity falls back to a plain
            // VisualElement on unknown UxmlElement types). The runtime
            // scaler adds the uniform scale on top of this baseline.
            string bgRule = letterboxColor.a <= 0.001f
                ? ""
                : $" background-color: rgba({Mathf.RoundToInt(letterboxColor.r * 255)}, " +
                  $"{Mathf.RoundToInt(letterboxColor.g * 255)}, " +
                  $"{Mathf.RoundToInt(letterboxColor.b * 255)}, " +
                  $"{letterboxColor.a.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)});";
            string fitRule = $" --odd-scale-mode: {fitMode.ToString().ToLowerInvariant()};";
            string wrapperStyle =
                "flex-grow: 1; align-items: center; justify-content: center; overflow: hidden;" + bgRule + fitRule;
            string wrapperOpen = $"\n  <odd:Html2UxmlScaleRoot style=\"{wrapperStyle}\">";
            string wrapperClose = "\n  </odd:Html2UxmlScaleRoot>\n";
            return before + wrapperOpen + "\n" + trimmed + wrapperClose + after;
        }

        static string EnsureOddNamespace(string uxml)
        {
            var m = _uxmlOpenTagRx.Match(uxml);
            if (!m.Success) return uxml;
            string tag = m.Value;
            if (tag.Contains("xmlns:odd")) return uxml;
            string injected = tag.Substring(0, tag.Length - 1).TrimEnd()
                + " xmlns:odd=\"ODDGames.Html2Uxml\">";
            return uxml.Substring(0, m.Index) + injected + uxml.Substring(m.Index + tag.Length);
        }

        static string InjectRootStyle(string uxml, string injected)
        {
            int searchFrom = 0;
            while (true)
            {
                var m = _elementOpenRx.Match(uxml, searchFrom);
                if (!m.Success) return uxml;
                string tag = m.Groups["tag"].Value;
                if (tag == "ui:UXML")
                {
                    searchFrom = m.Index + m.Length;
                    continue;
                }
                string attrs = m.Groups["attrs"].Value;
                string newAttrs = MergeStyleAttr(attrs, injected);
                return uxml.Substring(0, m.Index) + $"<{tag}{newAttrs}>" + uxml.Substring(m.Index + m.Length);
            }
        }

        static string MergeStyleAttr(string attrs, string injected)
        {
            string trimmed = attrs.TrimEnd();
            bool selfClose = trimmed.EndsWith("/", StringComparison.Ordinal);
            string core = selfClose ? trimmed.Substring(0, trimmed.Length - 1).TrimEnd() : trimmed;

            var existingMatch = _styleAttrRx.Match(core);
            string newCore;
            if (existingMatch.Success)
            {
                string existing = existingMatch.Groups["value"].Value.Trim();
                if (existing.EndsWith(";", StringComparison.Ordinal))
                    existing = existing.Substring(0, existing.Length - 1).TrimEnd();
                string merged = existing.Length == 0 ? injected : existing + "; " + injected;
                newCore = core.Substring(0, existingMatch.Index)
                    + $" style=\"{merged}\""
                    + core.Substring(existingMatch.Index + existingMatch.Length);
            }
            else
            {
                newCore = core + $" style=\"{injected}\"";
            }
            return selfClose ? newCore + " /" : newCore;
        }

        static int BuildFontAssets(string fontsDir)
        {
            if (!Directory.Exists(fontsDir)) return 0;
            AssetDatabase.Refresh();

            int count = 0;
            string root = Path.GetDirectoryName(Application.dataPath).Replace('\\', '/');
            foreach (var ttf in Directory.EnumerateFiles(fontsDir, "*.ttf", SearchOption.TopDirectoryOnly))
            {
                string norm = ttf.Replace('\\', '/');
                if (!norm.StartsWith(root + "/", StringComparison.Ordinal)) continue;
                string assetTtf = norm.Substring(root.Length + 1);
                var font = AssetDatabase.LoadAssetAtPath<Font>(assetTtf);
                if (font == null) continue;
                string sdf = $"{Path.GetDirectoryName(assetTtf)}/{Path.GetFileNameWithoutExtension(assetTtf)} SDF.asset".Replace('\\', '/');
                var existing = AssetDatabase.LoadAssetAtPath<FontAsset>(sdf);
                if (existing != null)
                {
                    bool stale = existing.sourceFontFile == null || existing.material == null;
                    if (!stale) continue;
                    AssetDatabase.DeleteAsset(sdf);
                }
                var fa = FontAsset.CreateFontAsset(font);
                if (fa == null) continue;
                fa.atlasPopulationMode = AtlasPopulationMode.Dynamic;
                AssetDatabase.CreateAsset(fa, sdf);
                count++;
            }
            if (count > 0) AssetDatabase.SaveAssets();
            return count;
        }

        static int BuildTextGradients(string sourceDir, string targetDir)
        {
            int count = 0;
            string root = Path.GetDirectoryName(Application.dataPath).Replace('\\', '/');
            foreach (var json in Directory.EnumerateFiles(sourceDir, "*.h2utg.json", SearchOption.TopDirectoryOnly))
            {
                TextGradientData data;
                try { data = JsonUtility.FromJson<TextGradientData>(File.ReadAllText(json)); }
                catch (Exception e) { Debug.LogWarning($"[Html2Uxml] gradient {json}: {e.Message}"); continue; }
                if (data == null) continue;

                string assetFile = Path.GetFileName(json);
                assetFile = assetFile.Substring(0, assetFile.Length - ".h2utg.json".Length) + ".asset";
                string assetAbs = Path.Combine(targetDir, assetFile);
                string norm = assetAbs.Replace('\\', '/');
                if (!norm.StartsWith(root + "/", StringComparison.Ordinal)) continue;
                string assetPath = norm.Substring(root.Length + 1);

                var existing = AssetDatabase.LoadAssetAtPath<TextColorGradient>(assetPath);
                bool create = existing == null;
                var asset = create ? ScriptableObject.CreateInstance<TextColorGradient>() : existing;
                asset.colorMode   = ParseGradientMode(data.mode);
                asset.topLeft     = data.topLeft.ToColor();
                asset.topRight    = data.topRight.ToColor();
                asset.bottomLeft  = data.bottomLeft.ToColor();
                asset.bottomRight = data.bottomRight.ToColor();
                if (create) AssetDatabase.CreateAsset(asset, assetPath);
                else EditorUtility.SetDirty(asset);
                count++;
            }
            if (count > 0) AssetDatabase.SaveAssets();
            return count;
        }

        static ColorGradientMode ParseGradientMode(string mode) => mode switch
        {
            "Horizontal" => ColorGradientMode.HorizontalGradient,
            "Vertical" => ColorGradientMode.VerticalGradient,
            "FourCornersGradient" => ColorGradientMode.FourCornersGradient,
            _ => ColorGradientMode.Single,
        };

        [Serializable] class TextGradientData { public string name, mode; public RGBA topLeft, topRight, bottomLeft, bottomRight; }
        [Serializable] class RGBA { public float r, g, b, a; public Color ToColor() => new Color(r, g, b, a); }

        static string FindFirstUxml(string targetDir)
        {
            string root = Path.GetDirectoryName(Application.dataPath).Replace('\\', '/');
            foreach (var p in Directory.EnumerateFiles(targetDir, "*.uxml", SearchOption.TopDirectoryOnly))
            {
                string norm = p.Replace('\\', '/');
                if (norm.StartsWith(root + "/", StringComparison.Ordinal))
                    return norm.Substring(root.Length + 1);
            }
            return null;
        }

        void Append(string line) { _log.AppendLine(line); Repaint(); }
    }
}
