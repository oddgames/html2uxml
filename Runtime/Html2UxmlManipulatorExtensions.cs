using UnityEngine.UIElements;

namespace ODDGames.Html2Uxml
{
    internal static class Html2UxmlManipulatorExtensions
    {
        public static void AddManipulator(this VisualElement element, Manipulator manipulator)
        {
            if (element == null || manipulator == null)
                return;
            manipulator.target = element;
        }

        public static void RemoveManipulator(this VisualElement element, Manipulator manipulator)
        {
            if (element == null || manipulator == null)
                return;
            if (manipulator.target == element)
                manipulator.target = null;
        }
    }
}
