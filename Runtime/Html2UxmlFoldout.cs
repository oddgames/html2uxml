using UnityEngine.UIElements;

namespace ODDGames.Html2Uxml
{
    [UxmlElement]
    public partial class Html2UxmlFoldout : Foldout
    {
        public Html2UxmlFoldout()
        {
            this.AddManipulator(new Html2UxmlAnimationManipulator());
            this.AddManipulator(new Html2UxmlPaintManipulator());
        }
    }
}
