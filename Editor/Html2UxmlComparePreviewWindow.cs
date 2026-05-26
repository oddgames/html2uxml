using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ODDGames.Html2Uxml.Editor.Converter;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;

namespace ODDGames.Html2Uxml.Editor
{
    // Side-by-side preview of a converted UXML and the original HTML.
    //
    // Left pane: live UXML render via UI Toolkit.
    // Right pane: a real Chrome `--app` window, reparented (Win32 SetParent)
    // into Unity's main editor HWND so it appears docked inside this
    // EditorWindow. An EditorApplication.update tick keeps the Chrome HWND
    // positioned over the right pane every frame, so dock changes / window
    // moves / pane resizes all stay in sync.
    //
    // The companion HTML is located via the `<!-- source: Src/<base>.html -->`
    // comment seeded into every generated UXML by html2uxml.cli.
    //
    // Caveats: keyboard focus is captured by Chrome when its area receives
    // a click; Unity shortcuts won't fire until you click back into Unity.
    // The browser process is killed when this window closes.
    public class Html2UxmlComparePreviewWindow : EditorWindow
    {
        const string Pref = "Html2Uxml.Compare.";

        VisualTreeAsset _uxml;
        string _htmlAbsPath;

        ObjectField _uxmlField;
        IntegerField _widthField;
        IntegerField _heightField;
        IntegerField _zoomField;
        double _zoomRelaunchAt;
        bool _zoomRelaunchScheduled;
        Label _statusLabel;
        VisualElement _uxmlHost;
        VisualElement _embedPane;
        Label _embedPlaceholder;

        Process _browser;
        IntPtr _browserHwnd = IntPtr.Zero;
        IntPtr _unityHwnd = IntPtr.Zero;
        long _browserOrigStyle;
        long _browserOrigExStyle;
        IntPtr _browserOrigParent = IntPtr.Zero;
        double _hwndSearchDeadline;
        string _hwndSearchTitleHint;

        // ---- URL fetch + scratch state -----------------------------------
        // When the user pulls a remote page in via the URL field, we mirror
        // the converter output into Assets/<ScratchAssetFolder>/<slug>/ so
        // VisualTreeAsset can be loaded for the live preview. "Save" then
        // moves that folder under Assets/UI/<slug>/.
        const string ScratchAssetFolder = "Html2UxmlScratch";

        TextField _urlField;
        Toggle _flattenToggle;
        Button _fetchBtn;
        Button _saveBtn;
        Button _discardBtn;
        Label _fetchStatus;
        string _scratchAssetPath;        // e.g. Assets/Html2UxmlScratch/example_com
        string _scratchTempDir;          // %TEMP%/html2uxml_compare/<slug>
        string _scratchSlug;
        bool _fetchInFlight;
        CancellationTokenSource _fetchCts;

        [MenuItem("Tools/Html2Uxml/Compare HTML ↔ UXML…")]
        static void Open()
        {
            var w = GetWindow<Html2UxmlComparePreviewWindow>("Compare UXML / HTML");
            w.minSize = new Vector2(900, 480);
        }

        void OnEnable()
        {
            _unityHwnd = Process.GetCurrentProcess().MainWindowHandle;
            Selection.selectionChanged += OnSelectionChanged;
            EditorApplication.update += Tick;
        }

        void OnDisable()
        {
            EditorApplication.update -= Tick;
            Selection.selectionChanged -= OnSelectionChanged;
            EditorPrefs.SetInt(Pref + "Width", _widthField?.value ?? 1920);
            EditorPrefs.SetInt(Pref + "Height", _heightField?.value ?? 1080);
            EditorPrefs.SetInt(Pref + "Zoom", _zoomField?.value ?? 100);
            _fetchCts?.Cancel();
            // Drop any unsaved scratch bundle so closing the window doesn't
            // leave Assets/Html2UxmlScratch/ littered with one-off previews.
            if (!string.IsNullOrEmpty(_scratchAssetPath)
                && _scratchAssetPath.StartsWith("Assets/" + ScratchAssetFolder + "/", StringComparison.Ordinal))
            {
                DeleteAssetFolderIfExists(_scratchAssetPath);
            }
            KillBrowser();
        }

        void OnSelectionChanged()
        {
            if (Selection.activeObject is VisualTreeAsset vt && vt != _uxml)
            {
                _uxml = vt;
                if (_uxmlField != null) _uxmlField.SetValueWithoutNotify(vt);
                Refresh();
            }
        }

        void CreateGUI()
        {
            try { CreateGuiCore(); }
            catch (Exception e)
            {
                Debug.LogException(e);
                var fallback = new Label("Compare window init failed: " + e.GetType().Name + " " + e.Message);
                fallback.style.color = Color.red;
                fallback.style.whiteSpace = WhiteSpace.Normal;
                rootVisualElement.Add(fallback);
            }
        }

