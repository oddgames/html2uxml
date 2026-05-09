using UnityEngine.UIElements;

namespace ODDGames.Html2Uxml
{
    // Button variant retained as a UXML-visible element so the converter can
    // emit <odd:Html2UxmlButton> for buttons that need future runtime hooks.
    // CSS gap is now baked into per-child margins at conversion time, so no
    // style-mutation callbacks are wired up here.
    [UxmlElement]
    public partial class Html2UxmlButton : Button
    {
        public Html2UxmlButton()
        {
            this.AddManipulator(new Html2UxmlAnimationManipulator());
            this.AddManipulator(new Html2UxmlPaintManipulator());
        }
    }
}
