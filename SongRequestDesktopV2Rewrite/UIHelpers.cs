using System.Windows;

namespace SongRequestDesktopV2Rewrite
{
    public static class UIHelpers
    {
        public static readonly DependencyProperty IsLoadingProperty =
            DependencyProperty.RegisterAttached(
                "IsLoading",
                typeof(bool),
                typeof(UIHelpers),
                new PropertyMetadata(false));

        public static void SetIsLoading(DependencyObject element, bool value)
        {
            element.SetValue(IsLoadingProperty, value);
        }

        public static bool GetIsLoading(DependencyObject element)
        {
            return (bool)element.GetValue(IsLoadingProperty);
        }
    }
}
