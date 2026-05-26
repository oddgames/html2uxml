using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.Video;

namespace ODDGames.Html2Uxml
{
    [UxmlElement]
    public partial class Html2UxmlVideo : Html2UxmlPanel
    {
        const int RenderTextureWidth = 1280;
        const int RenderTextureHeight = 720;

        VideoPlayer _player;
        GameObject _host;
        RenderTexture _target;
        VisualElement _surface;
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
        string _poster = string.Empty;
        bool _ready;

        [UxmlAttribute("src")]
        public string Src
        {
            get => _src;
            set { _src = value ?? string.Empty; ApplySource(); }
        }

        [UxmlAttribute("poster")]
        public string Poster
        {
            get => _poster;
            set { _poster = value ?? string.Empty; ApplyPoster(); }
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
            set { _autoplay = value; if (_player != null) _player.playOnAwake = value; }
        }

        [UxmlAttribute("loop")]
        public bool Loop
        {
            get => _loop;
            set { _loop = value; if (_player != null) _player.isLooping = value; }
        }

        [UxmlAttribute("muted")]
        public bool Muted
        {
            get => _muted;
            set { _muted = value; ApplyMute(); }
        }

        [UxmlAttribute("volume")]
        public float Volume
        {
            get => _volume;
            set { _volume = Mathf.Clamp01(value); ApplyMute(); }
        }

        public bool IsPlaying => _player != null && _player.isPlaying;
        public double Duration => _player != null ? _player.length : 0;
        public double CurrentTime
        {
            get => _player != null ? _player.time : 0;
            set { if (_player != null) _player.time = value; }
        }

        public Html2UxmlVideo()
        {
            AddToClassList("html2uxml-video");
            style.flexDirection = FlexDirection.Column;
            style.alignItems = Align.Stretch;
            style.minHeight = 180;
            style.minWidth = 320;

            _surface = new VisualElement { name = "html2uxml-video-surface", pickingMode = PickingMode.Ignore };
            _surface.AddToClassList("html2uxml-video-surface");
            _surface.style.flexGrow = 1;
            _surface.style.backgroundColor = Color.black;
            Add(_surface);

            BuildControlsBar();
            RegisterCallback<AttachToPanelEvent>(_ => EnsureHost());
            RegisterCallback<DetachFromPanelEvent>(_ => Teardown());
            RegisterCallback<ClickEvent>(_ => { if (_showControls) TogglePlay(); });
        }

        void BuildControlsBar()
        {
            _controlsBar = new VisualElement { name = "html2uxml-video-controls" };
            _controlsBar.AddToClassList("html2uxml-video-controls");
            _controlsBar.style.flexDirection = FlexDirection.Row;
            _controlsBar.style.alignItems = Align.Center;
            _controlsBar.style.height = 28;
            _controlsBar.style.backgroundColor = new Color(0, 0, 0, 0.5f);
            _controlsBar.style.paddingLeft = 8;
            _controlsBar.style.paddingRight = 8;
            _controlsBar.style.display = DisplayStyle.None;

            _playPauseButton = new VisualElement { name = "html2uxml-video-play" };
            _playPauseButton.AddToClassList("html2uxml-video-play");
            _playPauseButton.style.width = 18;
            _playPauseButton.style.height = 18;
            _playPauseButton.style.backgroundColor = Color.white;
            _playPauseButton.RegisterCallback<ClickEvent>(evt => { TogglePlay(); evt.StopPropagation(); });
            _controlsBar.Add(_playPauseButton);

            _progress = new VisualElement { name = "html2uxml-video-progress" };
            _progress.AddToClassList("html2uxml-video-progress");
            _progress.style.flexGrow = 1;
            _progress.style.height = 4;
            _progress.style.marginLeft = 8;
            _progress.style.marginRight = 8;
            _progress.style.backgroundColor = new Color(1, 1, 1, 0.3f);
            _progress.RegisterCallback<ClickEvent>(OnProgressClick);

            _progressFill = new VisualElement { name = "html2uxml-video-progress-fill", pickingMode = PickingMode.Ignore };
            _progressFill.AddToClassList("html2uxml-video-progress-fill");
            _progressFill.style.height = 4;
            _progressFill.style.width = Length.Percent(0);
            _progressFill.style.backgroundColor = Color.white;
            _progress.Add(_progressFill);
            _controlsBar.Add(_progress);

            _timeLabel = new Label("0:00 / 0:00") { name = "html2uxml-video-time", pickingMode = PickingMode.Ignore };
            _timeLabel.AddToClassList("html2uxml-video-time");
            _timeLabel.style.color = Color.white;
            _timeLabel.style.unityTextAlign = TextAnchor.MiddleRight;
            _timeLabel.style.minWidth = 80;
            _controlsBar.Add(_timeLabel);

            Add(_controlsBar);
        }