        void CreateGuiCore()
        {
            var root = rootVisualElement;
            root.style.paddingTop = 4;
            root.style.paddingBottom = 4;
            root.style.paddingLeft = 4;
            root.style.paddingRight = 4;

            var toolbar = new VisualElement();
            toolbar.style.flexDirection = FlexDirection.Row;
            toolbar.style.alignItems = Align.Center;
            toolbar.style.marginBottom = 4;
            root.Add(toolbar);

            _uxmlField = new ObjectField("UXML")
            {
                objectType = typeof(VisualTreeAsset),
                allowSceneObjects = false,
                value = _uxml,
            };
            _uxmlField.style.flexGrow = 1;
            _uxmlField.RegisterValueChangedCallback(e =>
            {
                _uxml = e.newValue as VisualTreeAsset;
                Refresh();
            });
            toolbar.Add(_uxmlField);

            _widthField = new IntegerField("W") { value = Mathf.Max(1, EditorPrefs.GetInt(Pref + "Width", 1920)) };
            _widthField.style.width = 110;
            _widthField.RegisterValueChangedCallback(_ => ApplyPaneSize());
            toolbar.Add(_widthField);

            _heightField = new IntegerField("H") { value = Mathf.Max(1, EditorPrefs.GetInt(Pref + "Height", 1080)) };
            _heightField.style.width = 110;
            _heightField.RegisterValueChangedCallback(_ => ApplyPaneSize());
            toolbar.Add(_heightField);

            _zoomField = new IntegerField("Zoom %") { value = Mathf.Clamp(EditorPrefs.GetInt(Pref + "Zoom", 100), 10, 400) };
            _zoomField.style.width = 110;
            _zoomField.RegisterValueChangedCallback(_ =>
            {
                ApplyUxmlZoom();
                ScheduleBrowserRelaunch();
            });
            toolbar.Add(_zoomField);

            var refreshBtn = new Button(Refresh) { text = "Refresh" };
            toolbar.Add(refreshBtn);

            _statusLabel = new Label("No UXML selected.");
            _statusLabel.style.unityFontStyleAndWeight = FontStyle.Italic;
            _statusLabel.style.whiteSpace = WhiteSpace.Normal;
            _statusLabel.style.marginBottom = 4;
            root.Add(_statusLabel);

            BuildUrlRow(root);

            var split = new VisualElement();
            split.style.flexDirection = FlexDirection.Row;
            split.style.flexGrow = 1;
            root.Add(split);

            split.Add(BuildUxmlPane());
            split.Add(BuildEmbedPane());

            ApplyPaneSize();
            Refresh();
        }

        void BuildUrlRow(VisualElement root)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.marginBottom = 4;
            root.Add(row);

            var lbl = new Label("URL");
            lbl.style.width = 30;
            row.Add(lbl);

