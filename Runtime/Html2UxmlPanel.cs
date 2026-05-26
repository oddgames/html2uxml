using UnityEngine.UIElements;

namespace ODDGames.Html2Uxml
{
    // Plain VisualElement wrapper that wires up the shared Html2Uxml paint and
    // animation manipulators. Kept as a UXML-visible element so the converter
    // can keep emitting <odd:Html2UxmlPanel> with the existing factory binding.
    // All CSS paint state and rendering lives in Html2UxmlPaintManipulator.
    [UxmlElement]
    public partial class Html2UxmlPanel : VisualElement
    {
        public Html2UxmlPanel()
        {
            this.AddManipulator(new Html2UxmlPaintManipulator());
            this.AddManipulator(new Html2UxmlAnimationManipulator());
        }
    }
}
