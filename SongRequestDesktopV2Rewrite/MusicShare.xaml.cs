using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SongRequestDesktopV2Rewrite
{
    public partial class MusicShare : Window
    {
        private readonly MusicShareService _shareService;
        private readonly MusicPlayer? _musicPlayer;
        private MusicSharePresentation? _presentationWindow;
        private LyricsService _lyricsService = new LyricsService();
        private DispatcherTimer? _metadataUpdateTimer;
        private DispatcherTimer? _statsUpdateTimer;

        // Lyrics caching
        private Song? _lastSongForLyrics = null;
        private string? _cachedLyrics = null;
        private bool _cachedHasSyncedLyrics = false;
        private string? _cachedThumbnail = null;

        // Audio capture for sharing
        private WaveOut? _audioMonitor;
        private BufferedWaveProvider? _captureBuffer;

        // Audio playback for receiving
        private WaveOut? _audioPlayer;
        private BufferedWaveProvider? _playbackBuffer;
        private bool _playbackStarted = false;

        // State
        private bool _isSharing = false;
        private bool _isReceiving = false;
        private DateTime _sessionStartTime;
        private int _bytesSent = 0;
        private int _bytesReceived = 0;

        // Audio buffering for slower, larger chunk sending
        private System.Collections.Generic.List<float> _accumulatedSamples = new();
        private const int TARGET_SAMPLES_PER_SEND = 44100; // ~0.5 seconds of stereo audio at 44.1kHz

        public MusicShare()
        {
            InitializeComponent();

            _shareService = new MusicShareService();
            _shareService.StatusChanged += ShareService_StatusChanged;
            _shareService.ErrorOccurred += ShareService_ErrorOccurred;
            _shareService.MetadataReceived += ShareService_MetadataReceived;
            _shareService.AudioChunkReceived += ShareService_AudioChunkReceived;

            // Try to find MusicPlayer window
            _musicPlayer = Application.Current.Windows.OfType<MusicPlayer>().FirstOrDefault();

            if (_musicPlayer == null)
            {
                StartShareButton.IsEnabled = false;
                ShareStatsText.Text = "Music Player not found";
            }
        }

        #region UI Event Handlers

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void ShareIdInput_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            // Only allow digits
            e.Handled = !Regex.IsMatch(e.Text, "^[0-9]+$");
        }

        private void ShareIdInput_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            // Enable receive button when 6 digits entered
            StartReceiveButton.IsEnabled = ShareIdInput.Text.Length == 6 && !_isSharing;
        }

        private async void StartShareButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isSharing)
            {
                await StopSharingAsync();
            }
            else
            {
                await StartSharingAsync();
            }
        }

        private async void StartReceiveButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isReceiving)
            {
                await StopReceivingAsync();
            }
            else
            {
                await StartReceivingAsync();
            }
        }

        private void ShowPresentationButton_Click(object sender, RoutedEventArgs e)
        {
            if (_presentationWindow == null || _presentationWindow.IsClosed())
            {
                _presentationWindow = new MusicSharePresentation();
                _presentationWindow.Show();
                System.Diagnostics.Debug.WriteLine("📺 Opened Music Share presentation window");
            }
            else
            {
                _presentationWindow.Activate();
            }
        }

        #endregion

        #region Sharing Mode

        private async Task StartSharingAsync()
        {
            if (_musicPlayer == null)
            {
                MessageBox.Show("Music Player not found!", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            try
            {
                StartShareButton.IsEnabled = false;

                // Start share session
                var sessionId = await _shareService.StartSharingAsync();

                // Update UI
                ShareIdText.Text = sessionId;
                ShareIdBorder.Visibility = Visibility.Visible;
                ShareStatusBorder.Visibility = Visibility.Visible;
                StartShareButton.Content = "Stop Sharing";
                StartShareButton.IsEnabled = true;

                // Disable receiving mode
                ShareIdInput.IsEnabled = false;
                StartReceiveButton.IsEnabled = false;

                _isSharing = true;
                _sessionStartTime = DateTime.Now;

                // Start metadata updates
                _metadataUpdateTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(500)
                };
                _metadataUpdateTimer.Tick += MetadataUpdateTimer_Tick;
                _metadataUpdateTimer.Start();

                // Start stats updates
                _statsUpdateTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(1)
                };
                _statsUpdateTimer.Tick += StatsUpdateTimer_Tick;
                _statsUpdateTimer.Start();

                // Subscribe to MusicPlayer events
                if (_musicPlayer != null)
                {
                    _musicPlayer.NowPlayingTick += MusicPlayer_NowPlayingTick;
                    _musicPlayer.AudioSamplesCaptured += MusicPlayer_AudioSamplesCaptured;
                }

                // Audio capture is now handled via MusicPlayer events
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to start sharing: {ex.Message}", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
                StartShareButton.IsEnabled = true;
            }
        }

        private async void MusicPlayer_AudioSamplesCaptured(object? sender, float[] samples)
        {
            if (!_isSharing) return;

            try
            {
                // CRITICAL: Make a defensive copy immediately!
                // The samples array is reused by NAudio and will be overwritten
                float[] samplesCopy = new float[samples.Length];
                Array.Copy(samples, samplesCopy, samples.Length);

                // Accumulate samples in buffer
                _accumulatedSamples.AddRange(samplesCopy);

                // Send when we have accumulated enough samples (larger, slower chunks)
                if (_accumulatedSamples.Count >= TARGET_SAMPLES_PER_SEND)
                {
                    // Take exactly TARGET_SAMPLES_PER_SEND samples and send them
                    float[] chunkToSend = new float[TARGET_SAMPLES_PER_SEND];
                    _accumulatedSamples.CopyTo(0, chunkToSend, 0, TARGET_SAMPLES_PER_SEND);
                    _accumulatedSamples.RemoveRange(0, TARGET_SAMPLES_PER_SEND);

                    // Debug: Log first chunk to verify samples are valid
                    if (_bytesSent == 0 && chunkToSend.Length >= 4)
                    {
                        System.Diagnostics.Debug.WriteLine($"🎤 SENDER: First LARGE chunk - {chunkToSend.Length} samples ({chunkToSend.Length / 44100.0:F3}s of stereo audio)");
                        System.Diagnostics.Debug.WriteLine($"🎤 First samples: [{chunkToSend[0].ToString("F6", System.Globalization.CultureInfo.InvariantCulture)}, {chunkToSend[1].ToString("F6", System.Globalization.CultureInfo.InvariantCulture)}, {chunkToSend[2].ToString("F6", System.Globalization.CultureInfo.InvariantCulture)}, {chunkToSend[3].ToString("F6", System.Globalization.CultureInfo.InvariantCulture)}]");

                        // Verify samples are in valid range
                        bool allValid = true;
                        for (int i = 0; i < Math.Min(100, chunkToSend.Length); i++)
                        {
                            if (Math.Abs(chunkToSend[i]) > 10.0)
                            {
                                allValid = false;
                                System.Diagnostics.Debug.WriteLine($"⚠️ SENDER WARNING: Sample {i} is {chunkToSend[i]:F6} (outside valid range)");
                                break;
                            }
                        }

                        if (allValid)
                        {
                            System.Diagnostics.Debug.WriteLine($"✓ SENDER: All samples in valid range");
                        }
                    }

                    // Send the large chunk
                    await _shareService.SendAudioChunkAsync(chunkToSend, 44100, 2);
                    _bytesSent += chunkToSend.Length * sizeof(float);

                    // Log periodically
                    if (_bytesSent % (1024 * 1024) < (TARGET_SAMPLES_PER_SEND * sizeof(float)))
                    {
                        System.Diagnostics.Debug.WriteLine($"📤 Sent large chunk: {chunkToSend.Length} samples, total {_bytesSent / 1024}KB sent");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to send audio samples: {ex.Message}");
            }
        }

        private async Task StopSharingAsync()
        {
            try
            {
                _metadataUpdateTimer?.Stop();
                _statsUpdateTimer?.Stop();

                if (_musicPlayer != null)
                {
                    _musicPlayer.NowPlayingTick -= MusicPlayer_NowPlayingTick;
                    _musicPlayer.AudioSamplesCaptured -= MusicPlayer_AudioSamplesCaptured;
                }

                await _shareService.StopSharingAsync();

                // Clear cache
                _lastSongForLyrics = null;
                _cachedLyrics = null;
                _cachedHasSyncedLyrics = false;
                _cachedThumbnail = null;

                // Clear accumulated samples buffer
                _accumulatedSamples.Clear();

                // Reset UI
                ShareIdBorder.Visibility = Visibility.Collapsed;
                ShareStatusBorder.Visibility = Visibility.Collapsed;
                StartShareButton.Content = "Start Sharing";
                ShareIdInput.IsEnabled = true;

                _isSharing = false;
                _bytesSent = 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error stopping sharing: {ex.Message}", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void MusicPlayer_NowPlayingTick(object? sender, MusicPlayer.NowPlayingEventArgs e)
        {
            if (!_isSharing) return;

            try
            {
                // Check if song changed - if so, fetch new lyrics and thumbnail
                if (_lastSongForLyrics != e.Current)
                {
                    _lastSongForLyrics = e.Current;

                    // Convert thumbnail to base64 (only when song changes)
                    _cachedThumbnail = null;
                    if (e.Current?.thumbnail?.Source is BitmapSource bitmapSource)
                    {
                        _cachedThumbnail = MusicShareService.BitmapSourceToBase64(bitmapSource);
                    }

                    // Fetch lyrics asynchronously (only when song changes)
                    _cachedLyrics = null;
                    _cachedHasSyncedLyrics = false;

                    if (e.Current != null)
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var lyricsResult = await _lyricsService.GetLyricsAsync(
                                    e.Current.Artist.Replace(" - Topic", ""), 
                                    e.Current.Title, 
                                    e.Current.Duration);

                                if (lyricsResult.Found)
                                {
                                    if (lyricsResult.HasSynced)
                                    {
                                        _cachedLyrics = lyricsResult.SyncedLyrics;
                                        _cachedHasSyncedLyrics = true;
                                    }
                                    else if (!string.IsNullOrWhiteSpace(lyricsResult.PlainLyrics))
                                    {
                                        _cachedLyrics = lyricsResult.PlainLyrics;
                                        _cachedHasSyncedLyrics = false;
                                    }
                                }

                                System.Diagnostics.Debug.WriteLine($"🎤 Fetched lyrics for: {e.Current.Title}");
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"Failed to fetch lyrics: {ex.Message}");
                            }
                        });
                    }
                }

                // Send metadata with cached lyrics and thumbnail
                var metadata = new MusicShareMetadata
                {
                    Title = e.Current?.Title ?? "Unknown",
                    Artist = e.Current?.Artist ?? "Unknown",
                    ElapsedSeconds = e.CurrentTime.TotalSeconds,
                    TotalSeconds = e.TotalTime.TotalSeconds,
                    ThumbnailData = _cachedThumbnail,
                    Lyrics = _cachedLyrics,
                    HasSyncedLyrics = _cachedHasSyncedLyrics
                };

                await _shareService.SendMetadataAsync(metadata);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to send metadata: {ex.Message}");
            }
        }

        private void MetadataUpdateTimer_Tick(object? sender, EventArgs e)
        {
            // Additional metadata updates if needed
        }

        private void StatsUpdateTimer_Tick(object? sender, EventArgs e)
        {
            if (_isSharing)
            {
                var elapsed = DateTime.Now - _sessionStartTime;
                ShareStatsText.Text = $"Streaming [Large Chunks] • {elapsed:mm\\:ss} elapsed • {_bytesSent / 1024}KB sent";
            }
        }

        #endregion

        #region Receiving Mode

        private async Task StartReceivingAsync()
        {
            try
            {
                StartReceiveButton.IsEnabled = false;

                var sessionId = ShareIdInput.Text;

                // Set audio reception flag based on checkbox
                _shareService.ReceiveAudio = ReceiveAudioCheckBox.IsChecked ?? true;

                await _shareService.StartReceivingAsync(sessionId);

                // Update UI
                ShareIdInputBorder.Visibility = Visibility.Collapsed;
                ReceiveStatusBorder.Visibility = Visibility.Visible;
                StartReceiveButton.Content = "Stop Receiving";
                StartReceiveButton.IsEnabled = true;

                // Disable checkbox while receiving
                ReceiveAudioCheckBox.IsEnabled = false;

                // Disable sharing mode
                StartShareButton.IsEnabled = false;

                _isReceiving = true;
                _sessionStartTime = DateTime.Now;

                // Start stats updates
                _statsUpdateTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(1)
                };
                _statsUpdateTimer.Tick += ReceiveStatsUpdateTimer_Tick;
                _statsUpdateTimer.Start();

                // Initialize audio playback ONLY if audio is enabled
                if (_shareService.ReceiveAudio)
                {
                    InitializeAudioPlayback();

                    // Enable volume slider
                    var volumeSlider = FindName("VolumeSlider") as Slider;
                    if (volumeSlider != null)
                    {
                        volumeSlider.IsEnabled = true;
                    }

                    System.Diagnostics.Debug.WriteLine("🔊 Audio playback initialized");
                }
                else
                {
                    // Disable volume slider when audio is off
                    var volumeSlider = FindName("VolumeSlider") as Slider;
                    if (volumeSlider != null)
                    {
                        volumeSlider.IsEnabled = false;
                    }

                    System.Diagnostics.Debug.WriteLine("🔇 Audio playback disabled (metadata only mode)");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to connect: {ex.Message}", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
                StartReceiveButton.IsEnabled = ShareIdInput.Text.Length == 6;
            }
        }

        private async Task StopReceivingAsync()
        {
            try
            {
                _statsUpdateTimer?.Stop();

                await _shareService.StopReceivingAsync();

                _audioPlayer?.Stop();
                _audioPlayer?.Dispose();
                _audioPlayer = null;
                _playbackBuffer = null;
                _playbackStarted = false;

                // Reset UI
                ShareIdInputBorder.Visibility = Visibility.Visible;
                ReceiveStatusBorder.Visibility = Visibility.Collapsed;
                StartReceiveButton.Content = "Start Receiving";
                StartReceiveButton.IsEnabled = ShareIdInput.Text.Length == 6;

                // Re-enable checkbox and volume slider
                ReceiveAudioCheckBox.IsEnabled = true;

                var volumeSlider = FindName("VolumeSlider") as Slider;
                if (volumeSlider != null)
                {
                    volumeSlider.IsEnabled = true;
                }

                StartShareButton.IsEnabled = true;

                _isReceiving = false;
                _bytesReceived = 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error stopping receiving: {ex.Message}", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void InitializeAudioPlayback()
        {
            try
            {
                var waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
                _playbackBuffer = new BufferedWaveProvider(waveFormat)
                {
                    BufferDuration = TimeSpan.FromSeconds(5), // 5 second buffer
                    DiscardOnBufferOverflow = true
                };

                _audioPlayer = new WaveOut();

                // Log device info
                System.Diagnostics.Debug.WriteLine($"🔊 WaveOut devices available: {WaveOut.DeviceCount}");
                for (int i = 0; i < WaveOut.DeviceCount; i++)
                {
                    var caps = WaveOut.GetCapabilities(i);
                    System.Diagnostics.Debug.WriteLine($"   Device {i}: {caps.ProductName}");
                }

                _audioPlayer.Init(_playbackBuffer);

                // Set initial volume from slider
                var volumeSlider = FindName("VolumeSlider") as Slider;
                if (volumeSlider != null)
                {
                    _audioPlayer.Volume = (float)volumeSlider.Value;
                }

                // Handle playback stopped (buffer starvation)
                _audioPlayer.PlaybackStopped += AudioPlayer_PlaybackStopped;

                _playbackStarted = false;
                // Don't start playing yet - wait for buffer to fill

                System.Diagnostics.Debug.WriteLine($"🔊 Audio playback initialized: {waveFormat.SampleRate}Hz, {waveFormat.Channels}ch, {waveFormat.BitsPerSample}bit");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to initialize audio playback: {ex.Message}");
            }
        }

        private void AudioPlayer_PlaybackStopped(object? sender, StoppedEventArgs e)
        {
            // Playback stopped (likely due to buffer starvation)
            // Will be restarted when buffer has enough data
            System.Diagnostics.Debug.WriteLine("Audio playback stopped");
            _playbackStarted = false;
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            try
            {
                // Update volume percentage text
                var volumePercentText = FindName("VolumePercentText") as TextBlock;
                if (volumePercentText != null)
                {
                    volumePercentText.Text = $"{(e.NewValue * 100):F0}%";
                }

                // Update audio player volume
                if (_audioPlayer != null)
                {
                    _audioPlayer.Volume = (float)e.NewValue;
                    System.Diagnostics.Debug.WriteLine($"🔊 Volume set to {(e.NewValue * 100):F0}%");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error setting volume: {ex.Message}");
            }
        }

        private void ReceiveStatsUpdateTimer_Tick(object? sender, EventArgs e)
        {
            if (_isReceiving)
            {
                var elapsed = DateTime.Now - _sessionStartTime;
                var bufferLevel = _shareService.GetBufferLevel();

                // Show audio status in stats
                string audioStatus = _shareService.ReceiveAudio ? $"Buffer: {bufferLevel} large chunks" : "Metadata Only";
                ReceiveStatsText.Text = $"Playing • {elapsed:mm\\:ss} elapsed • {audioStatus}";
            }
        }

        #endregion

        #region Service Event Handlers

        private void ShareService_StatusChanged(object? sender, ShareStatus status)
        {
            Dispatcher.Invoke(() =>
            {
                Color indicatorColor = status switch
                {
                    ShareStatus.Connected or ShareStatus.Streaming => Color.FromRgb(76, 175, 80), // Green
                    ShareStatus.Connecting or ShareStatus.Buffering => Color.FromRgb(255, 193, 7), // Yellow
                    ShareStatus.Error or ShareStatus.Disconnected => Color.FromRgb(229, 57, 53), // Red
                    _ => Color.FromRgb(128, 128, 128) // Gray
                };

                if (_isSharing)
                {
                    ShareStatusIndicator.Fill = new SolidColorBrush(indicatorColor);
                    ShareStatusText.Text = status.ToString();
                }
                else if (_isReceiving)
                {
                    ReceiveStatusIndicator.Fill = new SolidColorBrush(indicatorColor);
                    ReceiveStatusText.Text = status.ToString();
                }
            });
        }

        private void ShareService_ErrorOccurred(object? sender, string error)
        {
            Dispatcher.Invoke(() =>
            {
                MessageBox.Show(error, "Music Share Error", MessageBoxButton.OK, MessageBoxImage.Error);
            });
        }

        private void ShareService_MetadataReceived(object? sender, MusicShareMetadata metadata)
        {
            Dispatcher.Invoke(() =>
            {
                // Update presentation window with received metadata
                if (_presentationWindow != null && !_presentationWindow.IsClosed())
                {
                    _presentationWindow.UpdateFromMusicShare(metadata);
                }

                System.Diagnostics.Debug.WriteLine($"📺 Metadata: {metadata.Title} - {metadata.Artist}");
            });
        }

        private void ShareService_AudioChunkReceived(object? sender, AudioChunk chunk)
        {
            // Skip audio processing if audio reception is disabled
            if (!_shareService.ReceiveAudio)
            {
                return;
            }

            Dispatcher.Invoke(() =>
            {
                try
                {
                    if (_playbackBuffer == null || string.IsNullOrEmpty(chunk.Data))
                    {
                        System.Diagnostics.Debug.WriteLine("⚠ Invalid chunk or buffer");
                        return;
                    }

                    // Convert base64 string back to bytes
                    byte[] audioBytes;
                    try
                    {
                        audioBytes = Convert.FromBase64String(chunk.Data);

                        // Validate audio data - check first few samples
                        if (audioBytes.Length >= 16)
                        {
                            float[] firstSamples = new float[4];
                            Buffer.BlockCopy(audioBytes, 0, firstSamples, 0, 16);

                            // Log first chunk to verify data (use invariant culture for consistent formatting)
                            if (_bytesReceived == 0)
                            {
                                System.Diagnostics.Debug.WriteLine($"🎧 First audio samples: [{firstSamples[0].ToString("F6", System.Globalization.CultureInfo.InvariantCulture)}, {firstSamples[1].ToString("F6", System.Globalization.CultureInfo.InvariantCulture)}, {firstSamples[2].ToString("F6", System.Globalization.CultureInfo.InvariantCulture)}, {firstSamples[3].ToString("F6", System.Globalization.CultureInfo.InvariantCulture)}]");

                                // Check if values are in valid range
                                bool allValid = true;
                                foreach (var sample in firstSamples)
                                {
                                    if (Math.Abs(sample) > 10.0)
                                    {
                                        allValid = false;
                                        break;
                                    }
                                }

                                if (!allValid)
                                {
                                    System.Diagnostics.Debug.WriteLine($"⚠️ WARNING: Audio samples are outside expected range (-1.0 to 1.0)!");
                                    System.Diagnostics.Debug.WriteLine($"   This indicates a format mismatch or encoding issue.");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"✗ Failed to decode base64 audio data: {ex.Message}");
                        return;
                    }

                    // Add samples to buffer
                    int bytesAdded = 0;
                    try
                    {
                        _playbackBuffer.AddSamples(audioBytes, 0, audioBytes.Length);
                        bytesAdded = audioBytes.Length;
                        _bytesReceived += bytesAdded;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"✗ Error adding samples to buffer: {ex.Message}");
                        return;
                    }

                    // Check if we should start/restart playback based on chunk count
                    if (_audioPlayer != null && !_playbackStarted)
                    {
                        var bufferLevel = _shareService.GetBufferLevel();
                        var bufferedBytes = _playbackBuffer.BufferedBytes;
                        var bufferedDuration = _playbackBuffer.BufferedDuration;

                        System.Diagnostics.Debug.WriteLine($"📦 Chunk #{chunk.SequenceNumber}: Added {bytesAdded} bytes");
                        System.Diagnostics.Debug.WriteLine($"   Queue: {bufferLevel} chunks | Buffer: {bufferedBytes} bytes ({bufferedDuration.TotalSeconds:F2}s)");

                        // More aggressive start condition: just need a few chunks
                        bool shouldStart = bufferLevel >= 5 && bufferedBytes > 0;

                        if (shouldStart)
                        {
                            try
                            {
                                System.Diagnostics.Debug.WriteLine($"🎵 Starting playback NOW!");
                                System.Diagnostics.Debug.WriteLine($"   Reason: {bufferLevel} chunks, {bufferedBytes} bytes buffered");
                                System.Diagnostics.Debug.WriteLine($"   Format: {_playbackBuffer.WaveFormat}");
                                System.Diagnostics.Debug.WriteLine($"   Device: {_audioPlayer.DeviceNumber}");

                                // Set volume to max to ensure we can hear
                                _audioPlayer.Volume = 1.0f;

                                _audioPlayer.Play();
                                _playbackStarted = true;

                                System.Diagnostics.Debug.WriteLine($"✓ Playback started! State: {_audioPlayer.PlaybackState}, Volume: {_audioPlayer.Volume}");
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"✗ FAILED to start playback: {ex.GetType().Name}");
                                System.Diagnostics.Debug.WriteLine($"   Message: {ex.Message}");
                                if (ex.InnerException != null)
                                {
                                    System.Diagnostics.Debug.WriteLine($"   Inner: {ex.InnerException.Message}");
                                }
                            }
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"⏳ Waiting... (need 5 chunks, have {bufferLevel})");
                        }
                    }
                    else if (_playbackStarted && _audioPlayer != null)
                    {
                        // Monitor playback state
                        var state = _audioPlayer.PlaybackState;
                        if (state != PlaybackState.Playing)
                        {
                            System.Diagnostics.Debug.WriteLine($"⚠ Playback stopped! State: {state}");
                            _playbackStarted = false;

                            // Try to restart if we have enough buffer
                            var bufferLevel = _shareService.GetBufferLevel();
                            if (bufferLevel >= 5)
                            {
                                System.Diagnostics.Debug.WriteLine($"🔄 Attempting to restart playback...");
                                try
                                {
                                    _audioPlayer.Play();
                                    _playbackStarted = true;
                                    System.Diagnostics.Debug.WriteLine($"✓ Playback restarted!");
                                }
                                catch (Exception ex)
                                {
                                    System.Diagnostics.Debug.WriteLine($"✗ Restart failed: {ex.Message}");
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"✗ Error in AudioChunkReceived: {ex.Message}");
                    System.Diagnostics.Debug.WriteLine($"   Stack: {ex.StackTrace}");
                }
            });
        }

        #endregion

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);

            _metadataUpdateTimer?.Stop();
            _statsUpdateTimer?.Stop();

            if (_isSharing)
            {
                _ = _shareService.StopSharingAsync();
            }
            if (_isReceiving)
            {
                _ = _shareService.StopReceivingAsync();
            }

            _audioMonitor?.Dispose();
            _audioPlayer?.Dispose();
        }
    }

    /// <summary>
    /// Extension method to check if a window is closed
    /// </summary>
    internal static class WindowExtensions
    {
        public static bool IsClosed(this Window window)
        {
            return !Application.Current.Windows.OfType<Window>().Contains(window);
        }
    }
}
