using UnityEngine;
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
        string _placeholder;
        Label _placeholderLabel;

        [UxmlAttribute("password")]
        public bool Password
        {
            get => isPasswordField;
            set => isPasswordField = value;
        }

        [UxmlAttribute("placeholder")]
        public string Placeholder
        {
            get => _placeholder;
            set
            {
                _placeholder = value ?? string.Empty;
                EnsurePlaceholderLabel();
                UpdatePlaceholderVisibility();
            }
        }

        public Html2UxmlTextField()
        {
            this.AddManipulator(new Html2UxmlAnimationManipulator());
            this.AddManipulator(new Html2UxmlPaintManipulator());
            this.RegisterValueChangedCallback(_ => UpdatePlaceholderVisibility());
            RegisterCallback<AttachToPanelEvent>(_ => ScheduleFieldSync());
            schedule.Execute(SyncFieldVisuals).ExecuteLater(0);
        }

        void ScheduleFieldSync()
        {
            schedule.Execute(SyncFieldVisuals).ExecuteLater(0);
        }

        void SyncFieldVisuals()
        {
            Html2UxmlFieldStyling.ResetTextInputChrome(this);
            EnsurePlaceholderLabel();
            UpdatePlaceholderVisibility();
        }

        void EnsurePlaceholderLabel()
        {
            if (string.IsNullOrEmpty(_placeholder) || _placeholderLabel != null)
                return;

            _placeholderLabel = new Label
            {
                name = "html2uxml-placeholder",
                pickingMode = PickingMode.Ignore,
                text = _placeholder,
            };
            _placeholderLabel.AddToClassList("html2uxml-placeholder");
            _placeholderLabel.style.position = Position.Absolute;
            _placeholderLabel.style.left = 0;
            _placeholderLabel.style.right = 0;
            _placeholderLabel.style.top = 0;
            _placeholderLabel.style.bottom = 0;
            _placeholderLabel.style.marginLeft = 0;
            _placeholderLabel.style.marginRight = 0;
            _placeholderLabel.style.marginTop = 0;
            _placeholderLabel.style.marginBottom = 0;
            _placeholderLabel.style.paddingLeft = 0;
            _placeholderLabel.style.paddingRight = 0;
            _placeholderLabel.style.paddingTop = 0;
            _placeholderLabel.style.paddingBottom = 0;
            _placeholderLabel.style.color = new Color(0f, 0f, 0f, 0.45f);
            _placeholderLabel.style.unityTextAlign = TextAnchor.MiddleLeft;
            _placeholderLabel.style.overflow = Overflow.Hidden;
            hierarchy.Add(_placeholderLabel);
        }

        void UpdatePlaceholderVisibility()
        {
            if (_placeholderLabel == null)
                return;
            _placeholderLabel.text = _placeholder;
            _placeholderLabel.style.display =
                !string.IsNullOrEmpty(_placeholder) && string.IsNullOrEmpty(value)
                    ? DisplayStyle.Flex
                    : DisplayStyle.None;
        }
    }

    [UxmlElement]
    public partial class Html2UxmlFloatField : FloatField
    {
        public Html2UxmlFloatField()
        {
            this.AddManipulator(new Html2UxmlAnimationManipulator());
            this.AddManipulator(new Html2UxmlPaintManipulator());
            RegisterCallback<AttachToPanelEvent>(_ => ScheduleFieldSync());
            schedule.Execute(SyncFieldVisuals).ExecuteLater(0);
        }

        void ScheduleFieldSync()
        {
            schedule.Execute(SyncFieldVisuals).ExecuteLater(0);
        }

        void SyncFieldVisuals()
        {
            Html2UxmlFieldStyling.ResetTextInputChrome(this);
        }
    }

    static class Html2UxmlFieldStyling
    {
        public static void ResetTextInputChrome(VisualElement field)
        {
            var input = field.Q("unity-text-input");
            if (input == null)
                return;

            input.style.backgroundColor = Color.clear;
            input.style.borderTopWidth = 0;
            input.style.borderRightWidth = 0;
            input.style.borderBottomWidth = 0;
            input.style.borderLeftWidth = 0;
            input.style.marginLeft = 0;
            input.style.marginRight = 0;
            input.style.marginTop = 0;
            input.style.marginBottom = 0;
            input.style.paddingLeft = 0;
            input.style.paddingRight = 0;
            input.style.paddingTop = 0;
            input.style.paddingBottom = 0;
            input.style.flexGrow = 1;
            input.style.unityTextAlign = TextAnchor.MiddleLeft;
        }

        public static void ResetToggleChrome(Toggle toggle)
        {
            ResetChoiceChrome(
                toggle,
                Toggle.inputUssClassName,
                Toggle.checkmarkUssClassName,
                Toggle.textUssClassName,
                false);
        }

        public static void ResetRadioButtonChrome(RadioButton radio)
        {
            ResetChoiceChrome(
                radio,
                RadioButton.inputUssClassName,
                RadioButton.checkmarkBackgroundUssClassName,
                RadioButton.textUssClassName,
                true);
        }

        static void ResetChoiceChrome(
            VisualElement field,
            string inputClassName,
            string checkmarkClassName,
            string textClassName,
            bool round)
        {
            field.style.flexDirection = FlexDirection.Row;
            field.style.alignItems = Align.Center;
            field.style.flexGrow = 0;
            field.style.flexShrink = 0;
            field.style.marginTop = 0;
            field.style.marginBottom = 0;

            var input = field.Q(className: inputClassName);
            if (input != null)
            {
                input.style.width = 16;
                input.style.height = 16;
                input.style.minWidth = 16;
                input.style.minHeight = 16;
                input.style.maxWidth = 16;
                input.style.maxHeight = 16;
                input.style.flexGrow = 0;
                input.style.flexShrink = 0;
                input.style.marginLeft = 0;
                input.style.marginRight = 0;
                input.style.marginTop = 0;
                input.style.marginBottom = 0;
                input.style.paddingLeft = 0;
                input.style.paddingRight = 0;
                input.style.paddingTop = 0;
                input.style.paddingBottom = 0;
                input.style.backgroundColor = Color.clear;
                input.style.borderTopWidth = 0.5f;
                input.style.borderRightWidth = 0.5f;
                input.style.borderBottomWidth = 0.5f;
                input.style.borderLeftWidth = 0.5f;
                input.style.borderTopColor = new Color(0.45f, 0.45f, 0.45f, 1f);
                input.style.borderRightColor = new Color(0.45f, 0.45f, 0.45f, 1f);
                input.style.borderBottomColor = new Color(0.45f, 0.45f, 0.45f, 1f);
                input.style.borderLeftColor = new Color(0.45f, 0.45f, 0.45f, 1f);
                float radius = round ? 8f : 2f;
                input.style.borderTopLeftRadius = radius;
                input.style.borderTopRightRadius = radius;
                input.style.borderBottomRightRadius = radius;
                input.style.borderBottomLeftRadius = radius;
            }

            var checkmark = field.Q(className: checkmarkClassName);
            if (checkmark != null)
            {
                checkmark.style.width = 16;
                checkmark.style.height = 16;
                checkmark.style.minWidth = 16;
                checkmark.style.minHeight = 16;
                checkmark.style.marginLeft = 0;
                checkmark.style.marginRight = 0;
                checkmark.style.marginTop = 0;
                checkmark.style.marginBottom = 0;
            }

            var text = field.Q(className: textClassName);
            if (text != null)
            {
                text.style.marginLeft = 4;
                text.style.marginRight = 16;
                text.style.marginTop = 0;
                text.style.marginBottom = 0;
                text.style.unityTextAlign = TextAnchor.MiddleLeft;
            }
        }

        public static void ResetSliderChrome(Slider slider)
        {
            slider.showInputField = false;
            slider.style.flexGrow = 0;
            slider.style.flexShrink = 0;

            var input = slider.Q(className: Slider.inputUssClassName);
            if (input != null)
            {
                input.style.flexGrow = 1;
                input.style.height = 20;
                input.style.marginLeft = 0;
                input.style.marginRight = 0;
                input.style.marginTop = 0;
                input.style.marginBottom = 0;
            }

            var tracker = slider.Q(className: BaseSlider<float>.trackerUssClassName);
            if (tracker != null)
            {
                tracker.style.height = 4;
                tracker.style.backgroundColor = new Color(0.9f, 0.9f, 0.9f, 1f);
                tracker.style.borderTopWidth = 0.5f;
                tracker.style.borderRightWidth = 0.5f;
                tracker.style.borderBottomWidth = 0.5f;
                tracker.style.borderLeftWidth = 0.5f;
                tracker.style.borderTopColor = new Color(0.7f, 0.7f, 0.7f, 1f);
                tracker.style.borderRightColor = new Color(0.7f, 0.7f, 0.7f, 1f);
                tracker.style.borderBottomColor = new Color(0.7f, 0.7f, 0.7f, 1f);
                tracker.style.borderLeftColor = new Color(0.7f, 0.7f, 0.7f, 1f);
                tracker.style.borderTopLeftRadius = 2;
                tracker.style.borderTopRightRadius = 2;
                tracker.style.borderBottomRightRadius = 2;
                tracker.style.borderBottomLeftRadius = 2;
            }

            var fill = slider.Q(className: BaseSlider<float>.fillUssClassName);
            if (fill != null)
            {
                fill.style.height = 4;
                fill.style.backgroundColor = new Color(0.05f, 0.48f, 0.86f, 1f);
                fill.style.borderTopLeftRadius = 2;
                fill.style.borderTopRightRadius = 2;
                fill.style.borderBottomRightRadius = 2;
                fill.style.borderBottomLeftRadius = 2;
            }

            var dragger = slider.Q(className: BaseSlider<float>.draggerUssClassName);
            if (dragger != null)
            {
                dragger.style.width = 16;
                dragger.style.height = 16;
                dragger.style.backgroundColor = new Color(0.05f, 0.48f, 0.86f, 1f);
                dragger.style.borderTopWidth = 0;
                dragger.style.borderRightWidth = 0;
                dragger.style.borderBottomWidth = 0;
                dragger.style.borderLeftWidth = 0;
                dragger.style.borderTopLeftRadius = 8;
                dragger.style.borderTopRightRadius = 8;
                dragger.style.borderBottomRightRadius = 8;
                dragger.style.borderBottomLeftRadius = 8;
            }
        }
    }

    [UxmlElement]
    public partial class Html2UxmlSlider : Slider
    {
        public Html2UxmlSlider()
        {
            this.AddManipulator(new Html2UxmlAnimationManipulator());
            this.AddManipulator(new Html2UxmlPaintManipulator());
            RegisterCallback<AttachToPanelEvent>(_ => ScheduleFieldSync());
            schedule.Execute(SyncFieldVisuals).ExecuteLater(0);
        }

        void ScheduleFieldSync()
        {
            schedule.Execute(SyncFieldVisuals).ExecuteLater(0);
        }

        void SyncFieldVisuals()
        {
            Html2UxmlFieldStyling.ResetSliderChrome(this);
        }
    }

    [UxmlElement]
    public partial class Html2UxmlToggle : Toggle
    {
        public Html2UxmlToggle()
        {
            this.AddManipulator(new Html2UxmlAnimationManipulator());
            this.AddManipulator(new Html2UxmlPaintManipulator());
            RegisterCallback<AttachToPanelEvent>(_ => ScheduleFieldSync());
            schedule.Execute(SyncFieldVisuals).ExecuteLater(0);
        }

        void ScheduleFieldSync()
        {
            schedule.Execute(SyncFieldVisuals).ExecuteLater(0);
        }

        void SyncFieldVisuals()
        {
            Html2UxmlFieldStyling.ResetToggleChrome(this);
        }
    }

    [UxmlElement]
    public partial class Html2UxmlRadioButton : RadioButton
    {
        public Html2UxmlRadioButton()
        {
            this.AddManipulator(new Html2UxmlAnimationManipulator());
            this.AddManipulator(new Html2UxmlPaintManipulator());
            RegisterCallback<AttachToPanelEvent>(_ => ScheduleFieldSync());
            schedule.Execute(SyncFieldVisuals).ExecuteLater(0);
        }

        void ScheduleFieldSync()
        {
            schedule.Execute(SyncFieldVisuals).ExecuteLater(0);
        }

        void SyncFieldVisuals()
        {
            Html2UxmlFieldStyling.ResetRadioButtonChrome(this);
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