            _urlField = new TextField();
            _urlField.style.flexGrow = 1;
            _urlField.value = EditorPrefs.GetString(Pref + "Url", "");
            _urlField.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
                {
                    e.StopPropagation();
                    StartFetch();
                }
            });
            row.Add(_urlField);

            _flattenToggle = new Toggle("Flatten cascade")
            {
                tooltip = "Pre-evaluate CSS cascade + @media via AngleSharp.Css into inline styles. " +
                          "Helpful when layout depends on media queries; heavy on big pages.",
                value = EditorPrefs.GetBool(Pref + "Flatten", false),
            };
            _flattenToggle.style.marginLeft = 4;
            _flattenToggle.RegisterValueChangedCallback(e => EditorPrefs.SetBool(Pref + "Flatten", e.newValue));
            row.Add(_flattenToggle);

            _fetchBtn = new Button(StartFetch) { text = "Fetch + Preview" };
            _fetchBtn.style.marginLeft = 4;
            row.Add(_fetchBtn);

            _saveBtn = new Button(SaveScratchToUI) { text = "Save to Assets/UI/" };
            _saveBtn.style.marginLeft = 4;
            _saveBtn.SetEnabled(false);
            row.Add(_saveBtn);

            _discardBtn = new Button(DiscardScratch) { text = "Discard" };
            _discardBtn.style.marginLeft = 4;
            _discardBtn.SetEnabled(false);
            row.Add(_discardBtn);

            _fetchStatus = new Label("");
            _fetchStatus.style.unityFontStyleAndWeight = FontStyle.Italic;
            _fetchStatus.style.whiteSpace = WhiteSpace.Normal;
            _fetchStatus.style.marginBottom = 4;
            root.Add(_fetchStatus);
        }

        // ---- URL fetch / convert / preview --------------------------------

        void StartFetch()
        {
            if (_fetchInFlight) { SetFetchStatus("Already fetching…"); return; }
            string url = (_urlField?.value ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(url)) { SetFetchStatus("Enter a URL first."); return; }
            EditorPrefs.SetString(Pref + "Url", url);

            Uri parsed;
            try { parsed = new Uri(url); }
            catch (Exception e) { SetFetchStatus("Bad URL: " + e.Message); return; }
            string slug = Html2UxmlUrlFetcher.SlugFromUri(parsed);

            // Fresh scratch dirs for this slug — wipe any prior attempt so
            // stale image/font files don't leak into the new render.
            string tempRoot = Path.Combine(Path.GetTempPath(), "html2uxml_compare");
            string tempDir = Path.Combine(tempRoot, slug);
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
            Directory.CreateDirectory(tempDir);

            string assetPath = "Assets/" + ScratchAssetFolder + "/" + slug;
            DeleteAssetFolderIfExists(assetPath);

            bool flatten = _flattenToggle?.value ?? false;
            _fetchInFlight = true;
            _fetchBtn?.SetEnabled(false);
            _saveBtn?.SetEnabled(false);
            _discardBtn?.SetEnabled(false);
            SetFetchStatus("Fetching " + url + " …");

            _fetchCts?.Cancel();
            _fetchCts = new CancellationTokenSource();
            var ct = _fetchCts.Token;

            // Network + conversion off the UI thread; marshal back via
            // EditorApplication.delayCall so AssetDatabase calls run on main.
            Task.Run(() =>
            {
                try
                {
                    var fetched = Html2UxmlUrlFetcher.Fetch(url, tempDir, ct, flatten);

                    // Convert the rewritten local HTML into the temp dir as
                    // a full bundle (UI/<slug>.uxml + UI/<slug>.uss + Images/
                    // + Fonts/), with remote-asset downloading enabled so
                    // CSS-referenced images bundle in.
                    var convResult = Pipeline.Convert(new Pipeline.ConvertOptions
                    {
                        SourceHtmlPath = fetched.LocalHtmlPath,
                        OutputDir = tempDir,
                        Name = slug,
                        BundleAssets = true,
                        DownloadFonts = true,
                        UseTextcoreFontAssets = false,
                        SiblingOutput = false,
                    });
                    // AssetWriter.Write doesn't expose its DownloadRemoteAssets
                    // field through Pipeline.ConvertOptions — that flag defaults
                    // false. Most CSS rewriting + bundling already happened
                    // when html refs got resolved to absolute URLs by the
                    // fetcher; if any remote refs survive, they stay as
                    // remote in the USS (Unity will fail to load those, but
                    // it's surfaced as a warning rather than a hard failure).

                    EditorApplication.delayCall += () =>
                        OnFetchSucceeded(fetched, convResult, tempDir, assetPath, slug);
                }
                catch (OperationCanceledException)
                {
                    EditorApplication.delayCall += () => OnFetchCancelled();
                }
                catch (Exception e)
                {
                    EditorApplication.delayCall += () => OnFetchFailed(e);
                }
            }, ct);
        }

        void OnFetchSucceeded(
            Html2UxmlUrlFetcher.FetchResult fetched,
            Pipeline.ConvertResult convResult,
            string tempDir,
            string assetPath,
            string slug)
        {
            _fetchInFlight = false;
            _fetchBtn?.SetEnabled(true);

            // Mirror the converter output (UI/<slug>.uxml, UI/<slug>.uss, +
            // UI/Images/, UI/Fonts/) into Assets/<scratch>/<slug>/ so the
            // VisualTreeAsset is loadable. We deliberately exclude Src/<slug>.html
            // — putting a .html under Assets/ would re-trigger Html2UxmlHtmlImporter
            // and clobber our just-emitted files via SiblingOutput.
            try
            {
                string uiDir = Path.Combine(tempDir, "UI");
                if (!Directory.Exists(uiDir))
                    throw new DirectoryNotFoundException("converter produced no UI/ dir at " + uiDir);

                Directory.CreateDirectory("Assets/" + ScratchAssetFolder);
                string assetAbs = Path.GetFullPath(assetPath);
                if (Directory.Exists(assetAbs)) Directory.Delete(assetAbs, true);
                CopyDirectory(uiDir, assetAbs);
                SanitizeMirroredSvgs(assetAbs);
                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
            }
            catch (Exception e)
            {
                SetFetchStatus("Mirror to Assets failed: " + e.Message);
                Debug.LogException(e);
                return;
            }

            string vtaPath = assetPath + "/" + slug + ".uxml";
            var vta = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(vtaPath);
            if (vta == null)
            {
                SetFetchStatus("Generated UXML not found at " + vtaPath);
                return;
            }

            _scratchAssetPath = assetPath;
            _scratchTempDir = tempDir;
            _scratchSlug = slug;
            _saveBtn?.SetEnabled(true);
            _discardBtn?.SetEnabled(true);

            // Drive the existing preview pipeline by setting the UXML field —
            // Refresh() handles cloning the tree, and we override _htmlAbsPath
            // afterwards so Chrome opens the freshly-rewritten local HTML
            // (the `source:` comment search would otherwise turn up nothing).
            _uxml = vta;
            if (_uxmlField != null) _uxmlField.SetValueWithoutNotify(vta);
            Refresh();

            _htmlAbsPath = fetched.LocalHtmlPath;
            _hwndSearchTitleHint = string.IsNullOrEmpty(fetched.PageTitle)
                ? Path.GetFileNameWithoutExtension(fetched.LocalHtmlPath)
                : fetched.PageTitle;
            KillBrowser();
            LaunchBrowser(fetched.LocalHtmlPath);

            int warnCount = (fetched.Warnings?.Count ?? 0)
                          + (convResult?.Warnings?.Count ?? 0);
            string warnSuffix = warnCount > 0 ? $" ({warnCount} warnings — see Console)" : "";
            SetFetchStatus($"Fetched + previewing as Assets/{ScratchAssetFolder}/{slug}/. Click Save to keep.{warnSuffix}");
            if (fetched.Warnings != null)
                foreach (var w in fetched.Warnings) Debug.LogWarning("[html2uxml fetch] " + w);
            if (convResult?.Warnings != null)
                foreach (var w in convResult.Warnings) Debug.LogWarning("[html2uxml convert] " + w);
        }

        void OnFetchFailed(Exception e)
        {
            _fetchInFlight = false;
            _fetchBtn?.SetEnabled(true);
            SetFetchStatus("Fetch failed: " + e.Message);
            Debug.LogException(e);
        }

        void OnFetchCancelled()
        {
            _fetchInFlight = false;
            _fetchBtn?.SetEnabled(true);
            SetFetchStatus("Fetch cancelled.");
        }

        void SaveScratchToUI()
        {
            if (string.IsNullOrEmpty(_scratchAssetPath) || string.IsNullOrEmpty(_scratchSlug))
            {
                SetFetchStatus("Nothing to save — fetch a URL first.");
                return;
            }
            string destParent = "Assets/UI";
            string destPath = destParent + "/" + _scratchSlug;

            if (!AssetDatabase.IsValidFolder(destParent))
                AssetDatabase.CreateFolder("Assets", "UI");

            if (AssetDatabase.IsValidFolder(destPath))
            {
                bool overwrite = EditorUtility.DisplayDialog(
                    "Overwrite existing bundle?",
                    destPath + " already exists. Overwrite?",
                    "Overwrite", "Cancel");
                if (!overwrite) { SetFetchStatus("Save cancelled."); return; }
                AssetDatabase.DeleteAsset(destPath);
            }

            string err = AssetDatabase.MoveAsset(_scratchAssetPath, destPath);
            if (!string.IsNullOrEmpty(err))
            {
                SetFetchStatus("Save failed: " + err);
                return;
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            string newVtaPath = destPath + "/" + _scratchSlug + ".uxml";
            var vta = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(newVtaPath);
            if (vta != null)
            {
                _uxml = vta;
                if (_uxmlField != null) _uxmlField.SetValueWithoutNotify(vta);
                EditorGUIUtility.PingObject(vta);
            }

            _scratchAssetPath = destPath;
            SetFetchStatus("Saved to " + destPath + "/.");
            // Saved bundle is no longer scratch — disable Discard so the user
            // doesn't accidentally nuke their saved work with one click.
            _discardBtn?.SetEnabled(false);
            _saveBtn?.SetEnabled(false);
        }

        void DiscardScratch()
        {
            if (string.IsNullOrEmpty(_scratchAssetPath))
            {
                SetFetchStatus("Nothing to discard.");
                return;
            }
            if (!_scratchAssetPath.StartsWith("Assets/" + ScratchAssetFolder + "/", StringComparison.Ordinal))
            {
                SetFetchStatus("Refusing to discard — current bundle is no longer scratch.");
                return;
            }
            KillBrowser();
            DeleteAssetFolderIfExists(_scratchAssetPath);
            _scratchAssetPath = null;
            _scratchTempDir = null;
            _scratchSlug = null;
            _saveBtn?.SetEnabled(false);
            _discardBtn?.SetEnabled(false);
            _uxml = null;
            if (_uxmlField != null) _uxmlField.SetValueWithoutNotify(null);
            Refresh();
            SetFetchStatus("Discarded scratch bundle.");
        }

        static void DeleteAssetFolderIfExists(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return;
            if (AssetDatabase.IsValidFolder(assetPath))
                AssetDatabase.DeleteAsset(assetPath);
        }

        // Walk every .svg under the mirrored bundle and strip CSS var(--*)
        // refs that Unity's SVGImporter can't resolve. Inline <svg> blocks
        // that the converter materialises into UI/ can also carry these.
        static void SanitizeMirroredSvgs(string root)
        {
            if (!Directory.Exists(root)) return;
            foreach (var svgPath in Directory.GetFiles(root, "*.svg", SearchOption.AllDirectories))
            {
                try
                {
                    string text = File.ReadAllText(svgPath);
                    string sanitized = Html2UxmlUrlFetcher.SanitizeSvgText(text);
                    if (!ReferenceEquals(text, sanitized) && text != sanitized)
                        File.WriteAllText(svgPath, sanitized, new UTF8Encoding(false));
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[html2uxml] SVG sanitize failed for {svgPath}: {e.Message}");
                }
            }
        }

        static void CopyDirectory(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (var file in Directory.GetFiles(src))
                File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), overwrite: true);
            foreach (var dir in Directory.GetDirectories(src))
                CopyDirectory(dir, Path.Combine(dst, Path.GetFileName(dir)));
        }

        void SetFetchStatus(string s)
        {
            if (_fetchStatus != null) _fetchStatus.text = s;
        }

        VisualElement BuildUxmlPane()
        {
            var col = MakeColumn("UXML");
            var scroll = new ScrollView(ScrollViewMode.VerticalAndHorizontal);
            scroll.style.flexGrow = 1;
            AddBorder(scroll);
            col.Add(scroll);

            _uxmlHost = new VisualElement();
            _uxmlHost.style.flexShrink = 0;
            _uxmlHost.style.backgroundColor = new Color(0.1f, 0.1f, 0.1f, 1f);
            scroll.Add(_uxmlHost);
            return col;
        }

        VisualElement BuildEmbedPane()
        {
            var col = MakeColumn("HTML (embedded Chrome)");

            _embedPane = new VisualElement();
            _embedPane.style.flexGrow = 1;
            _embedPane.style.flexShrink = 1;
            _embedPane.style.backgroundColor = new Color(0.05f, 0.05f, 0.05f, 1f);
            AddBorder(_embedPane);
            col.Add(_embedPane);

            _embedPlaceholder = new Label("(no HTML — select a converted UXML)");
            _embedPlaceholder.style.position = Position.Absolute;
            _embedPlaceholder.style.top = 8;
            _embedPlaceholder.style.left = 8;
            _embedPlaceholder.style.color = new Color(0.7f, 0.7f, 0.7f);
            _embedPane.Add(_embedPlaceholder);

            return col;
        }

        static VisualElement MakeColumn(string title)
        {
            var col = new VisualElement();
            col.style.flexGrow = 1;
            col.style.flexBasis = 0;
            col.style.flexDirection = FlexDirection.Column;
            col.style.marginLeft = 2;
            col.style.marginRight = 2;
            var header = new Label(title);
            header.style.unityFontStyleAndWeight = FontStyle.Bold;
            header.style.marginBottom = 2;
            col.Add(header);
            return col;
        }

        static void AddBorder(VisualElement e)
        {
            var border = new Color(0f, 0f, 0f, 0.4f);
            e.style.borderLeftWidth = e.style.borderRightWidth =
                e.style.borderTopWidth = e.style.borderBottomWidth = 1;
            e.style.borderLeftColor = e.style.borderRightColor =
                e.style.borderTopColor = e.style.borderBottomColor = border;
        }

        void ApplyPaneSize()
        {
            int w = Mathf.Max(1, _widthField?.value ?? 1920);
            int h = Mathf.Max(1, _heightField?.value ?? 1080);
            if (_uxmlHost != null)
            {
                _uxmlHost.style.width = w;
                _uxmlHost.style.height = h;
            }
            ApplyUxmlZoom();
            // Embed pane fills available space; Chrome HWND tracks _embedPane.worldBound.
        }

        float ZoomFactor => Mathf.Clamp((_zoomField?.value ?? 100) / 100f, 0.1f, 4f);

        void ApplyUxmlZoom()
        {
            if (_uxmlHost == null) return;
            // Defer the mutation off the current call chain. Setting scale /
            // transformOrigin while a generateVisualContent callback is
            // mid-execution (which Html2UxmlPanel/Paint use for gradients,
            // shadows) throws InvalidOperationException; scheduling to the
            // next tick keeps the change outside that window.
            _uxmlHost.schedule.Execute(() =>
            {
                if (_uxmlHost == null) return;
                float z = ZoomFactor;
                _uxmlHost.style.transformOrigin = new StyleTransformOrigin(
                    new TransformOrigin(new Length(0f, LengthUnit.Pixel), new Length(0f, LengthUnit.Pixel)));
                _uxmlHost.style.scale = new StyleScale(new Scale(new Vector3(z, z, 1f)));
                int w = Mathf.Max(1, _widthField?.value ?? 1920);
                int h = Mathf.Max(1, _heightField?.value ?? 1080);
                _uxmlHost.style.marginRight = w * (z - 1f);
                _uxmlHost.style.marginBottom = h * (z - 1f);
            });
        }

        void ScheduleBrowserRelaunch()
        {
            // Debounce — IntegerField fires on every keystroke; relaunching
            // Chrome that often is awful. Wait 600ms of quiet before relaunch.
            _zoomRelaunchAt = EditorApplication.timeSinceStartup + 0.6;
            _zoomRelaunchScheduled = true;
        }

        void Refresh()
        {
            if (_uxmlHost == null) return;
            _uxmlHost.Clear();
            _htmlAbsPath = null;
            KillBrowser();
            SetEmbedPlaceholder("(no HTML — select a converted UXML)");

            if (_uxml == null) { SetStatus("No UXML selected."); return; }

            string assetPath = AssetDatabase.GetAssetPath(_uxml);
            if (string.IsNullOrEmpty(assetPath)) { SetStatus("UXML has no on-disk asset path."); return; }

            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
            var fresh = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(assetPath);
            if (fresh != null) _uxml = fresh;

            try { _uxml.CloneTree(_uxmlHost); }
            catch (Exception e) { SetStatus("UXML clone failed: " + e.Message); return; }
            ApplyPaneSize();

            _htmlAbsPath = ResolveHtmlAbsPath(assetPath);
            if (_htmlAbsPath == null)
            {
                SetStatus($"UXML: {assetPath}\nHTML: no `source:` comment found — reconvert via the importer to seed Src/<base>.html.");
                SetEmbedPlaceholder("(no HTML source)");
                return;
            }

            SetStatus($"UXML: {assetPath}\nHTML: {_htmlAbsPath}");
            LaunchBrowser(_htmlAbsPath);
        }

        static readonly Regex _sourceCommentRx = new Regex(
            @"<!--\s*source:\s*(?<rel>[^\s<>]+)\s*-->", RegexOptions.IgnoreCase);

        static string ResolveHtmlAbsPath(string uxmlAssetPath)
        {
            try
            {
                string projectRoot = Path.GetDirectoryName(Application.dataPath);
                string uxmlAbs = Path.Combine(projectRoot, uxmlAssetPath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(uxmlAbs)) return null;
                using var sr = new StreamReader(uxmlAbs);
                for (int i = 0; i < 8; i++)
                {
                    string line = sr.ReadLine();
                    if (line == null) break;
                    var m = _sourceCommentRx.Match(line);
                    if (!m.Success) continue;
                    string rel = m.Groups["rel"].Value;
                    string uxmlDir = Path.GetDirectoryName(uxmlAbs);
                    string combined = Path.GetFullPath(Path.Combine(uxmlDir, rel));
                    return File.Exists(combined) ? combined : null;
                }
            }
            catch { /* unreadable file — fall through */ }
            return null;
        }

        // ---- Browser spawn + reparent ------------------------------------

        void LaunchBrowser(string htmlPath)
        {
            string exe = FindBrowserExe();
            if (exe == null)
            {
                SetEmbedPlaceholder("Chrome/Edge not found. Install Chrome or set CHROME_PATH.");
                return;
            }

            string profile = Path.Combine(Path.GetTempPath(), "html2uxml_compare_profile");
            try { Directory.CreateDirectory(profile); } catch { }

            // Title hint: Chrome shows the file basename in the window title
            // in --app mode. Use it to disambiguate from any other Chrome
            // instances when we EnumWindows for the HWND.
            _hwndSearchTitleHint = Path.GetFileNameWithoutExtension(htmlPath);

            string uri = new Uri(htmlPath).AbsoluteUri;
            float dsf = ZoomFactor;
            // Spawn offscreen so the chromeless window doesn't flash on the
            // user's desktop before we reparent + reposition it.
            string args =
                $"--app=\"{uri}\" --user-data-dir=\"{profile}\" " +
                "--no-first-run --no-default-browser-check " +
                "--disable-features=Translate,AutofillKeyPressed " +
                $"--force-device-scale-factor={dsf.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} " +
                "--window-size=800,600 --window-position=-32000,-32000";

            try
            {
                _browser = Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = false,
                });
            }
            catch (Exception e)
            {
                SetEmbedPlaceholder("Browser launch failed: " + e.Message);
                Debug.LogException(e);
                return;
            }

            _hwndSearchDeadline = EditorApplication.timeSinceStartup + 10.0;
            SetEmbedPlaceholder("Launching Chrome…");
        }

        void Tick()
        {
            if (_embedPane == null) return;

            if (_zoomRelaunchScheduled && EditorApplication.timeSinceStartup >= _zoomRelaunchAt)
            {
                _zoomRelaunchScheduled = false;
                if (_htmlAbsPath != null)
                {
                    KillBrowser();
                    LaunchBrowser(_htmlAbsPath);
                }
            }

            // Stop early if no browser session in flight.
            if (_browserHwnd == IntPtr.Zero && _browser == null) return;

            // Did chrome die on us?
            if (_browser != null)
            {
                try { if (_browser.HasExited) { TeardownReparent(); _browser = null; _browserHwnd = IntPtr.Zero;
                        SetEmbedPlaceholder("Chrome exited."); return; } }
                catch { /* race with disposal */ }
            }

            // Discovery phase — look for our HWND.
            if (_browserHwnd == IntPtr.Zero)
            {
                if (EditorApplication.timeSinceStartup > _hwndSearchDeadline)
                {
                    SetEmbedPlaceholder("Could not find Chrome window after 10s.");
                    KillBrowser();
                    return;
                }
                IntPtr found = FindChromeAppHwnd(_hwndSearchTitleHint);
                if (found == IntPtr.Zero) return;
                AttachBrowser(found);
                if (_browserHwnd == IntPtr.Zero) return;
            }

            // Sync phase.
            if (!IsWindow(_browserHwnd)) { _browserHwnd = IntPtr.Zero; return; }

            var wb = _embedPane.worldBound;
            if (wb.width < 20 || wb.height < 20 || !hasFocus && !docked)
            {
                // Pane collapsed or window hidden — hide the child HWND so it
                // doesn't bleed over other UI.
                ShowWindow(_browserHwnd, SW_HIDE);
                return;
            }

            // worldBound is in panel-local pixels. EditorWindow.position is
            // screen pixels. Combine for a screen rect, then convert to the
            // Unity main HWND's client coords (which is what SetWindowPos
            // expects for a child HWND).
            Vector2 screenPt = new Vector2(position.x + wb.x, position.y + wb.y);
            POINT pt = new POINT { X = (int)screenPt.x, Y = (int)screenPt.y };
            ScreenToClient(_unityHwnd, ref pt);

            ShowWindow(_browserHwnd, SW_SHOWNA);
            SetWindowPos(_browserHwnd, IntPtr.Zero, pt.X, pt.Y,
                         (int)wb.width, (int)wb.height,
                         SWP_NOZORDER | SWP_NOACTIVATE);

            if (_embedPlaceholder != null) _embedPlaceholder.style.display = DisplayStyle.None;
        }

        void AttachBrowser(IntPtr hwnd)
        {
            _browserOrigStyle = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
            _browserOrigExStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            _browserOrigParent = GetParent(hwnd);

            long newStyle = _browserOrigStyle;
            newStyle &= ~(long)(WS_CAPTION | WS_THICKFRAME | WS_POPUP | WS_OVERLAPPED |
                                WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX);
            newStyle |= WS_CHILD | WS_VISIBLE;
            SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(newStyle));

            long newEx = _browserOrigExStyle;
            newEx &= ~(long)(WS_EX_DLGMODALFRAME | WS_EX_CLIENTEDGE | WS_EX_STATICEDGE | WS_EX_WINDOWEDGE);
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(newEx));

            if (SetParent(hwnd, _unityHwnd) == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                Debug.LogWarning($"[Html2Uxml] SetParent failed (Win32 {err}); browser will float instead.");
            }

            // Apply style change so the WM picks up the new frame.
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                         SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);

            _browserHwnd = hwnd;
        }

        void TeardownReparent()
        {
            if (_browserHwnd == IntPtr.Zero || !IsWindow(_browserHwnd)) return;
            try
            {
                SetWindowLongPtr(_browserHwnd, GWL_STYLE, new IntPtr(_browserOrigStyle));
                SetWindowLongPtr(_browserHwnd, GWL_EXSTYLE, new IntPtr(_browserOrigExStyle));
                SetParent(_browserHwnd, _browserOrigParent);
            }
            catch { /* HWND already gone */ }
        }

        void KillBrowser()
        {
            TeardownReparent();
            try
            {
                if (_browser != null && !_browser.HasExited)
                {
                    _browser.CloseMainWindow();
                    if (!_browser.WaitForExit(500)) _browser.Kill();
                }
            }
            catch { /* already gone */ }
            _browser = null;
            _browserHwnd = IntPtr.Zero;
        }

        // ---- HWND discovery -----------------------------------------------

        static IntPtr FindChromeAppHwnd(string titleHint)
        {
            var match = new ChromeFinder { titleHint = titleHint ?? "" };
            EnumWindows(match.Callback, IntPtr.Zero);
            return match.found;
        }

        class ChromeFinder
        {
            public string titleHint;
            public IntPtr found = IntPtr.Zero;
            readonly StringBuilder _buf = new StringBuilder(512);

            public bool Callback(IntPtr hwnd, IntPtr lparam)
            {
                if (!IsWindowVisible(hwnd)) return true;
                if (GetParent(hwnd) != IntPtr.Zero) return true;

                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == 0) return true;

                string procName;
                try { procName = Process.GetProcessById((int)pid).ProcessName; }
                catch { return true; }
                if (!procName.Equals("chrome", StringComparison.OrdinalIgnoreCase) &&
                    !procName.Equals("msedge", StringComparison.OrdinalIgnoreCase))
                    return true;

                _buf.Length = 0;
                GetWindowText(hwnd, _buf, _buf.Capacity);
                string title = _buf.ToString();
                if (!string.IsNullOrEmpty(titleHint) && !title.Contains(titleHint, StringComparison.OrdinalIgnoreCase))
                    return true;

                // Skip the headless renderer / GPU / utility windows: Chrome's
                // visible --app window has a non-empty title and the class
                // name "Chrome_WidgetWin_1".
                _buf.Length = 0;
                GetClassName(hwnd, _buf, _buf.Capacity);
                if (!_buf.ToString().StartsWith("Chrome_WidgetWin", StringComparison.Ordinal))
                    return true;

                found = hwnd;
                return false;
            }
        }

        static string FindBrowserExe()
        {
            string env = Environment.GetEnvironmentVariable("CHROME_PATH");
            if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
            string[] candidates =
            {
                @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
                @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
                @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
            };
            foreach (var c in candidates) if (File.Exists(c)) return c;
            return null;
        }

        void SetStatus(string s) { if (_statusLabel != null) _statusLabel.text = s; }
        void SetEmbedPlaceholder(string s)
        {
            if (_embedPlaceholder == null) return;
            _embedPlaceholder.text = s;
            _embedPlaceholder.style.display = DisplayStyle.Flex;
        }

        // ---- Win32 P/Invoke -----------------------------------------------

        const int GWL_STYLE = -16;
        const int GWL_EXSTYLE = -20;
        const long WS_CHILD = 0x40000000L;
        const long WS_VISIBLE = 0x10000000L;
        const long WS_POPUP = 0x80000000L;
        const long WS_OVERLAPPED = 0x00000000L;
        const long WS_CAPTION = 0x00C00000L;
        const long WS_THICKFRAME = 0x00040000L;
        const long WS_SYSMENU = 0x00080000L;
        const long WS_MINIMIZEBOX = 0x00020000L;
        const long WS_MAXIMIZEBOX = 0x00010000L;
        const long WS_EX_DLGMODALFRAME = 0x00000001L;
        const long WS_EX_CLIENTEDGE = 0x00000200L;
        const long WS_EX_STATICEDGE = 0x00020000L;
        const long WS_EX_WINDOWEDGE = 0x00000100L;

        const uint SWP_NOSIZE = 0x0001;
        const uint SWP_NOMOVE = 0x0002;
        const uint SWP_NOZORDER = 0x0004;
        const uint SWP_NOACTIVATE = 0x0010;
        const uint SWP_FRAMECHANGED = 0x0020;

        const int SW_HIDE = 0;
        const int SW_SHOWNA = 8;

        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int X; public int Y; }

        delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lparam);

        [DllImport("user32.dll")]
        static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lparam);
        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SetParent(IntPtr hwndChild, IntPtr hwndNewParent);
        [DllImport("user32.dll")]
        static extern IntPtr GetParent(IntPtr hwnd);
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool SetWindowPos(IntPtr hwnd, IntPtr hwndInsertAfter, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hwnd, int cmd);
        [DllImport("user32.dll")]
        static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        static extern int GetClassName(IntPtr hwnd, StringBuilder text, int maxCount);
        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool ScreenToClient(IntPtr hwnd, ref POINT pt);

        // Use SetWindowLongPtr to be 64-bit clean. On 32-bit hosts the runtime
        // alias falls back to SetWindowLong.
        static IntPtr GetWindowLongPtr(IntPtr hwnd, int idx)
        {
            return IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, idx) : new IntPtr(GetWindowLong32(hwnd, idx));
        }
        static IntPtr SetWindowLongPtr(IntPtr hwnd, int idx, IntPtr value)
        {
            return IntPtr.Size == 8
                ? SetWindowLongPtr64(hwnd, idx, value)
                : new IntPtr(SetWindowLong32(hwnd, idx, value.ToInt32()));
        }

        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        static extern int GetWindowLong32(IntPtr hwnd, int idx);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
        static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int idx);
        [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
        static extern int SetWindowLong32(IntPtr hwnd, int idx, int value);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
        static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int idx, IntPtr value);
    }
}
