using UnityEngine.UIElements;

namespace ODDGames.Html2Uxml
{
    [UxmlElement]
    public partial class Html2UxmlScrollView : ScrollView
    {
        public Html2UxmlScrollView()
        {
            this.AddManipulator(new Html2UxmlAnimationManipulator());
            this.AddManipulator(new Html2UxmlPaintManipulator());
        }
    }
}
