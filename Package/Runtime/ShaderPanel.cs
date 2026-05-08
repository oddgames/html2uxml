using UnityEngine;
using UnityEngine.UIElements;

namespace ODDGames.Html2Uxml
{
    // Draws a material/textured quad inside UI Toolkit. Use this when CSS-like
    // gradients need shader logic instead of Html2UxmlPanel's stripe approximation.
    [UxmlElement]
    public partial class ShaderPanel : VisualElement
    {
        Material _material;
        Material _instancedMaterial;
        Texture _texture;
        Color _tint = Color.white;
        string _materialResource;

        [UxmlAttribute]
        public Material Material
        {
            get => _material;
            set
            {
                if (_material == value && _instancedMaterial == null)
                    return;
                DisposeInstancedMaterial();
                _material = value;
                style.unityMaterial = value;
                MarkDirtyRepaint();
            }
        }

        [UxmlAttribute]
        public string MaterialResource
        {
            get => _materialResource;
            set
            {
                if (_materialResource == value)
                    return;
                _materialResource = value;
                LoadMaterialResource(value);
            }
        }

        [UxmlAttribute]
        public Texture Texture
        {
            get => _texture;
            set
            {
                if (_texture == value)
                    return;
                _texture = value;
                MarkDirtyRepaint();
            }
        }

        [UxmlAttribute]
        public Color Tint
        {
            get => _tint;
            set
            {
                if (_tint == value)
                    return;
                _tint = value;
                MarkDirtyRepaint();
            }
        }

        public ShaderPanel()
        {
            pickingMode = PickingMode.Ignore;
            generateVisualContent += OnGenerateVisualContent;
            RegisterCallback<CustomStyleResolvedEvent>(_ => MarkDirtyRepaint());
            RegisterCallback<DetachFromPanelEvent>(_ => DisposeInstancedMaterial());
        }

        protected virtual void BeforeGenerateVisualContent()
        {
        }

        void OnGenerateVisualContent(MeshGenerationContext ctx)
        {
            BeforeGenerateVisualContent();
            var rect = contentRect;
            if (rect.width <= 0f || rect.height <= 0f)
                return;

            var data = ctx.Allocate(4, 6, _texture);
            var tint = (Color32)_tint;
            data.SetAllVertices(new[]
            {
                MakeVertex(rect.xMin, rect.yMin, 0f, 0f, tint),
                MakeVertex(rect.xMax, rect.yMin, 1f, 0f, tint),
                MakeVertex(rect.xMax, rect.yMax, 1f, 1f, tint),
                MakeVertex(rect.xMin, rect.yMax, 0f, 1f, tint),
            });
            data.SetAllIndices(new ushort[] { 0, 1, 2, 2, 3, 0 });
        }

        void LoadMaterialResource(string resourcePath)
        {
            DisposeInstancedMaterial();
            _material = null;
            if (string.IsNullOrWhiteSpace(resourcePath))
            {
                style.unityMaterial = null;
                MarkDirtyRepaint();
                return;
            }

            var normalized = NormalizeResourcePath(resourcePath);
            var template = Resources.Load<Material>(normalized);
            if (template == null)
            {
                Debug.LogWarning($"[html2uxml] Missing material resource {normalized}.");
                style.unityMaterial = null;
                MarkDirtyRepaint();
                return;
            }

            _instancedMaterial = UnityEngine.Object.Instantiate(template);
            _instancedMaterial.hideFlags = HideFlags.HideAndDontSave;
            _material = _instancedMaterial;
            style.unityMaterial = _material;
            MarkDirtyRepaint();
        }

        void DisposeInstancedMaterial()
        {
            if (_instancedMaterial == null)
                return;
            if (_material == _instancedMaterial)
                _material = null;
            style.unityMaterial = null;
            if (Application.isPlaying)
                UnityEngine.Object.Destroy(_instancedMaterial);
            else
                UnityEngine.Object.DestroyImmediate(_instancedMaterial);
            _instancedMaterial = null;
        }

        static string NormalizeResourcePath(string path)
        {
            path = path.Replace('\\', '/').Trim();
            const string prefix = "Resources/";
            int idx = path.IndexOf(prefix, System.StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
                path = path.Substring(idx + prefix.Length);
            if (path.EndsWith(".mat", System.StringComparison.OrdinalIgnoreCase))
                path = path.Substring(0, path.Length - 4);
            return path.Trim('/');
        }

        static Vertex MakeVertex(float x, float y, float u, float v, Color32 tint)
        {
            return new Vertex
            {
                position = new Vector3(x, y, Vertex.nearZ),
                uv = new Vector2(u, v),
                tint = tint,
            };
        }
    }
}
