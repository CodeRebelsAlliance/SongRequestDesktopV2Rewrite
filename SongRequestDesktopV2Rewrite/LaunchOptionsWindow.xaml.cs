using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace SongRequestDesktopV2Rewrite
{
    public partial class LaunchOptionsWindow : Window
    {
        private readonly DispatcherTimer _autoTimer;
        private readonly TimeSpan _autoDuration = TimeSpan.FromSeconds(3);
        private DateTime _autoStartTime;
        private bool _autoRunning;

        private readonly Dictionary<StartupMode, Button> _modeButtons;

        public StartupMode SelectedMode { get; private set; }
        public bool RememberSelection => RememberSelectionCheckBox.IsChecked ?? false;

        public LaunchOptionsWindow(LaunchOptionsState state)
        {
            InitializeComponent();

            state ??= new LaunchOptionsState();
            SelectedMode = state.LastSelectedMode;
            RememberSelectionCheckBox.IsChecked = state.RememberSelection;

            _modeButtons = new Dictionary<StartupMode, Button>
            {
                [StartupMode.SongRequests] = SongRequestsButton,
                [StartupMode.MusicPlayer] = MusicPlayerButton,
                [StartupMode.MusicShare] = MusicShareButton,
                [StartupMode.Soundboard] = SoundboardButton
            };

            HighlightSelectedMode();

            _autoTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(30)
            };
            _autoTimer.Tick += AutoTimer_Tick;

            Loaded += (_, _) =>
            {
                Keyboard.Focus(this);

                if (state.RememberSelection)
                {
                    StartAutoSelection();
                }
                else
                {
                    AutoStartText.Text = "Select an option to continue.";
                }
            };
        }

        private void StartAutoSelection()
        {
            _autoRunning = true;
            _autoStartTime = DateTime.UtcNow;
            AutoStartText.Text = "Auto-starting saved selection in 3 seconds... (click anywhere or press Space to change)";
            _autoTimer.Start();
        }

        private void AutoTimer_Tick(object? sender, EventArgs e)
        {
            var elapsed = DateTime.UtcNow - _autoStartTime;
            var progress = Math.Clamp(elapsed.TotalMilliseconds / _autoDuration.TotalMilliseconds, 0, 1);

            ProgressFill.Width = Math.Max(0, (ActualWidth - 60) * progress);

            if (progress >= 1)
            {
                _autoTimer.Stop();
                _autoRunning = false;
                CompleteSelection(SelectedMode);
            }
        }

        private void InterruptAutoSelection()
        {
            if (!_autoRunning)
            {
                return;
            }

            _autoRunning = false;
            _autoTimer.Stop();
            ProgressFill.Width = 0;
            AutoStartText.Text = "Auto-start interrupted. Select an option to continue.";
        }

        private void OptionButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not string modeText)
            {
                return;
            }

            if (!Enum.TryParse<StartupMode>(modeText, out var mode))
            {
                return;
            }

            SelectedMode = mode;
            HighlightSelectedMode();

            if (_autoRunning)
            {
                return;
            }

            CompleteSelection(mode);
        }

        private void CompleteSelection(StartupMode mode)
        {
            SelectedMode = mode;
            DialogResult = true;
            Close();
        }

        private void HighlightSelectedMode()
        {
            foreach (var pair in _modeButtons)
            {
                var isSelected = pair.Key == SelectedMode;
                pair.Value.BorderBrush = isSelected
                    ? new SolidColorBrush(Color.FromRgb(91, 141, 239))
                    : new SolidColorBrush(Color.FromRgb(48, 48, 48));
                pair.Value.Background = isSelected
                    ? new SolidColorBrush(Color.FromRgb(34, 49, 76))
                    : new SolidColorBrush(Color.FromRgb(32, 32, 32));
            }
        }

        private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_autoRunning)
            {
                InterruptAutoSelection();
                e.Handled = true;
            }
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Space && _autoRunning)
            {
                InterruptAutoSelection();
                e.Handled = true;
            }
        }
    }
}
