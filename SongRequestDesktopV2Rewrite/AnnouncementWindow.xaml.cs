using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SongRequestDesktopV2Rewrite
{
    public partial class AnnouncementWindow : Window
    {
        private static readonly Color MicIdleColor = Color.FromRgb(204, 56, 56);
        private static readonly Color MicIdleBorderColor = Color.FromRgb(255, 106, 106);
        private static readonly Color MicPreparingColor = Color.FromRgb(227, 176, 49);
        private static readonly Color MicPreparingBorderColor = Color.FromRgb(255, 211, 115);
        private static readonly Color MicLiveColor = Color.FromRgb(63, 173, 80);
        private static readonly Color MicLiveBorderColor = Color.FromRgb(123, 228, 138);

        private readonly MusicPlayer _musicPlayer;
        private CancellationTokenSource? _announcementCts;
        private bool _isAnnouncementActive;
        private bool _isTransitioning;
        private bool _isLoadingSettings;
        private Storyboard? _pulseStoryboard;

        public AnnouncementWindow(MusicPlayer musicPlayer)
        {
            InitializeComponent();
            _musicPlayer = musicPlayer;
            LoadAnnouncementSettings();
            SetMicState(MicIdleColor, MicIdleBorderColor, "Ready");
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void DimDbSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            UpdateDimDbValueText();
            SaveAnnouncementSettings();
        }

        private void AnnouncementControl_Changed(object sender, RoutedEventArgs e)
        {
            SaveAnnouncementSettings();
        }

        private void MicButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (PushToTalkCheckBox.IsChecked != true) return;
            _ = StartAnnouncementAsync();
        }

        private void MicButton_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (PushToTalkCheckBox.IsChecked != true) return;
            _ = StopAnnouncementAsync();
        }

        private void MicButton_Click(object sender, RoutedEventArgs e)
        {
            if (PushToTalkCheckBox.IsChecked == true) return;
            _ = _isAnnouncementActive ? StopAnnouncementAsync() : StartAnnouncementAsync();
        }

        private async Task StartAnnouncementAsync()
        {
            if (_isAnnouncementActive || _isTransitioning) return;
            _isTransitioning = true;

            _announcementCts?.Cancel();
            _announcementCts?.Dispose();
            _announcementCts = new CancellationTokenSource();
            var token = _announcementCts.Token;

            try
            {
                SetMicState(MicPreparingColor, MicPreparingBorderColor, "Preparing announcement...");
                await _musicPlayer.FadeAnnouncementDuckingAsync(DimDbSlider.Value, 500);

                if (token.IsCancellationRequested) return;

                if (PlaySoundCheckBox.IsChecked == true)
                {
                    await PlayAnnouncementSoundAsync(token);
                }

                if (token.IsCancellationRequested) return;

                _isAnnouncementActive = true;
                SetMicState(MicLiveColor, MicLiveBorderColor, PushToTalkCheckBox.IsChecked == true ? "Live: hold to talk" : "Live: click to end");
                StartMicPulseAnimation();
            }
            catch (OperationCanceledException)
            {
                // Ignore cancellations from fast button toggles.
            }
            finally
            {
                _isTransitioning = false;
            }
        }

        private async Task StopAnnouncementAsync()
        {
            if (!_isAnnouncementActive && !_isTransitioning) return;

            _announcementCts?.Cancel();
            StopMicPulseAnimation();
            _isAnnouncementActive = false;
            _isTransitioning = true;

            try
            {
                SetMicState(MicPreparingColor, MicPreparingBorderColor, "Restoring music...");
                await _musicPlayer.FadeAnnouncementRestoreAsync(1000);
            }
            finally
            {
                _isTransitioning = false;
                SetMicState(MicIdleColor, MicIdleBorderColor, "Ready");
            }
        }

        private void SetMicState(Color backgroundColor, Color borderColor, string stateText)
        {
            AnimateButtonBrush(MicButton, Button.BackgroundProperty, backgroundColor);
            AnimateButtonBrush(MicButton, Button.BorderBrushProperty, borderColor);
            MicStateText.Text = stateText;
        }

        private static void AnimateButtonBrush(Button button, DependencyProperty property, Color targetColor)
        {
            var brush = button.GetValue(property) as SolidColorBrush;
            if (brush == null)
            {
                brush = new SolidColorBrush(targetColor);
                button.SetValue(property, brush);
            }
            else if (brush.IsFrozen)
            {
                brush = brush.CloneCurrentValue();
                button.SetValue(property, brush);
            }

            var animation = new ColorAnimation(targetColor, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            brush.BeginAnimation(SolidColorBrush.ColorProperty, animation, HandoffBehavior.SnapshotAndReplace);
        }

        private void StartMicPulseAnimation()
        {
            if (_pulseStoryboard != null)
            {
                _pulseStoryboard.Begin(MicButton, true);
                return;
            }

            var scale = new ScaleTransform(1.0, 1.0);
            MicButton.RenderTransformOrigin = new Point(0.5, 0.5);
            MicButton.RenderTransform = scale;

            _pulseStoryboard = new Storyboard
            {
                RepeatBehavior = RepeatBehavior.Forever,
                AutoReverse = true
            };

            var animX = new DoubleAnimation(1.0, 1.06, TimeSpan.FromMilliseconds(420))
            {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            var animY = animX.Clone();
            Storyboard.SetTarget(animX, MicButton);
            Storyboard.SetTarget(animY, MicButton);
            Storyboard.SetTargetProperty(animX, new PropertyPath("(UIElement.RenderTransform).(ScaleTransform.ScaleX)"));
            Storyboard.SetTargetProperty(animY, new PropertyPath("(UIElement.RenderTransform).(ScaleTransform.ScaleY)"));
            _pulseStoryboard.Children.Add(animX);
            _pulseStoryboard.Children.Add(animY);
            _pulseStoryboard.Begin(MicButton, true);
        }

        private void StopMicPulseAnimation()
        {
            _pulseStoryboard?.Stop(MicButton);
            MicButton.RenderTransform = new ScaleTransform(1.0, 1.0);
        }

        private async Task PlayAnnouncementSoundAsync(CancellationToken token)
        {
            string soundPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "announcement.mp3");
            if (!File.Exists(soundPath))
            {
                await Task.Delay(600, token);
                return;
            }

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var player = new MediaPlayer();
            EventHandler onOpened = null!;
            EventHandler onEnded = null!;
            EventHandler<ExceptionEventArgs> onFailed = null!;

            onOpened = (s, e) =>
            {
                try { player.Play(); } catch { tcs.TrySetResult(true); }
            };
            onEnded = (s, e) => tcs.TrySetResult(true);
            onFailed = (s, e) => tcs.TrySetResult(true);

            player.MediaOpened += onOpened;
            player.MediaEnded += onEnded;
            player.MediaFailed += onFailed;

            using var registration = token.Register(() => tcs.TrySetCanceled(token));
            try
            {
                player.Open(new Uri(soundPath, UriKind.Absolute));
                await tcs.Task;
            }
            finally
            {
                player.MediaOpened -= onOpened;
                player.MediaEnded -= onEnded;
                player.MediaFailed -= onFailed;
                player.Stop();
                player.Close();
            }
        }

        private void LoadAnnouncementSettings()
        {
            var config = ConfigService.Instance.Current;
            if (config == null)
            {
                UpdateDimDbValueText();
                return;
            }

            _isLoadingSettings = true;
            try
            {
                DimDbSlider.Value = Math.Clamp(config.AnnouncementDimDb, DimDbSlider.Minimum, DimDbSlider.Maximum);
                PlaySoundCheckBox.IsChecked = config.AnnouncementPlaySound;
                PushToTalkCheckBox.IsChecked = config.AnnouncementPushToTalk;
                UpdateDimDbValueText();
            }
            finally
            {
                _isLoadingSettings = false;
            }
        }

        private void SaveAnnouncementSettings()
        {
            if (_isLoadingSettings) return;
            if (DimDbSlider == null || PlaySoundCheckBox == null || PushToTalkCheckBox == null) return;

            ConfigService.Instance.Update(config =>
            {
                config.AnnouncementDimDb = Math.Round(DimDbSlider.Value);
                config.AnnouncementPlaySound = PlaySoundCheckBox.IsChecked == true;
                config.AnnouncementPushToTalk = PushToTalkCheckBox.IsChecked == true;
            });
        }

        private void UpdateDimDbValueText()
        {
            if (DimDbValueText == null || DimDbSlider == null) return;
            DimDbValueText.Text = $"{Math.Round(DimDbSlider.Value):0} dB";
        }

        protected override async void OnClosed(EventArgs e)
        {
            _announcementCts?.Cancel();
            _announcementCts?.Dispose();
            _announcementCts = null;

            if (_isAnnouncementActive || _isTransitioning)
            {
                await _musicPlayer.FadeAnnouncementRestoreAsync(1000);
            }

            base.OnClosed(e);
        }
    }
}
