using System.Collections.Generic;
using UnityEngine;

namespace ODDGames.Html2Uxml
{
    // Captures and blurs a shared backdrop once, so multiple UI panels can
    // sample the same texture instead of each doing its own camera/blit work.
    [DefaultExecutionOrder(-100)]
    public sealed class BackdropCaptureSource : MonoBehaviour
    {
        public const string DefaultSourceId = "default";

        static readonly Dictionary<string, BackdropCaptureSource> Sources =
            new Dictionary<string, BackdropCaptureSource>();

        [SerializeField] string sourceId = DefaultSourceId;
        [SerializeField] Camera sourceCamera;
        [SerializeField] RenderTexture sourceTexture;
        [SerializeField] Shader blurShader;
        [SerializeField] bool captureEachFrame;
        [SerializeField] int downsample = 2;
        [SerializeField] int iterations = 2;
        [SerializeField] float radius = 3f;

        RenderTexture _ownedSource;
        RenderTexture _ping;
        RenderTexture _pong;
        Material _blurMaterial;
        int _lastWidth;
        int _lastHeight;

        public string SourceId
        {
            get => sourceId;
            set
            {
                if (sourceId == value) return;
                Unregister();
                sourceId = string.IsNullOrWhiteSpace(value) ? DefaultSourceId : value;
                if (isActiveAndEnabled) Register();
            }
        }

        public Camera SourceCamera
        {
            get => sourceCamera;
            set => sourceCamera = value;
        }

        public RenderTexture SourceTexture
        {
            get => sourceTexture;
            set => sourceTexture = value;
        }

        public bool CaptureEachFrame
        {
            get => captureEachFrame;
            set => captureEachFrame = value;
        }

        public int Downsample
        {
            get => downsample;
            set
            {
                value = Mathf.Max(1, value);
                if (downsample == value) return;
                downsample = value;
                ReleasePingPong();
            }
        }

        public int Iterations
        {
            get => iterations;
            set => iterations = Mathf.Max(1, value);
        }

        public float Radius
        {
            get => radius;
            set => radius = Mathf.Max(0f, value);
        }

        public Texture BlurredTexture => _ping != null ? _ping : CurrentSourceTexture();

        public static bool TryGet(string id, out BackdropCaptureSource source)
        {
            return Sources.TryGetValue(NormalizeId(id), out source) && source != null && source.isActiveAndEnabled;
        }

        void OnEnable()
        {
            Register();
            EnsureBlurShader();
            Refresh();
        }

        void OnDisable()
        {
            Unregister();
            ReleaseTemporaryTextures();
            ReleaseBlurMaterial();
        }

        void LateUpdate()
        {
            if (captureEachFrame)
                Refresh();
        }

        public void Refresh()
        {
            CaptureCameraIfNeeded();
            UpdateBlurredTexture();
        }

        void Register()
        {
            Sources[NormalizeId(sourceId)] = this;
        }

        void Unregister()
        {
            string key = NormalizeId(sourceId);
            if (Sources.TryGetValue(key, out var current) && current == this)
                Sources.Remove(key);
        }

        void CaptureCameraIfNeeded()
        {
            if (sourceCamera == null || sourceTexture != null)
                return;
            int width = Mathf.Max(1, sourceCamera.pixelWidth);
            int height = Mathf.Max(1, sourceCamera.pixelHeight);
            EnsureOwnedSource(width, height);
            var previous = sourceCamera.targetTexture;
            sourceCamera.targetTexture = _ownedSource;
            sourceCamera.Render();
            sourceCamera.targetTexture = previous;
        }

        RenderTexture CurrentSourceTexture()
        {
            if (sourceTexture != null)
                return sourceTexture;
            return _ownedSource;
        }

        void UpdateBlurredTexture()
        {
            var source = CurrentSourceTexture();
            if (source == null || source.width <= 0 || source.height <= 0)
                return;
            EnsureBlurShader();
            if (blurShader == null || !blurShader.isSupported)
                return;

            int width = Mathf.Max(1, source.width / Mathf.Max(1, downsample));
            int height = Mathf.Max(1, source.height / Mathf.Max(1, downsample));
            EnsurePingPong(width, height);
            EnsureBlurMaterial();
            if (_blurMaterial == null)
                return;

            Graphics.Blit(source, _ping);
            int passes = Mathf.Max(1, iterations);
            for (int i = 0; i < passes; i++)
            {
                float r = Mathf.Max(0f, radius) * (i + 1f) / passes;
                _blurMaterial.SetVector("_Direction", new Vector4(r, 0f, 0f, 0f));
                Graphics.Blit(_ping, _pong, _blurMaterial, 0);
                _blurMaterial.SetVector("_Direction", new Vector4(0f, r, 0f, 0f));
                Graphics.Blit(_pong, _ping, _blurMaterial, 0);
            }
        }

        void EnsureOwnedSource(int width, int height)
        {
            if (_ownedSource != null && _ownedSource.width == width && _ownedSource.height == height)
                return;
            if (_ownedSource != null)
                _ownedSource.Release();
            _ownedSource = NewRenderTexture(width, height, "ODDGames html2uxml Shared Backdrop Source");
        }

        void EnsurePingPong(int width, int height)
        {
            if (_ping != null && _pong != null && _lastWidth == width && _lastHeight == height)
                return;
            ReleasePingPong();
            _lastWidth = width;
            _lastHeight = height;
            _ping = NewRenderTexture(width, height, "ODDGames html2uxml Shared Backdrop Blur A");
            _pong = NewRenderTexture(width, height, "ODDGames html2uxml Shared Backdrop Blur B");
        }

        static RenderTexture NewRenderTexture(int width, int height, string name)
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
            if (blurShader == null)
                blurShader = Shader.Find("Hidden/ODDGames/html2uxml/GaussianBlur");
        }

        void EnsureBlurMaterial()
        {
            if (_blurMaterial == null && blurShader != null)
                _blurMaterial = new Material(blurShader) { hideFlags = HideFlags.HideAndDontSave };
        }

        void ReleaseTemporaryTextures()
        {
            ReleasePingPong();
            if (_ownedSource != null)
            {
                _ownedSource.Release();
                _ownedSource = null;
            }
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
                Destroy(_blurMaterial);
            else
                DestroyImmediate(_blurMaterial);
            _blurMaterial = null;
        }

        static string NormalizeId(string id)
        {
            return string.IsNullOrWhiteSpace(id) ? DefaultSourceId : id.Trim();
        }
    }
}
