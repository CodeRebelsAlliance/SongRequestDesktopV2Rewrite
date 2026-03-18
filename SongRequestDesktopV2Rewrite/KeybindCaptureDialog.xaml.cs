using System.Windows;
using System.Windows.Input;

namespace SongRequestDesktopV2Rewrite
{
    public partial class KeybindCaptureDialog : Window
    {
        public string SelectedGesture { get; private set; }

        public KeybindCaptureDialog(string currentGesture)
        {
            InitializeComponent();
            SelectedGesture = currentGesture ?? string.Empty;
            DetectedKeyText.Text = string.IsNullOrWhiteSpace(SelectedGesture)
                ? "No shortcut detected"
                : SelectedGesture;

            Loaded += (_, _) => Keyboard.Focus(this);
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var key = KeyboardShortcutHelper.NormalizeKey(e.Key, e.SystemKey);

            if (KeyboardShortcutHelper.IsModifierKey(key))
            {
                return;
            }

            SelectedGesture = KeyboardShortcutHelper.BuildGesture(key, Keyboard.Modifiers);
            DetectedKeyText.Text = SelectedGesture;
            e.Handled = true;
        }

        private void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(SelectedGesture))
            {
                MessageBox.Show("Press a key combination first.", "Shortcut", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
