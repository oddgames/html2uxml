using UnityEngine;
using UnityEngine.UIElements;

namespace ODDGames.Html2Uxml
{
    // Displays a downsampled camera/render-texture backdrop behind UI content.
    // This is the runtime path for true frosted-glass effects; ordinary USS
    // filters only process the element subtree, not pixels already behind it.
    [UxmlElement]
    public partial class BackdropBlurPanel : ShaderPanel
    {
        RenderTexture _source;
        RenderTexture _ping;
        RenderTexture _pong;
        Camera _sourceCamera;
        Shader _blurShader;
        Material _blurMaterial;
        IVisualElementScheduledItem _captureSchedule;
        bool _captureEachFrame;
        bool _blurDirty = true;
        string _sourceId;
        bool _useScreenSpaceUv = true;
        int _lastWidth;
        int _lastHeight;
        int _lastSourceWidth;
        int _lastSourceHeight;

        const string ScreenBackdropMaterialResource = "ODDGames/html2uxml/Materials/ODDGamesScreenBackdrop";

        int _downsample = 2;
        int _iterations = 2;
        float _radius = 3f;

        [UxmlAttribute]
        public int Downsample
        {
            get => _downsample;
            set
            {
                value = Mathf.Max(1, value);
                if (_downsample == value) return;
                _downsample = value;
                ReleasePingPong();
                MarkBlurDirty();
            }
        }

        [UxmlAttribute]
        public int Iterations
        {
            get => _iterations;
            set
            {
                value = Mathf.Max(1, value);
                if (_iterations == value) return;
                _iterations = value;
                MarkBlurDirty();
            }
        }

        [UxmlAttribute]
        public float Radius
        {
            get => _radius;
            set
            {
                value = Mathf.Max(0f, value);
                if (Mathf.Approximately(_radius, value)) return;
                _radius = value;
                MarkBlurDirty();
            }
        }

        [UxmlAttribute]
        public string SourceId
        {
            get => _sourceId;
            set
            {
                if (_sourceId == value) return;
                _sourceId = value;
                ConfigureSharedMaterial();
                TryBindSharedTexture();
                MarkDirtyRepaint();
            }
        }

        [UxmlAttribute]
        public bool UseScreenSpaceUv
        {
            get => _useScreenSpaceUv;
            set
            {
                if (_useScreenSpaceUv == value) return;
                _useScreenSpaceUv = value;
                ConfigureSharedMaterial();
                MarkDirtyRepaint();
            }
        }

        [UxmlAttribute]
        public bool CaptureEachFrame
        {
            get => _captureEachFrame;
            set
            {
                _captureEachFrame = value;
                if (_captureSchedule == null) return;
                if (value) _captureSchedule.Resume();
                else _captureSchedule.Pause();
            }
        }

        public Camera SourceCamera
        {
            get => _sourceCamera;
            set
            {
                _sourceCamera = value;
                MarkBlurDirty();
            }
        }

        public Shader BlurShader
        {
            get => _blurShader;
            set
            {
                _blurShader = value;
                ReleaseBlurMaterial();
                MarkBlurDirty();
            }
        }

        public BackdropBlurPanel()
        {
            RegisterCallback<AttachToPanelEvent>(_ =>
            {
                EnsureBlurShader();
                ConfigureSharedMaterial();
                TryBindSharedTexture();
            });
            RegisterCallback<DetachFromPanelEvent>(_ => ReleaseTemporaryTextures());
            _captureSchedule = schedule.Execute(UpdateScheduled).Every(16);
            _captureSchedule.Pause();
        }

        public void SetSource(RenderTexture source)
        {
            _source = source;
            MarkBlurDirty();
        }

        public void Refresh()
        {
            CaptureSourceCamera();
            UpdateBlurredTexture(force: true);
            MarkDirtyRepaint();
        }

        protected override void BeforeGenerateVisualContent()
        {
            if (UsesSharedSource())
            {
                TryBindSharedTexture();
                return;
            }
            UpdateBlurredTexture();
        }

        void UpdateScheduled()
        {
            CaptureSourceCamera();
            MarkBlurDirty();
        }

        void CaptureSourceCamera()
        {
            if (_sourceCamera == null) return;
            EnsureSourceTexture(
                Mathf.CeilToInt(Mathf.Max(1f, contentRect.width)),
                Mathf.CeilToInt(Mathf.Max(1f, contentRect.height)));
            var previous = _sourceCamera.targetTexture;
            _sourceCamera.targetTexture = _source;
            _sourceCamera.Render();
            _sourceCamera.targetTexture = previous;
            _blurDirty = true;
        }

