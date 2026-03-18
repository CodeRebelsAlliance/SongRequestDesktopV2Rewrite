using System.Windows;

namespace SongRequestDesktopV2Rewrite
{
    public partial class KeyboardSettingsDialog : Window
    {
        public GlobalKeyboardShortcuts Result { get; private set; }

        public KeyboardSettingsDialog(GlobalKeyboardShortcuts current)
        {
            InitializeComponent();

            Result = Clone(current);
            LoadUi();
        }

        private void LoadUi()
        {
            StopAllEnabled.IsChecked = Result.StopAll.Enabled;
            StopAllGestureText.Text = FormatGesture(Result.StopAll.Gesture);

            VolumeUpEnabled.IsChecked = Result.VolumeUp.Enabled;
            VolumeUpGestureText.Text = FormatGesture(Result.VolumeUp.Gesture);

            VolumeDownEnabled.IsChecked = Result.VolumeDown.Enabled;
            VolumeDownGestureText.Text = FormatGesture(Result.VolumeDown.Gesture);

            NextPageEnabled.IsChecked = Result.NextPage.Enabled;
            NextPageGestureText.Text = FormatGesture(Result.NextPage.Gesture);

            PreviousPageEnabled.IsChecked = Result.PreviousPage.Enabled;
            PreviousPageGestureText.Text = FormatGesture(Result.PreviousPage.Gesture);
        }

        private static string FormatGesture(string gesture)
        {
            return string.IsNullOrWhiteSpace(gesture) ? "(not set)" : gesture;
        }

        private static GlobalKeyboardShortcuts Clone(GlobalKeyboardShortcuts current)
        {
            current ??= new GlobalKeyboardShortcuts();

            return new GlobalKeyboardShortcuts
            {
                StopAll = new KeyboardShortcutConfig
                {
                    Enabled = current.StopAll?.Enabled ?? false,
                    Gesture = current.StopAll?.Gesture ?? "S"
                },
                VolumeUp = new KeyboardShortcutConfig
                {
                    Enabled = current.VolumeUp?.Enabled ?? false,
                    Gesture = current.VolumeUp?.Gesture ?? "Plus"
                },
                VolumeDown = new KeyboardShortcutConfig
                {
                    Enabled = current.VolumeDown?.Enabled ?? false,
                    Gesture = current.VolumeDown?.Gesture ?? "Minus"
                },
                NextPage = new KeyboardShortcutConfig
                {
                    Enabled = current.NextPage?.Enabled ?? false,
                    Gesture = current.NextPage?.Gesture ?? "PageUp"
                },
                PreviousPage = new KeyboardShortcutConfig
                {
                    Enabled = current.PreviousPage?.Enabled ?? false,
                    Gesture = current.PreviousPage?.Gesture ?? "PageDown"
                }
            };
        }

        private void SetStopAll_Click(object sender, RoutedEventArgs e) => CaptureShortcut(Result.StopAll, StopAllGestureText);
        private void SetVolumeUp_Click(object sender, RoutedEventArgs e) => CaptureShortcut(Result.VolumeUp, VolumeUpGestureText);
        private void SetVolumeDown_Click(object sender, RoutedEventArgs e) => CaptureShortcut(Result.VolumeDown, VolumeDownGestureText);
        private void SetNextPage_Click(object sender, RoutedEventArgs e) => CaptureShortcut(Result.NextPage, NextPageGestureText);
        private void SetPreviousPage_Click(object sender, RoutedEventArgs e) => CaptureShortcut(Result.PreviousPage, PreviousPageGestureText);

        private void ClearStopAll_Click(object sender, RoutedEventArgs e) => ClearShortcut(Result.StopAll, StopAllGestureText);
        private void ClearVolumeUp_Click(object sender, RoutedEventArgs e) => ClearShortcut(Result.VolumeUp, VolumeUpGestureText);
        private void ClearVolumeDown_Click(object sender, RoutedEventArgs e) => ClearShortcut(Result.VolumeDown, VolumeDownGestureText);
        private void ClearNextPage_Click(object sender, RoutedEventArgs e) => ClearShortcut(Result.NextPage, NextPageGestureText);
        private void ClearPreviousPage_Click(object sender, RoutedEventArgs e) => ClearShortcut(Result.PreviousPage, PreviousPageGestureText);

        private void CaptureShortcut(KeyboardShortcutConfig config, System.Windows.Controls.TextBlock target)
        {
            var capture = new KeybindCaptureDialog(config.Gesture)
            {
                Owner = this
            };

            if (capture.ShowDialog() == true)
            {
                config.Gesture = capture.SelectedGesture;
                target.Text = FormatGesture(config.Gesture);
            }
        }

        private static void ClearShortcut(KeyboardShortcutConfig config, System.Windows.Controls.TextBlock target)
        {
            config.Gesture = string.Empty;
            config.Enabled = false;
            target.Text = "(not set)";
        }

        private void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            Result.StopAll.Enabled = (StopAllEnabled.IsChecked ?? false) && !string.IsNullOrWhiteSpace(Result.StopAll.Gesture);
            Result.VolumeUp.Enabled = (VolumeUpEnabled.IsChecked ?? false) && !string.IsNullOrWhiteSpace(Result.VolumeUp.Gesture);
            Result.VolumeDown.Enabled = (VolumeDownEnabled.IsChecked ?? false) && !string.IsNullOrWhiteSpace(Result.VolumeDown.Gesture);
            Result.NextPage.Enabled = (NextPageEnabled.IsChecked ?? false) && !string.IsNullOrWhiteSpace(Result.NextPage.Gesture);
            Result.PreviousPage.Enabled = (PreviousPageEnabled.IsChecked ?? false) && !string.IsNullOrWhiteSpace(Result.PreviousPage.Gesture);

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
