using UnityEngine.UIElements;

namespace ODDGames.Html2Uxml
{
    [UxmlElement]
    public partial class Html2UxmlElement : Html2UxmlPanel
    {
        public Html2UxmlElement()
        {
        }
    }

    [UxmlElement]
    public partial class Html2UxmlLabel : Label
    {
        public Html2UxmlLabel()
        {
            this.AddManipulator(new Html2UxmlAnimationManipulator());
            this.AddManipulator(new Html2UxmlPaintManipulator());
        }
    }

    [UxmlElement]
    public partial class Html2UxmlScrollView : ScrollView
    {
        public Html2UxmlScrollView()
        {
            this.AddManipulator(new Html2UxmlAnimationManipulator());
            this.AddManipulator(new Html2UxmlPaintManipulator());
        }
    }

    [UxmlElement]
    public partial class Html2UxmlTextField : TextField
    {
        public Html2UxmlTextField()
        {
            this.AddManipulator(new Html2UxmlAnimationManipulator());
            this.AddManipulator(new Html2UxmlPaintManipulator());
        }
    }

    [UxmlElement]
    public partial class Html2UxmlFloatField : FloatField
    {
        public Html2UxmlFloatField()
        {
            this.AddManipulator(new Html2UxmlAnimationManipulator());
            this.AddManipulator(new Html2UxmlPaintManipulator());
        }
    }

    [UxmlElement]
    public partial class Html2UxmlSlider : Slider
    {
        public Html2UxmlSlider()
        {
            this.AddManipulator(new Html2UxmlAnimationManipulator());
            this.AddManipulator(new Html2UxmlPaintManipulator());
        }
    }

    [UxmlElement]
    public partial class Html2UxmlToggle : Toggle
    {
        public Html2UxmlToggle()
        {
            this.AddManipulator(new Html2UxmlAnimationManipulator());
            this.AddManipulator(new Html2UxmlPaintManipulator());
        }
    }

    [UxmlElement]
    public partial class Html2UxmlRadioButton : RadioButton
    {
        public Html2UxmlRadioButton()
        {
            this.AddManipulator(new Html2UxmlAnimationManipulator());
            this.AddManipulator(new Html2UxmlPaintManipulator());
        }
    }

    [UxmlElement]
    public partial class Html2UxmlDropdownField : DropdownField
    {
        public Html2UxmlDropdownField()
        {
            this.AddManipulator(new Html2UxmlAnimationManipulator());
            this.AddManipulator(new Html2UxmlPaintManipulator());
        }
    }

    [UxmlElement]
    public partial class Html2UxmlProgressBar : ProgressBar
    {
        public Html2UxmlProgressBar()
        {
            this.AddManipulator(new Html2UxmlAnimationManipulator());
            this.AddManipulator(new Html2UxmlPaintManipulator());
        }
    }

    [UxmlElement]
    public partial class Html2UxmlFoldout : Foldout
    {
        public Html2UxmlFoldout()
        {
            this.AddManipulator(new Html2UxmlAnimationManipulator());
            this.AddManipulator(new Html2UxmlPaintManipulator());
        }
    }

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