        void UpdateBlurredTexture(bool force = false)
        {
            if (_source == null || _source.width <= 0 || _source.height <= 0)
                return;
            bool sourceSizeChanged = _lastSourceWidth != _source.width || _lastSourceHeight != _source.height;
            if (!force && !_blurDirty && !sourceSizeChanged)
                return;
            _lastSourceWidth = _source.width;
            _lastSourceHeight = _source.height;
            EnsureBlurShader();
            if (_blurShader == null || !_blurShader.isSupported)
            {
                Texture = _source;
                _blurDirty = false;
                return;
            }

            int width = Mathf.Max(1, _source.width / Mathf.Max(1, _downsample));
            int height = Mathf.Max(1, _source.height / Mathf.Max(1, _downsample));
            EnsurePingPong(width, height);
            EnsureBlurMaterial();
            if (_blurMaterial == null)
            {
                Texture = _source;
                _blurDirty = false;
                return;
            }

            Graphics.Blit(_source, _ping);
            int passes = Mathf.Max(1, _iterations);
            for (int i = 0; i < passes; i++)
            {
                float radius = Mathf.Max(0f, _radius) * (i + 1f) / passes;
                _blurMaterial.SetVector("_Direction", new Vector4(radius, 0f, 0f, 0f));
                Graphics.Blit(_ping, _pong, _blurMaterial, 0);
                _blurMaterial.SetVector("_Direction", new Vector4(0f, radius, 0f, 0f));
                Graphics.Blit(_pong, _ping, _blurMaterial, 0);
            }
            Texture = _ping;
            _blurDirty = false;
        }

        bool UsesSharedSource()
        {
            return !string.IsNullOrWhiteSpace(_sourceId);
        }

        void ConfigureSharedMaterial()
        {
            if (UsesSharedSource() && _useScreenSpaceUv && MaterialResource != ScreenBackdropMaterialResource)
                MaterialResource = ScreenBackdropMaterialResource;
        }

        void TryBindSharedTexture()
        {
            if (!UsesSharedSource())
                return;
            if (BackdropCaptureSource.TryGet(_sourceId, out var source) && source.BlurredTexture != null)
                Texture = source.BlurredTexture;
        }

        void MarkBlurDirty()
        {
            _blurDirty = true;
            MarkDirtyRepaint();
        }

        void EnsureSourceTexture(int width, int height)
        {
            width = Mathf.Max(1, width);
            height = Mathf.Max(1, height);
            if (_source != null && _source.width == width && _source.height == height)
                return;
            if (_source != null)
                _source.Release();
            _source = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32)
            {
                name = "ODDGames html2uxml Backdrop Source",
                useMipMap = false,
                autoGenerateMips = false,
            };
            _source.Create();
        }

        void EnsurePingPong(int width, int height)
        {
            if (_ping != null && _pong != null && _lastWidth == width && _lastHeight == height)
                return;
            ReleasePingPong();
            _lastWidth = width;
            _lastHeight = height;
            _ping = NewTemp(width, height, "ODDGames html2uxml Backdrop Blur A");
            _pong = NewTemp(width, height, "ODDGames html2uxml Backdrop Blur B");
        }

        static RenderTexture NewTemp(int width, int height, string name)
        {
            var rt = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32)
            {
                name = name,
                useMipMap = false,
                autoGenerateMips = false,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            rt.Create();
            return rt;
        }

        void EnsureBlurShader()
        {
            if (_blurShader == null)
                _blurShader = Shader.Find("Hidden/ODDGames/html2uxml/GaussianBlur");
        }

        void EnsureBlurMaterial()
        {
            if (_blurMaterial == null && _blurShader != null)
                _blurMaterial = new Material(_blurShader) { hideFlags = HideFlags.HideAndDontSave };
        }

        void ReleaseTemporaryTextures()
        {
            ReleasePingPong();
            if (_source != null && _sourceCamera != null && _sourceCamera.targetTexture == _source)
                _sourceCamera.targetTexture = null;
            if (_source != null)
            {
                _source.Release();
                _source = null;
            }
            ReleaseBlurMaterial();
        }

        void ReleasePingPong()
        {
            if (_ping != null)
            {
                _ping.Release();
                _ping = null;
            }
            if (_pong != null)
            {
                _pong.Release();
                _pong = null;
            }
        }

        void ReleaseBlurMaterial()
        {
            if (_blurMaterial == null) return;
            if (Application.isPlaying)
                Object.Destroy(_blurMaterial);
            else
                Object.DestroyImmediate(_blurMaterial);
            _blurMaterial = null;
        }
    }
}
