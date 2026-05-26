using UnityEngine;
using UnityEngine.UIElements;

namespace ODDGames.Html2Uxml
{
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
            input.style.flexGrow = 1;
            input.style.unityTextAlign = TextAnchor.MiddleLeft;
        }
    }
}
