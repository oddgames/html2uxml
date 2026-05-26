using System.Collections;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UIElements;

namespace ODDGames.Html2Uxml
{
    [UxmlElement]
    public partial class Html2UxmlAudio : Html2UxmlPanel
    {
        AudioSource _audio;
        GameObject _host;
        VisualElement _controlsBar;
        VisualElement _playPauseButton;
        VisualElement _progress;
        VisualElement _progressFill;
        Label _timeLabel;
        bool _showControls;
        bool _autoplay;
        bool _loop;
        bool _muted;
        float _volume = 1f;
        string _src = string.Empty;
        AudioClip _clip;
        Coroutine _loader;

        [UxmlAttribute("src")]
        public string Src
        {
            get => _src;
            set { _src = value ?? string.Empty; ApplySource(); }
        }

        [UxmlAttribute("controls")]
        public bool Controls
        {
            get => _showControls;
            set { _showControls = value; UpdateControlsVisibility(); }
        }

        [UxmlAttribute("autoplay")]
        public bool Autoplay
        {
            get => _autoplay;
            set => _autoplay = value;
        }

        [UxmlAttribute("loop")]
        public bool Loop
        {
            get => _loop;
            set { _loop = value; if (_audio != null) _audio.loop = value; }
        }

        [UxmlAttribute("muted")]
        public bool Muted
        {
            get => _muted;
            set { _muted = value; if (_audio != null) _audio.mute = value; }
        }

        [UxmlAttribute("volume")]
        public float Volume
        {
            get => _volume;
            set { _volume = Mathf.Clamp01(value); if (_audio != null) _audio.volume = _volume; }
        }

        public bool IsPlaying => _audio != null && _audio.isPlaying;
        public float Duration => _clip != null ? _clip.length : 0f;
        public float CurrentTime
        {
            get => _audio != null ? _audio.time : 0f;
            set { if (_audio != null) _audio.time = Mathf.Clamp(value, 0, Duration); }
        }

        public Html2UxmlAudio()
        {
            AddToClassList("html2uxml-audio");
            style.flexDirection = FlexDirection.Row;
            style.alignItems = Align.Center;
            style.minHeight = 32;
            style.minWidth = 240;
            BuildControlsBar();
            UpdateControlsVisibility();
            RegisterCallback<AttachToPanelEvent>(_ => EnsureHost());
            RegisterCallback<DetachFromPanelEvent>(_ => Teardown());
        }

        void BuildControlsBar()
        {
            _controlsBar = new VisualElement { name = "html2uxml-audio-controls" };
            _controlsBar.AddToClassList("html2uxml-audio-controls");
            _controlsBar.style.flexDirection = FlexDirection.Row;
            _controlsBar.style.alignItems = Align.Center;
            _controlsBar.style.flexGrow = 1;
            _controlsBar.style.paddingLeft = 8;
            _controlsBar.style.paddingRight = 8;
            _controlsBar.style.backgroundColor = new Color(0.93f, 0.93f, 0.93f, 1f);
            _controlsBar.style.borderTopLeftRadius = 16;
            _controlsBar.style.borderTopRightRadius = 16;
            _controlsBar.style.borderBottomRightRadius = 16;
            _controlsBar.style.borderBottomLeftRadius = 16;

            _playPauseButton = new VisualElement { name = "html2uxml-audio-play" };
            _playPauseButton.AddToClassList("html2uxml-audio-play");
            _playPauseButton.style.width = 18;
            _playPauseButton.style.height = 18;
            _playPauseButton.style.backgroundColor = new Color(0.05f, 0.48f, 0.86f, 1f);
            _playPauseButton.RegisterCallback<ClickEvent>(evt => { TogglePlay(); evt.StopPropagation(); });
            _controlsBar.Add(_playPauseButton);

            _progress = new VisualElement { name = "html2uxml-audio-progress" };
            _progress.AddToClassList("html2uxml-audio-progress");
            _progress.style.flexGrow = 1;
            _progress.style.height = 4;
            _progress.style.marginLeft = 8;
            _progress.style.marginRight = 8;
            _progress.style.backgroundColor = new Color(0.7f, 0.7f, 0.7f, 1f);
            _progress.RegisterCallback<ClickEvent>(OnProgressClick);

            _progressFill = new VisualElement { name = "html2uxml-audio-progress-fill", pickingMode = PickingMode.Ignore };
            _progressFill.AddToClassList("html2uxml-audio-progress-fill");
            _progressFill.style.height = 4;
            _progressFill.style.width = Length.Percent(0);
            _progressFill.style.backgroundColor = new Color(0.05f, 0.48f, 0.86f, 1f);
            _progress.Add(_progressFill);
            _controlsBar.Add(_progress);

            _timeLabel = new Label("0:00 / 0:00") { name = "html2uxml-audio-time", pickingMode = PickingMode.Ignore };
            _timeLabel.AddToClassList("html2uxml-audio-time");
            _timeLabel.style.unityTextAlign = TextAnchor.MiddleRight;
            _timeLabel.style.minWidth = 80;
            _controlsBar.Add(_timeLabel);

            Add(_controlsBar);
        }

