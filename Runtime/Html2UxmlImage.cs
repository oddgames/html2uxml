using System.Collections;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UIElements;

namespace ODDGames.Html2Uxml
{
    [UxmlElement]
    public partial class Html2UxmlImage : Html2UxmlPanel
    {
        string _src = string.Empty;
        string _alt = string.Empty;
        string _loading = "auto";
        Texture2D _texture;
        Coroutine _loader;
        GameObject _runner;

        [UxmlAttribute("src")]
        public string Src
        {
            get => _src;
            set { _src = value ?? string.Empty; ApplySource(); }
        }

        [UxmlAttribute("alt")]
        public string Alt
        {
            get => _alt;
            set { _alt = value ?? string.Empty; tooltip = _alt; }
        }

        [UxmlAttribute("loading")]
        public string Loading
        {
            get => _loading;
            set => _loading = string.IsNullOrEmpty(value) ? "auto" : value.ToLowerInvariant();
        }

        public Texture2D Texture => _texture;

        public Html2UxmlImage()
        {
            AddToClassList("html2uxml-image");
            style.flexShrink = 0;
            RegisterCallback<AttachToPanelEvent>(_ => MaybeLoad());
            RegisterCallback<DetachFromPanelEvent>(_ => CancelLoader());
        }

        void MaybeLoad()
        {
            if (_loading == "lazy")
            {
                schedule.Execute(() =>
                {
                    if (worldBound.height > 0)
                        ApplySource();
                }).Every(200);
                return;
            }
            ApplySource();
        }

        void ApplySource()
        {
            CancelLoader();
            if (string.IsNullOrEmpty(_src)) return;
            if (_src.StartsWith("data:") || _src.Contains("://"))
            {
                if (!Application.isPlaying)
                    return;
                _runner = new GameObject("Html2UxmlImageLoader");
                Object.DontDestroyOnLoad(_runner);
                var rb = _runner.AddComponent<RunnerBehaviour>();
                _loader = rb.StartCoroutine(LoadRemote(_src));
                return;
            }
            // Resources fallback (drop the asset under Resources/)
            var tex = Resources.Load<Texture2D>(StripExtension(_src));
            if (tex != null) ApplyTexture(tex);
        }

        IEnumerator LoadRemote(string url)
        {
            using var req = UnityWebRequestTexture.GetTexture(url);
            yield return req.SendWebRequest();
            if (req.result == UnityWebRequest.Result.Success)
                ApplyTexture(DownloadHandlerTexture.GetContent(req));
            else
                Debug.LogWarning($"[Html2UxmlImage] failed to load {url}: {req.error}");
            CancelLoader();
        }

        void ApplyTexture(Texture2D tex)
        {
            _texture = tex;
            style.backgroundImage = new StyleBackground(tex);
        }

        void CancelLoader()
        {
            if (_runner != null)
            {
                Object.Destroy(_runner);
                _runner = null;
            }
            _loader = null;
        }

        static string StripExtension(string path)
        {
            int dot = path.LastIndexOf('.');
            return dot > 0 ? path.Substring(0, dot) : path;
        }

        class RunnerBehaviour : MonoBehaviour { }
    }
}
