using UnityEngine.UIElements;

namespace ODDGames.Html2Uxml
{
    [UxmlElement]
    public partial class Html2UxmlGroupBox : GroupBox
    {
        public Html2UxmlGroupBox()
        {
            this.AddManipulator(new Html2UxmlAnimationManipulator());
            this.AddManipulator(new Html2UxmlPaintManipulator());
        }
    }
}
