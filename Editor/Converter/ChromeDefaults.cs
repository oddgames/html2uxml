namespace ODDGames.Html2Uxml.Editor.Converter
{
    // Browser user-agent stylesheet defaults. Mirrors converter._USER_AGENT_CSS
    // exactly so the C# pipeline receives the same default rules Python
    // prepends. Parsed through the standard CssLoader+Resolver+StyleMapper
    // chain so cascade ordering matches.
    //
    // Author CSS (and inline styles) are appended after this so any author
    // declaration wins on equal specificity.
    public static class ChromeDefaults
    {
        public const string UserAgentCss = @"
body { margin: 8px; min-height: 100%; background-color: #ffffff; color: #000000; }
mark { background-color: #ffff00; color: #000000; }
small { font-size: 0.83em; }
a { color: #0000ee; -unity-text-decoration: underline; }
h1 { font-size: 2em; margin-bottom: 0.67em; }
h2 { font-size: 1.5em; margin-bottom: 0.83em; }
h3 { font-size: 1.17em; margin-bottom: 1em; }
h4 { font-size: 1em; margin-bottom: 1em; }
h5 { font-size: 0.83em; margin-bottom: 1em; }
h6 { font-size: 0.67em; margin-bottom: 1em; }
p { margin-bottom: 1em; }
blockquote { margin-bottom: 1em; }
pre { white-space: pre; margin-bottom: 1em; }
pre code { white-space: pre; background-color: transparent; padding: 0; }
ul, ol { margin-bottom: 1em; }
progress { width: 160px; height: 14px; background-color: #ededed; border: 2px solid #cccccc; border-radius: 4px; }
meter { width: 160px; height: 14px; background-color: #ededed; border: 2px solid #cccccc; border-radius: 4px; }
hr { height: 1px; background-color: #cccccc; margin-top: 8px; margin-bottom: 8px; }
";
    }
}
