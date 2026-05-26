using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace ODDGames.Html2Uxml.Editor
{
    // Walks a single subtree (rooted at a CSS-class selector) in both the
    // Chrome render of the source HTML and the UXML render of the
    // converter output, then prints a side-by-side hierarchy with rect
    // + key style diffs per node. Built so converter-fidelity work can
    // proceed one element at a time: pick a selector, see exactly which
    // child has the wrong width / padding / font / margin, fix it, repeat.
    public static class Html2UxmlSubtreeCompare
    {
        public sealed class Node
        {
            public string Tag;        // <div>, ui:Button, etc
            public string ClassChain; // "mt-replay__cam-btn mt-replay__cam-btn--active"
            public double W, H, X, Y;
            public double PaddingL, PaddingT, PaddingR, PaddingB;
            public double MarginL, MarginT, MarginR, MarginB;
            public string FontFamily;
            public double FontSizePx;
            public double LetterSpacingPx;
            public string Color;
            public string Text;
            public List<Node> Children = new List<Node>();
        }

        public static string Compare(
            string htmlPath, string uxmlPath, string rootClass,
            int viewportWidth = 1438, int viewportHeight = 788)
        {
            if (string.IsNullOrEmpty(rootClass)) throw new ArgumentException("rootClass required");

            var htmlRoot = DumpHtmlSubtree(htmlPath, rootClass, viewportWidth, viewportHeight);
            var uxmlRoot = DumpUxmlSubtree(uxmlPath, rootClass, viewportWidth, viewportHeight);

            var sb = new StringBuilder();
            sb.AppendLine("== Subtree compare for ." + rootClass + " ==");
            Walk(htmlRoot, uxmlRoot, sb, 0);
            return sb.ToString();
        }

        static void Walk(Node h, Node u, StringBuilder sb, int depth)
        {
            string indent = new string(' ', depth * 2);
            string cls = (h?.ClassChain ?? u?.ClassChain ?? "?").Trim();
            if (cls.Length > 42) cls = cls.Substring(0, 42) + "…";

            string hRect = h == null ? "-"
                : $"({Round(h.W)},{Round(h.H)})";
            string uRect = u == null ? "-"
                : $"({Round(u.W)},{Round(u.H)})";
            string delta;
            if (h != null && u != null)
            {
                double dw = u.W - h.W, dh = u.H - h.H;
                delta = $"Δ({Round(dw)},{Round(dh)})";
            }
            else delta = "";

            sb.AppendLine($"{indent}{cls,-44} {hRect,-13} {uRect,-13} {delta}");

            // Highlight notable style diffs.
            if (h != null && u != null)
            {
                EmitStyleDiff(indent, h, u, sb);
            }

            // Pair children by index — same DOM order on both sides, since
            // the converter preserves source order. When trees diverge the
            // missing side becomes null.
            var hc = h?.Children ?? new List<Node>();
            var uc = u?.Children ?? new List<Node>();
            int n = Math.Max(hc.Count, uc.Count);
            for (int i = 0; i < n; i++)
            {
                Walk(i < hc.Count ? hc[i] : null,
                     i < uc.Count ? uc[i] : null,
                     sb, depth + 1);
            }
        }

        static void EmitStyleDiff(string indent, Node h, Node u, StringBuilder sb)
        {
            void Line(string label, string hv, string uv)
            {
                if (hv != uv) sb.AppendLine($"{indent}    {label,-12} html={hv} uxml={uv}");
            }
            Line("padding",
                $"{Round(h.PaddingT)} {Round(h.PaddingR)} {Round(h.PaddingB)} {Round(h.PaddingL)}",
                $"{Round(u.PaddingT)} {Round(u.PaddingR)} {Round(u.PaddingB)} {Round(u.PaddingL)}");
            Line("font-size", $"{Round(h.FontSizePx)}", $"{Round(u.FontSizePx)}");
            Line("letter-sp", $"{Round(h.LetterSpacingPx)}", $"{Round(u.LetterSpacingPx)}");
            if (!string.IsNullOrEmpty(h.Text) || !string.IsNullOrEmpty(u.Text))
                Line("text", Trunc(h.Text), Trunc(u.Text));
        }

        static string Round(double v)
        {
            if (double.IsNaN(v)) return "-";
            return v.ToString("0.#", CultureInfo.InvariantCulture);
        }
        static string Trunc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "\"\"";
            if (s.Length > 24) s = s.Substring(0, 24) + "…";
            return "\"" + s + "\"";
        }

        // ============================================================
        // HTML side — drive Chrome via CDP (System.Net.WebSockets).
        // ============================================================

        static Node DumpHtmlSubtree(string htmlPath, string rootClass, int vw, int vh)
        {
            string js = @"
(function(rootCls) {
    const root = document.querySelector('.' + rootCls);
    if (!root) return null;
    function walk(el) {
        const s = getComputedStyle(el);
        const r = el.getBoundingClientRect();
        const cls = [...el.classList].filter(c => !c.startsWith('h2u-') && !c.startsWith('unity-')).join(' ');
        return {
            tag: el.tagName.toLowerCase(),
            cls: cls,
            w: r.width, h: r.height, x: r.left, y: r.top,
            pl: parseFloat(s.paddingLeft), pt: parseFloat(s.paddingTop),
            pr: parseFloat(s.paddingRight), pb: parseFloat(s.paddingBottom),
            ml: parseFloat(s.marginLeft), mt: parseFloat(s.marginTop),
            mr: parseFloat(s.marginRight), mb: parseFloat(s.marginBottom),
            ff: s.fontFamily, fs: parseFloat(s.fontSize),
            ls: parseFloat(s.letterSpacing) || 0,
            color: s.color,
            text: el.children.length === 0 ? (el.textContent || '').trim().slice(0, 64) : '',
            children: [...el.children].map(walk),
        };
    }
    return walk(root);
})('" + rootClass.Replace("'", "\\'") + "');";

            string raw = ChromeRunJs(htmlPath, js, vw, vh);
            return ParseHtmlNode(raw);
        }

        static string ChromeRunJs(string htmlPath, string js, int vw, int vh)
        {
            // Reuse the existing dumper's Chrome plumbing by spawning a
            // headless instance directly here — Html2UxmlChromeLayoutDump
            // wraps the same protocol but only returns aggregated rects.
            string exe = FindChrome() ?? throw new InvalidOperationException("Chrome not found");
            int port = NextFreePort();
            string profile = Path.Combine(Path.GetTempPath(), "h2u_subtree_chrome_" + port);
            try { Directory.CreateDirectory(profile); } catch { }

            var args =
                "--headless --disable-gpu --no-sandbox --no-first-run --hide-scrollbars " +
                "--no-default-browser-check --remote-allow-origins=* " +
                $"--remote-debugging-port={port} --window-size={vw},{vh} " +
                $"--user-data-dir=\"{profile}\" \"{new Uri(htmlPath).AbsoluteUri}\"";

            var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe, Arguments = args,
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true, RedirectStandardOutput = true,
            });
            try
            {
                var task = Task.Run(() => DriveCdp(port, js));
                if (!task.Wait(30000))
                    throw new TimeoutException("Chrome CDP timed out");
                return task.Result;
            }
            finally
            {
                try { if (proc != null && !proc.HasExited) proc.Kill(); } catch { }
                try { if (Directory.Exists(profile)) Directory.Delete(profile, true); } catch { }
            }
        }

        static async Task<string> DriveCdp(int port, string js)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            string targetWs = null;
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline && targetWs == null)
            {
                try
                {
                    string body = await http.GetStringAsync($"http://127.0.0.1:{port}/json");
                    foreach (Match m in Regex.Matches(body, "\\{[^{}]*?\\}", RegexOptions.Singleline))
                    {
                        if (!m.Value.Contains("\"type\": \"page\"")
                            && !m.Value.Contains("\"type\":\"page\"")) continue;
                        var w = Regex.Match(m.Value, "\"webSocketDebuggerUrl\"\\s*:\\s*\"(?<u>ws://[^\"]+)\"");
                        if (w.Success) { targetWs = w.Groups["u"].Value; break; }
                    }
                }
                catch { }
                if (targetWs == null) await Task.Delay(150);
            }
            if (targetWs == null) throw new TimeoutException("page target not found");

            using var ws = new ClientWebSocket();
            using (var ctsConn = new CancellationTokenSource(5000))
                await ws.ConnectAsync(new Uri(targetWs), ctsConn.Token);

            int id = 1;
            async Task<string> Send(string method, string p)
            {
                int myId = id++;
                string msg = "{\"id\":" + myId + ",\"method\":\"" + method + "\""
                           + (p == null ? "" : ",\"params\":" + p) + "}";
                await ws.SendAsync(Encoding.UTF8.GetBytes(msg), WebSocketMessageType.Text, true, CancellationToken.None);
                while (true)
                {
                    string r = await Recv(ws);
                    if (r.Contains("\"id\":" + myId + ",") || r.Contains("\"id\":" + myId + "}"))
                        return r;
                }
            }

            // Wait for readyState complete.
            for (int i = 0; i < 50; i++)
            {
                string s = await Send("Runtime.evaluate", "{\"expression\":\"document.readyState\",\"returnByValue\":true}");
                if (s.Contains("\"value\":\"complete\"")) break;
                await Task.Delay(150);
            }
            await Task.Delay(300);

            string resp = await Send("Runtime.evaluate",
                "{\"expression\":" + Json(js) + ",\"returnByValue\":true}");
            return resp;
        }

        static async Task<string> Recv(ClientWebSocket ws)
        {
            using var cts = new CancellationTokenSource(10000);
            var buf = new byte[16384];
            var sb = new StringBuilder();
            while (true)
            {
                var seg = new ArraySegment<byte>(buf);
                var r = await ws.ReceiveAsync(seg, cts.Token);
                sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
                if (r.EndOfMessage) return sb.ToString();
            }
        }

        static string Json(string raw)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in raw)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        static Node ParseHtmlNode(string cdpResp)
        {
            // CDP returns {"result":{"result":{"type":"object","value":{...tree...}}}}
            // returnByValue=true means the value is the raw JSON object, not stringified.
            // Find the inner "value" : { ... } block and parse our tree.
            int valIdx = cdpResp.IndexOf("\"value\"");
            if (valIdx < 0) return null;
            // Skip "value":<space?>{ to land on the object.
            int braceStart = cdpResp.IndexOf('{', valIdx);
            if (braceStart < 0) return null;
            string objJson = ExtractBalanced(cdpResp, braceStart);
            return objJson == null ? null : NodeFromJson(objJson);
        }

        static string ExtractBalanced(string s, int start)
        {
            if (start < 0 || start >= s.Length || s[start] != '{') return null;
            int depth = 0; bool inStr = false; bool esc = false;
            for (int i = start; i < s.Length; i++)
            {
                char c = s[i];
                if (esc) { esc = false; continue; }
                if (c == '\\') { esc = true; continue; }
                if (c == '"') { inStr = !inStr; continue; }
                if (inStr) continue;
                if (c == '{') depth++;
                else if (c == '}') { depth--; if (depth == 0) return s.Substring(start, i - start + 1); }
            }
            return null;
        }

        static Node NodeFromJson(string json)
        {
            var n = new Node();
            n.Tag = JS(json, "tag");
            n.ClassChain = JS(json, "cls");
            n.W = JN(json, "w"); n.H = JN(json, "h"); n.X = JN(json, "x"); n.Y = JN(json, "y");
            n.PaddingL = JN(json, "pl"); n.PaddingT = JN(json, "pt");
            n.PaddingR = JN(json, "pr"); n.PaddingB = JN(json, "pb");
            n.MarginL = JN(json, "ml"); n.MarginT = JN(json, "mt");
            n.MarginR = JN(json, "mr"); n.MarginB = JN(json, "mb");
            n.FontFamily = JS(json, "ff");
            n.FontSizePx = JN(json, "fs");
            n.LetterSpacingPx = JN(json, "ls");
            n.Color = JS(json, "color");
            n.Text = JS(json, "text");

            // children: [ {...}, {...} ]
            int kidsIdx = json.IndexOf("\"children\"");
            if (kidsIdx >= 0)
            {
                int br = json.IndexOf('[', kidsIdx);
                if (br > 0)
                {
                    int depth = 0; bool inStr = false; bool esc = false;
                    int i = br;
                    for (; i < json.Length; i++)
                    {
                        char c = json[i];
                        if (esc) { esc = false; continue; }
                        if (c == '\\') { esc = true; continue; }
                        if (c == '"') { inStr = !inStr; continue; }
                        if (inStr) continue;
                        if (c == '[') depth++;
                        else if (c == ']') { depth--; if (depth == 0) break; }
                        else if (c == '{' && depth == 1)
                        {
                            string sub = ExtractBalanced(json, i);
                            if (sub != null) { n.Children.Add(NodeFromJson(sub)); i += sub.Length - 1; }
                        }
                    }
                }
            }
            return n;
        }

        static string JS(string json, string key)
        {
            var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"(?<v>(?:\\\\.|[^\"\\\\])*)\"");
            return m.Success ? Regex.Unescape(m.Groups["v"].Value)
                .Replace("\\\"", "\"").Replace("\\\\", "\\") : "";
        }
        static double JN(string json, string key)
        {
            var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*(-?[0-9.]+)");
            if (!m.Success) return double.NaN;
            return double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN;
        }

        // ============================================================
        // UXML side — render in hidden EditorWindow, walk tree.
        // ============================================================

        static Node DumpUxmlSubtree(string uxmlPath, string rootClass, int vw, int vh)
        {
            AssetDatabase.ImportAsset(uxmlPath, ImportAssetOptions.ForceUpdate);
            var vta = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(uxmlPath);
            if (vta == null) return null;

            var window = ScriptableObject.CreateInstance<EditorWindow>();
            try
            {
                window.position = new Rect(20000, 20000, vw, vh);
                window.minSize = new Vector2(vw, vh);
                window.maxSize = new Vector2(vw, vh);
                // Show() drives a more complete layout pump than
                // ShowAuxWindow — descendant Labels inside buttons stay
                // NaN under the aux path because the Button class only
                // sizes its own text node on the lightweight pump.
                window.Show();
                window.Focus();

                var root = window.rootVisualElement;
                root.style.width = vw; root.style.height = vh;
                vta.CloneTree(root);
                root.MarkDirtyRepaint();

                var pump = typeof(EditorApplication).GetMethod(
                    "Internal_CallUpdateFunctions",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                var panel = root.panel as object;
                var panelUpdate = panel?.GetType().GetMethod(
                    "UpdateForRepaint",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    ?? panel?.GetType().GetMethod(
                        "ValidateLayout",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                // Pump editor-app updates AND drive a full panel update
                // pass (styles + layout + repaint). Without the style
                // pass, resolvedStyle queries on deeply-nested elements
                // return UI Toolkit defaults (font-size=14, etc) instead
                // of the values their USS rules specify.
                var panelType = panel?.GetType();
                System.Reflection.MethodInfo[] passes = panelType == null ? new System.Reflection.MethodInfo[0]
                    : new[] {
                        panelType.GetMethod("UpdateBindings", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance),
                        panelType.GetMethod("UpdateAnimations", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance),
                        panelType.GetMethod("UpdateStyle", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance),
                        panelType.GetMethod("ValidateLayout", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance),
                        panelType.GetMethod("UpdateForRepaint", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance),
                    };
                for (int i = 0; i < 30; i++)
                {
                    pump?.Invoke(null, null);
                    foreach (var pi in passes) try { pi?.Invoke(panel, null); } catch { }
                    var _ = root.layout;
                }

                string logPath = Path.Combine(Path.GetTempPath(), "h2u_subtree.log");
                File.AppendAllText(logPath, $"root.worldBound={root.worldBound} class='{rootClass}'\n");
                var match = root.Q(null, rootClass);
                if (match == null) { File.AppendAllText(logPath, "no match\n"); return null; }
                File.AppendAllText(logPath, $"match.worldBound={match.worldBound} layout={match.layout} resolvedW={match.resolvedStyle.width} resolvedH={match.resolvedStyle.height}\n");
                return Snap(match);
            }
            finally
            {
                if (window != null) window.Close();
            }
        }

        static Node Snap(VisualElement el)
        {
            var rs = el.resolvedStyle;
            // worldBound is the one that's actually populated after the
            // editor pump runs — el.layout often stays NaN until a real
            // repaint fires. We only need width/height for the diff, so
            // the world offset doesn't matter.
            var rect = el.worldBound;
            string cls = string.Join(" ", FilterClasses(el.GetClasses()));
            string text = "";
            if (el is TextElement te) text = te.text ?? "";

            try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "h2u_subtree.log"),
                $"Snap '{cls}' rect={rect}\n"); } catch { }
            var n = new Node
            {
                Tag = el.GetType().Name,
                ClassChain = cls,
                W = rect.width, H = rect.height, X = rect.x, Y = rect.y,
                PaddingL = rs.paddingLeft, PaddingT = rs.paddingTop,
                PaddingR = rs.paddingRight, PaddingB = rs.paddingBottom,
                MarginL = rs.marginLeft, MarginT = rs.marginTop,
                MarginR = rs.marginRight, MarginB = rs.marginBottom,
                FontFamily = rs.unityFont == null ? "" : rs.unityFont.name,
                FontSizePx = rs.fontSize,
                LetterSpacingPx = rs.letterSpacing,
                Color = ColorToStr(rs.color),
                Text = text,
            };
            try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "h2u_subtree.log"),
                $"  '{cls}' hChildren={el.hierarchy.childCount}\n"); } catch { }
            for (int i = 0; i < el.hierarchy.childCount; i++)
            {
                var child = el.hierarchy[i];
                try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "h2u_subtree.log"),
                    $"    -> child[{i}] type={child.GetType().Name} classes={string.Join(",",child.GetClasses())}\n"); } catch { }
                var cclasses = child.GetClasses();
                bool internalPart = false;
                bool isLayoutWrapper = true;
                int classCount = 0;
                foreach (var c in cclasses)
                {
                    classCount++;
                    // html2uxml-button-text is the auto-Label Unity Button
                    // injects. Skip it so the synthetic node doesn't show
                    // up alongside the author's own children. Do NOT also
                    // filter `unity-button` — that's the class Html2UxmlButton
                    // inherits from UnityEngine.UIElements.Button, and the
                    // button IS an author node we want to compare.
                    if (c == "html2uxml-button-text")
                    { internalPart = true; break; }
                    // Anything that isn't the synthetic h2u-button-row-*
                    // wrapper is a real author element — surface it as a
                    // proper node and stop treating the child as a
                    // transparent wrapper.
                    if (!c.StartsWith("h2u-button-row")) isLayoutWrapper = false;
                }
                if (internalPart) continue;

                // Auto-inserted inline-flow wrappers don't exist in the
                // source HTML; flatten them so child indices line up
                // against the browser DOM. Without this, HTML cam-btn
                // pairs with UXML wrapper and the whole subtree diverges.
                if (classCount > 0 && isLayoutWrapper)
                {
                    for (int j = 0; j < child.childCount; j++)
                        n.Children.Add(Snap(child.hierarchy[j]));
                    continue;
                }
                n.Children.Add(Snap(child));
            }
            return n;
        }

        static IEnumerable<string> FilterClasses(IEnumerable<string> all)
        {
            foreach (var c in all)
            {
                if (string.IsNullOrEmpty(c)) continue;
                if (c.StartsWith("unity-")) continue;
                if (c.StartsWith("h2u-") && !c.StartsWith("h2u-tag-")) continue;
                yield return c;
            }
        }

        static string ColorToStr(Color c)
            => $"rgba({(int)(c.r*255)},{(int)(c.g*255)},{(int)(c.b*255)},{c.a:0.##})";

        // ============================================================
        // shared helpers
        // ============================================================

        static int NextFreePort()
        {
            var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            l.Start();
            int p = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return p;
        }

        static string FindChrome()
        {
            string env = Environment.GetEnvironmentVariable("CHROME_PATH");
            if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
            string[] cs = {
                @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
                @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
                @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
            };
            foreach (var p in cs) if (File.Exists(p)) return p;
            return null;
        }
    }
}