        void OnProgressClick(ClickEvent evt)
        {
            if (_player == null || _player.length <= 0) return;
            float w = _progress.resolvedStyle.width;
            if (w <= 0) return;
            float t = Mathf.Clamp01((evt.localPosition.x - 0) / w);
            _player.time = t * _player.length;
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
            _host = new GameObject("Html2UxmlVideoHost");
            Object.DontDestroyOnLoad(_host);
            _player = _host.AddComponent<VideoPlayer>();
            _player.playOnAwake = _autoplay;
            _player.isLooping = _loop;
            _player.renderMode = VideoRenderMode.RenderTexture;
            _target = new RenderTexture(RenderTextureWidth, RenderTextureHeight, 0);
            _player.targetTexture = _target;
            _surface.style.backgroundImage = Background.FromRenderTexture(_target);
            _player.audioOutputMode = VideoAudioOutputMode.AudioSource;
            var audio = _host.AddComponent<AudioSource>();
            _player.SetTargetAudioSource(0, audio);
            ApplyMute();
            _player.prepareCompleted += _ => { _ready = true; if (_autoplay) _player.Play(); };
            ApplySource();
            schedule.Execute(TickProgress).Every(100);
        }

        void TickProgress()
        {
            if (_player == null || !_showControls) return;
            UpdateProgressVisual();
        }

        void UpdateProgressVisual()
        {
            if (_player == null || _progressFill == null) return;
            double dur = _player.length;
            double cur = _player.time;
            float t = dur > 0 ? Mathf.Clamp01((float)(cur / dur)) : 0;
            _progressFill.style.width = Length.Percent(t * 100f);
            if (_timeLabel != null)
                _timeLabel.text = $"{FormatTime(cur)} / {FormatTime(dur)}";
        }

        static string FormatTime(double seconds)
        {
            if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
            int s = (int)seconds;
            return $"{s / 60}:{s % 60:00}";
        }

        void Teardown()
        {
            if (_player != null) _player.Stop();
            if (_host != null) Object.Destroy(_host);
            if (_target != null) _target.Release();
            _host = null;
            _player = null;
            _target = null;
            _ready = false;
        }

        void ApplySource()
        {
            if (_player == null) return;
            if (string.IsNullOrEmpty(_src))
            {
                _player.Stop();
                return;
            }
            _player.source = VideoSource.Url;
            _player.url = _src;
            _player.Prepare();
        }

        void ApplyPoster()
        {
            // Poster shown until video starts. Use background-image on surface
            // until first frame renders. Resources.Load fails silently for
            // remote URLs; UI Toolkit's background-image accepts asset only.
            if (string.IsNullOrEmpty(_poster) || _surface == null) return;
            var tex = Resources.Load<Texture2D>(_poster);
            if (tex != null)
                _surface.style.backgroundImage = new StyleBackground(tex);
        }

        void ApplyMute()
        {
            if (_host == null) return;
            var audio = _host.GetComponent<AudioSource>();
            if (audio == null) return;
            audio.mute = _muted;
            audio.volume = _volume;
        }

        public void Play() { if (_player != null) _player.Play(); }
        public void Pause() { if (_player != null) _player.Pause(); }
        public void Stop() { if (_player != null) _player.Stop(); }
        public void TogglePlay()
        {
            if (_player == null) return;
            if (_player.isPlaying) _player.Pause();
            else _player.Play();
        }
    }
}
