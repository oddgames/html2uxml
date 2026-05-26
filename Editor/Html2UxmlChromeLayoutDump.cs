using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ODDGames.Html2Uxml.Editor
{
    // Spawns headless Chrome at a fixed viewport, navigates to the local
    // HTML file, then talks the Chrome DevTools Protocol (CDP) over a
    // WebSocket to read `getBoundingClientRect()` for every element. Uses
    // System.Net.WebSockets so there's no Playwright / Node / Python
    // dependency — pure .NET that ships with Unity's editor runtime.
    //
    // The result is the same shape as Html2UxmlCssInliner.DumpLayoutByClass
    // so Html2UxmlLayoutCompare can swap one for the other.
    public static class Html2UxmlChromeLayoutDump
    {
        public sealed class ClassLayout
        {
            public int Count;
            public double W, H, X, Y;
        }

        public static Dictionary<string, ClassLayout> Dump(
            string htmlAbsPath, int viewportWidth = 1438, int viewportHeight = 788,
            int timeoutMs = 30000)
        {
            if (!File.Exists(htmlAbsPath))
                throw new FileNotFoundException("HTML not found", htmlAbsPath);

            string chromeExe = FindChrome();
            if (chromeExe == null)
                throw new InvalidOperationException("Chrome/Edge not found. Set CHROME_PATH env var.");

            int port = NextFreePort();
            string profileDir = Path.Combine(Path.GetTempPath(), "h2u_chrome_layout_" + port);
            try { Directory.CreateDirectory(profileDir); } catch { }

            string args =
                // Use the classic --headless flag rather than --headless=new
                // — the new headless implementation refuses file:// schemes
                // with ERR_ABORTED on Page.navigate (different security
                // policy), and we always want local-file loads here.
                "--headless --disable-gpu --no-sandbox --no-first-run " +
                "--no-default-browser-check --hide-scrollbars " +
                "--disable-features=Translate " +
                // Allow file:// access — headless=new defaults to blocking
                // local-file navigation, which surfaces as ERR_ABORTED on
                // Page.navigate and leaves us with an empty document.
                "--allow-file-access-from-files " +
                // Required since Chrome 111 — without this Chrome rejects
                // the WebSocket handshake from a non-browser client and
                // we hang forever waiting on a connection that never
                // upgrades. The lock symptom we saw in CODE_EXEC was
                // this, not a slow page load.
                "--remote-allow-origins=* " +
                $"--remote-debugging-port={port} " +
                $"--window-size={viewportWidth},{viewportHeight} " +
                $"--user-data-dir=\"{profileDir}\" " +
                // Launch directly at the target URL. Page.navigate over CDP
                // gets ERR_ABORTED on file:// in headless mode, but Chrome
                // happily opens a file:// URL passed on the command line.
                $"\"{new Uri(htmlAbsPath).AbsoluteUri}\"";

            Process proc = null;
            try
            {
                proc = Process.Start(new ProcessStartInfo
                {
                    FileName = chromeExe,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                });

                string fileUri = new Uri(htmlAbsPath).AbsoluteUri;
                // Drive the async pipeline on the threadpool with a hard
                // deadline. .GetAwaiter().GetResult() blocks the caller's
                // thread for the full duration of the awaits — Unity's
                // main thread freezes for the entire WebSocket handshake +
                // page-load round-trip. Wait() with a timeout lets us
                // bail cleanly instead of locking the editor.
                var task = Task.Run(() => DumpAsync(port, fileUri, viewportWidth, viewportHeight, timeoutMs));
                if (!task.Wait(timeoutMs))
                    throw new TimeoutException($"Chrome layout dump exceeded {timeoutMs}ms");
                return task.Result;
            }
            finally
            {
                try
                {
                    if (proc != null && !proc.HasExited) proc.Kill();
                }
                catch { }
                try { if (Directory.Exists(profileDir)) Directory.Delete(profileDir, true); } catch { }
            }
        }

        // ---------- CDP plumbing ------------------------------------------

        // Pick a free TCP port by binding to 0 and reading what the OS gave
        // us. Avoids hard-coding 9222 and clashing with other Chrome runs.
        static int NextFreePort()
        {
            var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            l.Start();
            int p = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return p;
        }

        static async Task<Dictionary<string, ClassLayout>> DumpAsync(
            int port, string fileUri, int vw, int vh, int timeoutMs)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

            // Wait for Chrome's /json endpoint to come up. Headless takes
            // ~500ms on a warm cache, longer cold. /json returns an array
            // of all targets — extensions, service workers, the page —
            // and we need the one whose type is "page", otherwise we'd
            // attach to an extension background page and see no DOM.
            string targetWs = null;
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline && targetWs == null)
            {
                try
                {
                    string body = await http.GetStringAsync($"http://127.0.0.1:{port}/json");
                    Trace("/json body (len " + body.Length + "): " + (body.Length > 1500 ? body.Substring(0, 1500) : body));
                    // Each target is one JSON object — find the first
                    // {...} block whose "type" is "page" and grab its WS.
                    foreach (Match m in Regex.Matches(body, "\\{[^{}]*?\\}", RegexOptions.Singleline))
                    {
                        if (!m.Value.Contains("\"type\": \"page\"")
                            && !m.Value.Contains("\"type\":\"page\"")) continue;
                        var w = Regex.Match(m.Value, "\"webSocketDebuggerUrl\"\\s*:\\s*\"(?<u>ws://[^\"]+)\"");
                        if (w.Success) { targetWs = w.Groups["u"].Value; break; }
                    }
                }
                catch (Exception ex) { Trace("/json err: " + ex.Message); }
                if (targetWs == null) await Task.Delay(200);
            }
            if (targetWs == null)
                throw new TimeoutException("Chrome remote-debug port did not expose a 'page' target");

            using var ws = new ClientWebSocket();
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
            using (var ctsConn = new CancellationTokenSource(5000))
                await ws.ConnectAsync(new Uri(targetWs), ctsConn.Token);
            Trace("connected to " + targetWs);

            int nextId = 1;
            async Task<string> Call(string method, string paramsJson = null)
            {
                int id = nextId++;
                string p = paramsJson == null ? "" : ",\"params\":" + paramsJson;
                string msg = "{\"id\":" + id + ",\"method\":\"" + method + "\"" + p + "}";
                await ws.SendAsync(Encoding.UTF8.GetBytes(msg), WebSocketMessageType.Text, true, CancellationToken.None);
                Trace("-> " + method + " id=" + id);
                while (true)
                {
                    string raw = await Recv(ws);
                    Trace("<- " + (raw.Length > 200 ? raw.Substring(0, 200) : raw));
                    if (raw.Contains("\"id\":" + id + ","))
                        return raw;
                    if (raw.Contains("\"id\":" + id + "}"))
                        return raw;
                    // Otherwise it's an event we don't care about — drop it.
                }
            }

            async Task WaitForEvent(string method, int waitMs)
            {
                var t = DateTime.UtcNow.AddMilliseconds(waitMs);
                while (DateTime.UtcNow < t)
                {
                    string raw = await Recv(ws);
                    if (raw.Contains("\"method\":\"" + method + "\"")) return;
                }
                throw new TimeoutException("Timed out waiting for " + method);
            }

            await Call("Emulation.setDeviceMetricsOverride",
                $"{{\"width\":{vw},\"height\":{vh},\"deviceScaleFactor\":1,\"mobile\":false}}");
            // Chrome opened directly at the target URL, so it's already
            // loading. Poll document.readyState until 'complete' instead
            // of waiting on Page.loadEventFired (which may have fired
            // before we attached).
            for (int i = 0; i < 50; i++)
            {
                string resp0 = await Call("Runtime.evaluate",
                    "{\"expression\":\"document.readyState\",\"returnByValue\":true}");
                if (resp0.Contains("\"value\":\"complete\"")) break;
                await Task.Delay(150);
            }
            // One more frame for fonts/images.
            await Task.Delay(300);

            string urlResp = await Call("Runtime.evaluate",
                "{\"expression\":\"document.URL\",\"returnByValue\":true}");
            Trace("docURL: " + urlResp);
            string countResp = await Call("Runtime.evaluate",
                "{\"expression\":\"document.querySelectorAll('*').length\",\"returnByValue\":true}");
            Trace("nodeCount: " + countResp);

            // Walk the DOM in-page and return per-class accumulated rects.
            // Use element.classList rather than .className — SVG elements
            // expose an SVGAnimatedString for .className that stringifies
            // to "[object SVGAnimatedString]", leaking those literals as
            // fake class buckets in the output.
            const string js =
                "JSON.stringify((()=>{const b={};for(const e of document.querySelectorAll('*')){" +
                "if(!e.classList||e.classList.length===0)continue;" +
                "const r=e.getBoundingClientRect();" +
                "for(const c of e.classList){if(!c||c.startsWith('h2u-')||c.startsWith('unity-'))continue;" +
                "let t=b[c]||(b[c]={n:0,w:0,h:0,x:0,y:0});" +
                "t.n++;t.w+=r.width;t.h+=r.height;t.x+=r.left;t.y+=r.top;}}return b;})())";

            string resp = await Call("Runtime.evaluate",
                "{\"expression\":" + JsonString(js) + ",\"returnByValue\":true}");

            return ParseResult(resp);
        }

        static async Task<string> Recv(ClientWebSocket ws, int timeoutMs = 10000)
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            var buf = new byte[8192];
            var sb = new StringBuilder();
            while (true)
            {
                var seg = new ArraySegment<byte>(buf);
                var r = await ws.ReceiveAsync(seg, cts.Token);
                sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
                if (r.EndOfMessage) return sb.ToString();
            }
        }

        static string JsonString(string raw)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in raw)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"':  sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n");  break;
                    case '\r': sb.Append("\\r");  break;
                    case '\t': sb.Append("\\t");  break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        // Pull the JSON string CDP returned (inside result.value) and parse
        // it into our typed dict. Regex parsing is brittle, but the shape
        // is fully known — keeps the dep footprint at zero.
        static Dictionary<string, ClassLayout> ParseResult(string cdpResp)
        {
            var output = new Dictionary<string, ClassLayout>(StringComparer.Ordinal);

            // CDP returns: {"id":N,"result":{"result":{"type":"string","value":"<json>"}}}
            // Pull the inner value first — it's a JSON-encoded string.
            var valMatch = Regex.Match(cdpResp,
                "\"value\"\\s*:\\s*\"(?<v>(?:\\\\.|[^\"\\\\])*)\"");
            if (!valMatch.Success) return output;
            string inner = Regex.Unescape(valMatch.Groups["v"].Value)
                .Replace("\\\"", "\"").Replace("\\\\", "\\");

            // Then parse the outer object: {"className":{"n":N,"w":W,...}, ...}
            // Brace-balanced cut per entry — class names may contain odd chars.
            int i = 0;
            if (i < inner.Length && inner[i] == '{') i++;
            while (i < inner.Length)
            {
                while (i < inner.Length && (inner[i] == ',' || char.IsWhiteSpace(inner[i]))) i++;
                if (i >= inner.Length || inner[i] == '}') break;
                if (inner[i] != '"') break;
                int keyStart = ++i;
                while (i < inner.Length && inner[i] != '"') i++;
                string key = inner.Substring(keyStart, i - keyStart);
                i++; // closing quote
                while (i < inner.Length && inner[i] != '{') i++;
                int depth = 0;
                int objStart = i;
                while (i < inner.Length)
                {
                    if (inner[i] == '{') depth++;
                    else if (inner[i] == '}') { depth--; if (depth == 0) { i++; break; } }
                    i++;
                }
                string obj = inner.Substring(objStart, i - objStart);
                output[key] = ParseRect(obj);
            }
            return output;
        }

        static ClassLayout ParseRect(string obj)
        {
            int N(string k)
            {
                var m = Regex.Match(obj, "\"" + k + "\"\\s*:\\s*(-?[0-9.]+)");
                return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : 0;
            }
            double D(string k)
            {
                var m = Regex.Match(obj, "\"" + k + "\"\\s*:\\s*(-?[0-9.]+)");
                return m.Success && double.TryParse(m.Groups[1].Value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;
            }
            int n = N("n");
            double w = D("w"), h = D("h"), x = D("x"), y = D("y");
            return new ClassLayout
            {
                Count = n,
                W = n > 0 ? w / n : 0,
                H = n > 0 ? h / n : 0,
                X = n > 0 ? x / n : 0,
                Y = n > 0 ? y / n : 0,
            };
        }

        // ---------- chrome locator ----------------------------------------

        static void Trace(string msg)
        {
            try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "h2u_cdp.log"),
                DateTime.Now.ToString("HH:mm:ss.fff ") + msg + Environment.NewLine); }
            catch { }
        }

        static string FindChrome()
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
    }
}