        void OnProgressClick(ClickEvent evt)
        {
            if (_audio == null || _clip == null) return;
            float w = _progress.resolvedStyle.width;
            if (w <= 0) return;
            float t = Mathf.Clamp01((evt.localPosition.x - 0) / w);
            _audio.time = t * _clip.length;
            UpdateProgressVisual();
            evt.StopPropagation();
        }

        void UpdateControlsVisibility()
        {
            if (_controlsBar == null) return;
            _controlsBar.style.display = _showControls ? DisplayStyle.Flex : DisplayStyle.None;
        }

        void EnsureHost()
        {
            if (_host != null) return;
            if (!Application.isPlaying)
                return;
            _host = new GameObject("Html2UxmlAudioHost");
            Object.DontDestroyOnLoad(_host);
            _audio = _host.AddComponent<AudioSource>();
            _audio.playOnAwake = false;
            _audio.loop = _loop;
            _audio.mute = _muted;
            _audio.volume = _volume;
            ApplySource();
            schedule.Execute(TickProgress).Every(100);
        }

        void TickProgress()
        {
            if (_audio == null || !_showControls) return;
            UpdateProgressVisual();
        }

        void UpdateProgressVisual()
        {
            if (_audio == null || _progressFill == null) return;
            float dur = Duration;
            float cur = _audio.time;
            float t = dur > 0 ? Mathf.Clamp01(cur / dur) : 0;
            _progressFill.style.width = Length.Percent(t * 100f);
            if (_timeLabel != null)
                _timeLabel.text = $"{FormatTime(cur)} / {FormatTime(dur)}";
        }

        static string FormatTime(float seconds)
        {
            if (float.IsNaN(seconds) || seconds < 0) seconds = 0;
            int s = (int)seconds;
            return $"{s / 60}:{s % 60:00}";
        }

        void Teardown()
        {
            if (_audio != null) _audio.Stop();
            if (_host != null) Object.Destroy(_host);
            _host = null;
            _audio = null;
            _clip = null;
        }

        void ApplySource()
        {
            if (_host == null || _audio == null) return;
            if (_loader != null)
            {
                var runner = _host.GetComponent<CoroutineRunner>();
                if (runner != null) runner.StopCoroutine(_loader);
                _loader = null;
            }
            if (string.IsNullOrEmpty(_src))
            {
                _audio.Stop();
                _clip = null;
                return;
            }
            var r = _host.GetComponent<CoroutineRunner>() ?? _host.AddComponent<CoroutineRunner>();
            _loader = r.StartCoroutine(LoadClip(_src));
        }

        IEnumerator LoadClip(string url)
        {
            AudioType type = url.EndsWith(".wav") ? AudioType.WAV
                : url.EndsWith(".ogg") ? AudioType.OGGVORBIS
                : AudioType.MPEG;
            using var req = UnityWebRequestMultimedia.GetAudioClip(url, type);
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"[Html2UxmlAudio] failed to load {url}: {req.error}");
                yield break;
            }
            _clip = DownloadHandlerAudioClip.GetContent(req);
            _audio.clip = _clip;
            if (_autoplay) _audio.Play();
        }

        public void Play() { if (_audio != null && _clip != null) _audio.Play(); }
        public void Pause() { if (_audio != null) _audio.Pause(); }
        public void Stop() { if (_audio != null) _audio.Stop(); }
        public void TogglePlay()
        {
            if (_audio == null) return;
            if (_audio.isPlaying) _audio.Pause();
            else if (_clip != null) _audio.Play();
        }

        class CoroutineRunner : MonoBehaviour { }
    }
}
