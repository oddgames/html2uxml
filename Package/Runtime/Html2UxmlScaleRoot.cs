using UnityEngine;
using UnityEngine.UIElements;

namespace ODDGames.Html2Uxml
{
    // Outer wrapper for fixed-design imports (Figma exports, Medium-style
    // bracket pages, etc.). The wrapper flex-fills the panel; its single
    // child carries the design width/height. The wrapper picks a scale and
    // applies it via `style.scale` so the whole UI tree (back panel,
    // gradients, bracket, labels) scales together — no fixed-pixel children
    // get left behind.
    //
    // Four fit modes, selectable via the `--odd-scale-mode` custom USS
    // property the importer writes onto the wrapper inline style:
    //   * contain (default) — uniform min(W/Wd, H/Hd); letterbox where aspects mismatch.
    //   * cover             — uniform max(W/Wd, H/Hd); design fills panel, edges may clip.
    //   * stretch           — non-uniform (W/Wd, H/Hd); distorts but fills exactly.
    //   * fill              — resize the child's box to the wrapper's bounds without
    //                         any transform. Background layers (gradients, carbon-fiber
    //                         patterns, back-panel images) stretch with the box; child
    //                         elements with absolute positioning stay anchored to their
    //                         design offsets. Best for fixed designs where you want the
    //                         frame to reach the panel edges without distorting items.
    [UxmlElement]
    public partial class Html2UxmlScaleRoot : VisualElement
    {
        public enum FitMode { Contain, Cover, Stretch, Fill }

        static readonly CustomStyleProperty<string> ScaleModeProp =
            new CustomStyleProperty<string>("--odd-scale-mode");

        VisualElement _hooked;
        FitMode _fit = FitMode.Contain;
        Vector2 _lastScale = new Vector2(-1f, -1f);
        Vector2 _lastOffset = new Vector2(float.NaN, float.NaN);

        public Html2UxmlScaleRoot()
        {
            style.flexGrow = 1f;
            style.alignItems = Align.Center;
            style.justifyContent = Justify.Center;
            style.overflow = Overflow.Hidden;
            RegisterCallback<GeometryChangedEvent>(OnSelfGeometry);
            RegisterCallback<AttachToPanelEvent>(_ => HookFirstChild());
            RegisterCallback<CustomStyleResolvedEvent>(OnCustomStyleResolved);
        }

        void OnCustomStyleResolved(CustomStyleResolvedEvent evt)
        {
            if (!evt.customStyle.TryGetValue(ScaleModeProp, out var raw))
                return;
            FitMode next = ParseFitMode(raw);
            if (next == _fit) return;
            _fit = next;
            _lastScale = new Vector2(-1f, -1f);
            _lastOffset = new Vector2(float.NaN, float.NaN);
            UpdateScale();
        }

        static FitMode ParseFitMode(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return FitMode.Contain;
            switch (raw.Trim().ToLowerInvariant())
            {
                case "cover":   return FitMode.Cover;
                case "stretch": return FitMode.Stretch;
                case "fill":    return FitMode.Fill;
                default:        return FitMode.Contain;
            }
        }

        void HookFirstChild()
        {
            var child = childCount > 0 ? this[0] : null;
            if (child == _hooked) return;
            if (_hooked != null) _hooked.UnregisterCallback<GeometryChangedEvent>(OnChildGeometry);
            _hooked = child;
            if (_hooked != null) _hooked.RegisterCallback<GeometryChangedEvent>(OnChildGeometry);
            _lastScale = new Vector2(-1f, -1f);
            _lastOffset = new Vector2(float.NaN, float.NaN);
            UpdateScale();
        }

        void OnSelfGeometry(GeometryChangedEvent _)
        {
            HookFirstChild();
            UpdateScale();
        }

        void OnChildGeometry(GeometryChangedEvent _) => UpdateScale();

        void UpdateScale()
        {
            if (_hooked == null) return;
            float w = resolvedStyle.width;
            float h = resolvedStyle.height;
            float dw = _hooked.resolvedStyle.width;
            float dh = _hooked.resolvedStyle.height;
            if (w <= 0f || h <= 0f || dw <= 0f || dh <= 0f) return;

            if (_fit == FitMode.Fill)
            {
                // Resize the child's box to the wrapper's bounds so its
                // background layers stretch to fill the panel. No transform
                // scale is applied — children with absolute positioning
                // stay anchored to their design offsets.
                _hooked.style.position = Position.Absolute;
                _hooked.style.left = 0f;
                _hooked.style.top = 0f;
                _hooked.style.scale = new Scale(Vector3.one);
                _hooked.style.width = w;
                _hooked.style.height = h;
                _lastScale = new Vector2(1f, 1f);
                _lastOffset = Vector2.zero;
                return;
            }

            float sx, sy;
            switch (_fit)
            {
                case FitMode.Cover:
                    sx = sy = Mathf.Max(w / dw, h / dh);
                    break;
                case FitMode.Stretch:
                    sx = w / dw;
                    sy = h / dh;
                    break;
                default:
                    sx = sy = Mathf.Min(w / dw, h / dh);
                    break;
            }

            if (sx <= 0f || sy <= 0f
                || float.IsNaN(sx) || float.IsNaN(sy)
                || float.IsInfinity(sx) || float.IsInfinity(sy)) return;
            var offset = new Vector2((w - dw * sx) * 0.5f, (h - dh * sy) * 0.5f);
            if (Mathf.Approximately(sx, _lastScale.x)
                && Mathf.Approximately(sy, _lastScale.y)
                && Mathf.Approximately(offset.x, _lastOffset.x)
                && Mathf.Approximately(offset.y, _lastOffset.y))
                return;
            _lastScale = new Vector2(sx, sy);
            _lastOffset = offset;
            _hooked.style.position = Position.Absolute;
            _hooked.style.left = offset.x;
            _hooked.style.top = offset.y;
            _hooked.style.transformOrigin = new TransformOrigin(Length.Percent(0f), Length.Percent(0f), 0f);
            _hooked.style.scale = new Scale(new Vector3(sx, sy, 1f));
        }
    }
}
