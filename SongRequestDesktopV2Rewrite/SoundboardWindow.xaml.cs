using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NAudio.Wave;
using NAudio.Midi;

namespace SongRequestDesktopV2Rewrite
{
    public partial class SoundboardWindow : Window
    {
        private SoundboardConfiguration _config;
        private MidiService _midiService;
        private bool _isAssignMode = false;

        // Supported audio formats
        private static readonly string[] SupportedFormats = { ".mp3", ".wav", ".m4a", ".aac", ".ogg", ".flac", ".wma" };

        // Playback tracking
        private class PlaybackInstance
        {
            public WaveOutEvent OutputDevice { get; set; }
            public AudioFileReader AudioFile { get; set; }
            public Button Button { get; set; }
            public ButtonContext Context { get; set; }
            public DispatcherTimer ProgressTimer { get; set; }
            public bool IsLooping { get; set; }
            public bool IsManuallyStopping { get; set; }
            public string PageId { get; set; } // Track which page this playback belongs to
        }

        private List<PlaybackInstance> _activePlaybacks = new List<PlaybackInstance>();

        public SoundboardWindow()
        {
            InitializeComponent();

            // Load configuration
            _config = SoundboardConfiguration.Load();

            // Initialize MIDI service
            _midiService = new MidiService();
            _midiService.MidiMessageReceived += MidiService_MidiMessageReceived;
            _midiService.ErrorOccurred += MidiService_ErrorOccurred;

            InitializeSoundboardGrid();
            UpdatePageInfo();
            InitializeAudioDevices();
            InitializeMasterVolume();
            InitializeMidi();
        }

        private void InitializeAudioDevices()
        {
            try
            {
                // Enumerate available output devices
                AudioDeviceCombo.Items.Clear();

                // Add default device
                AudioDeviceCombo.Items.Add(new ComboBoxItem
                {
                    Content = "Default Audio Device",
                    Tag = -1
                });

                // Add all available devices
                for (int i = 0; i < WaveOut.DeviceCount; i++)
                {
                    var capabilities = WaveOut.GetCapabilities(i);
                    AudioDeviceCombo.Items.Add(new ComboBoxItem
                    {
                        Content = capabilities.ProductName,
                        Tag = i
                    });
                }

                // Select saved device or default
                int targetDevice = _config.OutputDeviceNumber;
                foreach (ComboBoxItem item in AudioDeviceCombo.Items)
                {
                    if ((int)item.Tag == targetDevice)
                    {
                        AudioDeviceCombo.SelectedItem = item;
                        break;
                    }
                }

                if (AudioDeviceCombo.SelectedItem == null)
                {
                    AudioDeviceCombo.SelectedIndex = 0; // Default device
                }

                System.Diagnostics.Debug.WriteLine($"✓ Initialized {WaveOut.DeviceCount} audio devices");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"⚠ Error enumerating audio devices: {ex.Message}");
                AudioDeviceCombo.Items.Add(new ComboBoxItem { Content = "Default Audio Device", Tag = -1 });
                AudioDeviceCombo.SelectedIndex = 0;
            }
        }

        private void InitializeMasterVolume()
        {
            MasterVolumeSlider.Value = _config.MasterVolume * 100;
            MasterVolumeLabel.Text = $"{(int)MasterVolumeSlider.Value}%";
        }

        private void InitializeSoundboardGrid()
        {
            var currentPage = _config.GetCurrentPage();

            // Clear existing buttons
            SoundboardGrid.Children.Clear();

            // Set grid dimensions from current page
            SoundboardGrid.Columns = currentPage.Columns;
            SoundboardGrid.Rows = currentPage.Rows;

            // Calculate total buttons for this page
            int totalButtons = currentPage.TotalSlots;

            // Create soundboard buttons from page data
            for (int i = 0; i < totalButtons; i++)
            {
                // Calculate position in 12x12 grid
                int row = i / currentPage.Columns;
                int col = i % currentPage.Columns;

                // Get button data from 12x12 array
                var buttonData = currentPage.GetButton(row, col);

                var button = new Button
                {
                    Content = buttonData.IsEmpty ? $"Empty {i + 1}" : buttonData.Name,
                    Style = (Style)FindResource("SoundboardButtonStyle"),
                    IsEnabled = true, // Always enabled to receive drag-and-drop events
                    Tag = new ButtonContext { Data = buttonData, Row = row, Col = col }
                };

                // Apply button color from data
                if (!string.IsNullOrEmpty(buttonData.Color))
                {
                    try
                    {
                        button.Background = (Brush)new BrushConverter().ConvertFromString(buttonData.Color);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"⚠ Invalid color '{buttonData.Color}': {ex.Message}");
                    }
                }

                // Set visual state for empty buttons
                if (buttonData.IsEmpty)
                {
                    button.Opacity = 0.6; // Make empty buttons slightly transparent
                }

                // Add click handler
                button.Click += SoundboardButton_Click;

                // Add right-click context menu
                button.MouseRightButtonUp += SoundboardButton_RightClick;

                // Add drag-and-drop handlers
                button.PreviewDragEnter += Button_PreviewDragEnter;
                button.PreviewDragOver += Button_PreviewDragOver;
                button.PreviewDragLeave += Button_PreviewDragLeave;
                button.PreviewDrop += Button_PreviewDrop;

                SoundboardGrid.Children.Add(button);
            }

            // Update button references in active playbacks
            UpdateActivePlaybackReferences();

            System.Diagnostics.Debug.WriteLine($"✓ Initialized soundboard grid: {currentPage.Columns}×{currentPage.Rows} with {totalButtons} buttons");
        }

        /// <summary>
        /// Update button references in active playbacks after grid recreation
        /// </summary>
        private void UpdateActivePlaybackReferences()
        {
            var currentPage = _config.GetCurrentPage();

            foreach (var playback in _activePlaybacks)
            {
                // Only update button references for playbacks on the current page
                if (playback.PageId != currentPage.Id)
                {
                    // This playback is from a different page, set button to null
                    playback.Button = null;
                    continue;
                }

                // Find the new button at the same position
                var newButton = FindButtonAtPosition(playback.Context.Row, playback.Context.Col);
                if (newButton != null)
                {
                    playback.Button = newButton;

                    // Update the button's visual state immediately
                    if (playback.AudioFile != null)
                    {
                        var elapsed = playback.AudioFile.CurrentTime;
                        var total = playback.AudioFile.TotalTime;
                        var progress = total.TotalSeconds > 0 ? elapsed.TotalSeconds / total.TotalSeconds : 0;
                        UpdateButtonPlaybackUI(newButton, elapsed, total, progress);
                    }
                }
            }

            System.Diagnostics.Debug.WriteLine($"✓ Updated {_activePlaybacks.Count(p => p.PageId == currentPage.Id)} playback references for current page");
        }

        /// <summary>
        /// Find button in grid at specific row/col position
        /// </summary>
        private Button FindButtonAtPosition(int row, int col)
        {
            foreach (var child in SoundboardGrid.Children)
            {
                if (child is Button button && button.Tag is ButtonContext context)
                {
                    if (context.Row == row && context.Col == col)
                    {
                        return button;
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Helper class to store button context
        /// </summary>
        private class ButtonContext
        {
            public SoundboardButton Data { get; set; }
            public int Row { get; set; }
            public int Col { get; set; }
        }

        private void SoundboardButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is ButtonContext context)
            {
                // Check if in assign mode
                if (_isAssignMode)
                {
                    OpenMidiAssignDialog(context);
                    return;
                }

                if (context.Data.IsEmpty)
                {
                    System.Diagnostics.Debug.WriteLine("Cannot play empty button");
                    return;
                }

                PlaySound(button, context);
            }
        }

        private void OpenMidiAssignDialog(ButtonContext context)
        {
            var dialog = new MidiAssignDialog(_midiService, context.Data.MidiMapping)
            {
                Owner = this
            };

            if (dialog.ShowDialog() == true && dialog.Result != null)
            {
                // Save MIDI mapping to button
                context.Data.MidiMapping = dialog.Result;

                // Update button in page
                var currentPage = _config.GetCurrentPage();
                currentPage.SetButton(context.Row, context.Col, context.Data);
                _config.Save();

                // Update feedback for all buttons (including empty button defaults)
                UpdateAllMidiFeedback();

                System.Diagnostics.Debug.WriteLine($"✓ MIDI mapped to button '{context.Data.Name}': Channel {dialog.Result.Channel}, Note {dialog.Result.Note}");

                MessageBox.Show($"MIDI mapping assigned!\n\nChannel: {dialog.Result.Channel}\nNote/CC: {dialog.Result.Note}\n\nYou can now exit assign mode.", 
                    "Assignment Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            // Exit assign mode after assignment (successful or cancelled)
            _isAssignMode = false;
            MidiAssignModeButton.Content = "🎯 Assign";
            MidiAssignModeButton.ClearValue(Button.BackgroundProperty);
        }

        private void TriggerButton(Button button)
        {
            if (button.Tag is ButtonContext context && !context.Data.IsEmpty)
            {
                PlaySound(button, context);
            }
        }

        private void PlaySound(Button button, ButtonContext context)
        {
            var soundFile = Path.Combine(SoundboardConfiguration.GetSoundboardFolder(), context.Data.SoundFile);

            if (!File.Exists(soundFile))
            {
                MessageBox.Show($"Sound file not found:\n{context.Data.SoundFile}", 
                    "File Not Found", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var repeatMode = context.Data.RepeatMode ?? "none";
            var currentPageId = _config.GetCurrentPage().Id;

            // Check if this specific button has an active playback on the current page
            var existingPlayback = _activePlaybacks.FirstOrDefault(p => 
                p.Context.Row == context.Row && 
                p.Context.Col == context.Col && 
                p.Context.Data.SoundFile == context.Data.SoundFile &&
                p.PageId == currentPageId); // Same page check

            if (existingPlayback != null)
            {
                // Sound is already playing from this button
                switch (repeatMode)
                {
                    case "none": // Play-Stop: Stop on reclick
                        StopPlayback(existingPlayback);
                        System.Diagnostics.Debug.WriteLine($"⏸ Stopped (play-stop mode): {context.Data.Name}");
                        break;

                    case "loop": // Play-Loop: Stop looping on reclick
                        StopPlayback(existingPlayback);
                        System.Diagnostics.Debug.WriteLine($"⏸ Stopped loop: {context.Data.Name}");
                        break;

                    case "overlay": // Play-Overlay: Start new overlapping instance
                        StartNewPlayback(button, context, soundFile, false);
                        System.Diagnostics.Debug.WriteLine($"▶ Started overlay instance: {context.Data.Name}");
                        break;
                }
            }
            else
            {
                // Not playing - start new playback
                bool shouldLoop = repeatMode == "loop";
                StartNewPlayback(button, context, soundFile, shouldLoop);
            }
        }

        private void StartNewPlayback(Button button, ButtonContext context, string soundFile, bool shouldLoop)
        {
            try
            {
                var audioFile = new AudioFileReader(soundFile);

                // Use configured output device
                var outputDevice = new WaveOutEvent
                {
                    DeviceNumber = _config.OutputDeviceNumber
                };

                outputDevice.Init(audioFile);
                outputDevice.Play();

                var playback = new PlaybackInstance
                {
                    OutputDevice = outputDevice,
                    AudioFile = audioFile,
                    Button = button,
                    Context = context,
                    IsLooping = shouldLoop,
                    PageId = _config.GetCurrentPage().Id // Store current page ID
                };

                // Apply master and button volumes
                ApplyVolume(playback);

                // Setup progress timer
                playback.ProgressTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(50)
                };
                playback.ProgressTimer.Tick += (s, e) => UpdatePlaybackProgress(playback);
                playback.ProgressTimer.Start();

                // Handle playback stopped
                outputDevice.PlaybackStopped += (s, e) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        // Don't restart if manually stopped
                        if (playback.IsManuallyStopping)
                        {
                            return;
                        }

                        if (playback.IsLooping && playback.AudioFile != null)
                        {
                            // Restart for loop mode
                            try
                            {
                                playback.AudioFile.Position = 0;
                                playback.OutputDevice.Play();
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"⚠ Loop restart error: {ex.Message}");
                            }
                        }
                        else
                        {
                            // Clean up after playback ends
                            StopPlayback(playback);
                        }
                    });
                };

                _activePlaybacks.Add(playback);

                // Send MIDI feedback for pressed state
                UpdateMidiFeedback(context.Data, true);

                System.Diagnostics.Debug.WriteLine($"▶ Started playback: {context.Data.Name} (Loop: {shouldLoop}, Volume: {context.Data.Volume:P0})");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error playing sound:\n{ex.Message}", 
                    "Playback Error", MessageBoxButton.OK, MessageBoxImage.Error);
                System.Diagnostics.Debug.WriteLine($"❌ Playback error: {ex.Message}");
            }
        }

        private void ApplyVolume(PlaybackInstance playback)
        {
            if (playback?.AudioFile == null) return;

            try
            {
                // Calculate final volume: button volume × master volume
                float buttonVolume = playback.Context.Data.Volume;
                float masterVolume = _config.MasterVolume;
                float finalVolume = buttonVolume * masterVolume;

                playback.AudioFile.Volume = Math.Clamp(finalVolume, 0f, 1f);

                System.Diagnostics.Debug.WriteLine($"🔊 Volume applied: Button={buttonVolume:P0}, Master={masterVolume:P0}, Final={finalVolume:P0}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"⚠ Error applying volume: {ex.Message}");
            }
        }

        private void UpdatePlaybackProgress(PlaybackInstance playback)
        {
            if (playback.AudioFile == null || playback.Button == null)
            {
                // Button is null when playback is on a different page
                return;
            }

            try
            {
                var elapsed = playback.AudioFile.CurrentTime;
                var total = playback.AudioFile.TotalTime;
                var progress = total.TotalSeconds > 0 ? elapsed.TotalSeconds / total.TotalSeconds : 0;

                // For overlay mode, show the progress of the most recent playback
                // For other modes, just update the single playback
                var buttonPlaybacks = _activePlaybacks.Where(p => 
                    p.Context.Row == playback.Context.Row && 
                    p.Context.Col == playback.Context.Col &&
                    p.PageId == playback.PageId).ToList(); // Check same page

                // Update UI with the most recent playback (last in list)
                if (buttonPlaybacks.Any() && buttonPlaybacks.Last() == playback)
                {
                    UpdateButtonPlaybackUI(playback.Button, elapsed, total, progress);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"⚠ Progress update error: {ex.Message}");
            }
        }

        private void UpdateButtonPlaybackUI(Button button, TimeSpan elapsed, TimeSpan total, double progress)
        {
            // Find progress bar in button template
            var progressBar = FindVisualChild<Border>(button, "ProgressBar");
            if (progressBar != null && button.ActualWidth > 0)
            {
                progressBar.Width = button.ActualWidth * progress;
            }

            // Find status text
            var statusText = FindVisualChild<TextBlock>(button, "StatusText");
            if (statusText != null)
            {
                statusText.Text = $"{elapsed:mm\\:ss} / {total:mm\\:ss}";
            }
        }

        private void StopPlayback(PlaybackInstance playback)
        {
            if (playback == null) return;

            try
            {
                // Set flag to prevent loop restart
                playback.IsManuallyStopping = true;

                playback.ProgressTimer?.Stop();
                playback.OutputDevice?.Stop();
                playback.OutputDevice?.Dispose();
                playback.AudioFile?.Dispose();

                _activePlaybacks.Remove(playback);

                // Only reset button UI if there are no more active playbacks for this button on this page
                var remainingPlaybacks = _activePlaybacks.Where(p => 
                    p.Context.Row == playback.Context.Row && 
                    p.Context.Col == playback.Context.Col &&
                    p.PageId == playback.PageId).ToList(); // Same page check

                // Only reset UI and MIDI if no more playbacks for this button
                if (!remainingPlaybacks.Any())
                {
                    if (playback.Button != null)
                    {
                        var progressBar = FindVisualChild<Border>(playback.Button, "ProgressBar");
                        if (progressBar != null)
                        {
                            progressBar.Width = 0;
                        }

                        var statusText = FindVisualChild<TextBlock>(playback.Button, "StatusText");
                        if (statusText != null)
                        {
                            statusText.Text = "Ready";
                        }
                    }

                    // Send MIDI feedback for unpressed state only when all playbacks stopped
                    UpdateMidiFeedback(playback.Context.Data, false);
                }

                System.Diagnostics.Debug.WriteLine($"■ Stopped playback: {playback.Context.Data.Name}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"⚠ Stop playback error: {ex.Message}");
            }
        }

        private void StopAllSounds()
        {
            var playbacksCopy = _activePlaybacks.ToList();
            foreach (var playback in playbacksCopy)
            {
                StopPlayback(playback);
            }
            System.Diagnostics.Debug.WriteLine("■ Stopped all sounds");
        }

        private void SoundboardButton_RightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is Button button && button.Tag is ButtonContext context)
            {
                // Don't show context menu for empty buttons
                if (context.Data.IsEmpty)
                {
                    return;
                }

                // Create context menu
                var contextMenu = new ContextMenu
                {
                    Background = new SolidColorBrush(Color.FromRgb(32, 32, 32)),
                    Foreground = Brushes.White
                };

                // Edit menu item
                var editItem = new MenuItem
                {
                    Header = "✏️ Edit",
                    Foreground = Brushes.White
                };
                editItem.Click += (s, args) => EditButton_Click(context);
                contextMenu.Items.Add(editItem);

                // Clear MIDI Mapping menu item (only show if mapped)
                if (context.Data.MidiMapping != null && context.Data.MidiMapping.IsConfigured)
                {
                    var clearMidiItem = new MenuItem
                    {
                        Header = "🎹 Clear MIDI Mapping",
                        Foreground = Brushes.White
                    };
                    clearMidiItem.Click += (s, args) => ClearMidiMapping_Click(context);
                    contextMenu.Items.Add(clearMidiItem);
                }

                // Delete menu item
                var deleteItem = new MenuItem
                {
                    Header = "🗑️ Delete",
                    Foreground = Brushes.White
                };
                deleteItem.Click += (s, args) => DeleteButton_Click(context);
                contextMenu.Items.Add(deleteItem);

                // Show context menu
                contextMenu.PlacementTarget = button;
                contextMenu.IsOpen = true;

                e.Handled = true;
            }
        }

        private void EditButton_Click(ButtonContext context)
        {
            var dialog = new ButtonEditDialog(context.Data)
            {
                Owner = this
            };

            if (dialog.ShowDialog() == true)
            {
                // Update button data
                context.Data.Name = dialog.ButtonName;
                context.Data.Color = dialog.ButtonColor;
                context.Data.Icon = dialog.ButtonIcon;
                context.Data.FadeIn = dialog.FadeIn;
                context.Data.FadeOut = dialog.FadeOut;
                context.Data.RepeatMode = dialog.RepeatMode;
                context.Data.Volume = dialog.ButtonVolume;
                context.Data.SoundFile = Path.GetFileName(dialog.SoundFilePath);
                // Preserve MIDI mapping (it's already in context.Data.MidiMapping)

                // Update length if file changed
                if (!string.IsNullOrEmpty(dialog.SoundFilePath))
                {
                    try
                    {
                        using (var audioFile = new AudioFileReader(dialog.SoundFilePath))
                        {
                            context.Data.Length = audioFile.TotalTime.TotalSeconds;
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"⚠ Could not read audio duration: {ex.Message}");
                    }
                }

                // Save to page
                var currentPage = _config.GetCurrentPage();
                currentPage.SetButton(context.Row, context.Col, context.Data);
                _config.Save();

                // Refresh grid
                InitializeSoundboardGrid();
                UpdatePageInfo();

                System.Diagnostics.Debug.WriteLine($"✓ Button edited: {context.Data.Name}");
            }
        }

        private void DeleteButton_Click(ButtonContext context)
        {
            var result = MessageBox.Show(
                $"Are you sure you want to delete '{context.Data.Name}'?",
                "Delete Sound",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
            {
                return;
            }

            var soundFile = context.Data.SoundFile;

            // Reset button to empty state
            context.Data.Name = "Empty";
            context.Data.SoundFile = string.Empty;
            context.Data.Icon = "🔊";
            context.Data.Color = "#2A2A2A";
            context.Data.Length = 0;
            context.Data.FadeIn = false;
            context.Data.FadeOut = false;
            context.Data.RepeatMode = "none";
            context.Data.IsEnabled = false;

            // Clear MIDI mapping when deleting button
            if (context.Data.MidiMapping != null)
            {
                UpdateMidiFeedback(context.Data, false); // Turn off LED
                context.Data.MidiMapping = null;
            }

            // Save to page
            var currentPage = _config.GetCurrentPage();
            currentPage.SetButton(context.Row, context.Col, context.Data);
            _config.Save();

            // Check if any other button uses this sound file
            bool fileUsedElsewhere = false;
            foreach (var page in _config.Pages)
            {
                for (int i = 0; i < page.Buttons.Length; i++)
                {
                    if (!page.Buttons[i].IsEmpty && page.Buttons[i].SoundFile == soundFile)
                    {
                        fileUsedElsewhere = true;
                        break;
                    }
                }
                if (fileUsedElsewhere) break;
            }

            // Delete file if not used elsewhere
            if (!fileUsedElsewhere && !string.IsNullOrEmpty(soundFile))
            {
                try
                {
                    var filePath = Path.Combine(SoundboardConfiguration.GetSoundboardFolder(), soundFile);
                    if (File.Exists(filePath))
                    {
                        File.Delete(filePath);
                        System.Diagnostics.Debug.WriteLine($"✓ Deleted file: {soundFile}");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"⚠ Could not delete file: {ex.Message}");
                }
            }
            else if (fileUsedElsewhere)
            {
                System.Diagnostics.Debug.WriteLine($"⚠ File '{soundFile}' not deleted - used by other buttons");
            }

            // Refresh grid
            InitializeSoundboardGrid();
            UpdatePageInfo();

            System.Diagnostics.Debug.WriteLine($"✓ Button deleted and reset to empty");
        }

        private void ClearMidiMapping_Click(ButtonContext context)
        {
            var result = MessageBox.Show(
                $"Clear MIDI mapping for '{context.Data.Name}'?",
                "Clear MIDI Mapping",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
            {
                return;
            }

            // Turn off LED before clearing
            UpdateMidiFeedback(context.Data, false);

            // Clear mapping
            context.Data.MidiMapping = null;

            // Save to page
            var currentPage = _config.GetCurrentPage();
            currentPage.SetButton(context.Row, context.Col, context.Data);
            _config.Save();

            System.Diagnostics.Debug.WriteLine($"✓ MIDI mapping cleared for: {context.Data.Name}");

            MessageBox.Show("MIDI mapping cleared successfully.", "MIDI Mapping", 
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        #region Drag and Drop Handlers

        private void Button_PreviewDragEnter(object sender, DragEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine("🎯 PreviewDragEnter triggered");

            if (sender is Button button && button.Tag is ButtonContext context)
            {
                System.Diagnostics.Debug.WriteLine($"   Button: {button.Content}, IsEmpty: {context.Data.IsEmpty}");

                // Only show effect for empty buttons
                if (!context.Data.IsEmpty)
                {
                    System.Diagnostics.Debug.WriteLine("   ✗ Button not empty");
                    e.Effects = DragDropEffects.None;
                    e.Handled = true;
                    return;
                }

                // Check for song request drag (from YoutubeForm)
                if (e.Data.GetDataPresent("SongRequestDrag"))
                {
                    System.Diagnostics.Debug.WriteLine("   ✓ Song request drag detected - showing green effect");
                    e.Effects = DragDropEffects.Copy;
                    SetDragOverEffect(button, true);
                    e.Handled = true;
                    return;
                }

                // Check for file drop (from Explorer)
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                    if (files != null && files.Length > 0)
                    {
                        System.Diagnostics.Debug.WriteLine($"   File: {files[0]}");

                        if (IsAudioFile(files[0]))
                        {
                            System.Diagnostics.Debug.WriteLine("   ✓ Valid audio file - showing green effect");
                            e.Effects = DragDropEffects.Copy;
                            SetDragOverEffect(button, true);
                            e.Handled = true;
                            return;
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine("   ✗ Not an audio file");
                        }
                    }
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"   ✗ No valid drag data");
                }
            }

            e.Effects = DragDropEffects.None;
            e.Handled = true;
        }

        private void Button_PreviewDragOver(object sender, DragEventArgs e)
        {
            if (sender is Button button && button.Tag is ButtonContext context)
            {
                // Only allow drop on empty buttons
                if (!context.Data.IsEmpty)
                {
                    e.Effects = DragDropEffects.None;
                    e.Handled = true;
                    return;
                }

                // Check for song request drag
                if (e.Data.GetDataPresent("SongRequestDrag"))
                {
                    e.Effects = DragDropEffects.Copy;
                    e.Handled = true;
                    return;
                }

                // Check for file drop
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                    if (files != null && files.Length > 0 && IsAudioFile(files[0]))
                    {
                        e.Effects = DragDropEffects.Copy;
                        e.Handled = true;
                        return;
                    }
                }
            }

            e.Effects = DragDropEffects.None;
            e.Handled = true;
        }

        private void Button_PreviewDragLeave(object sender, DragEventArgs e)
        {
            if (sender is Button button)
            {
                SetDragOverEffect(button, false);
            }
        }

        private async void Button_PreviewDrop(object sender, DragEventArgs e)
        {
            SetDragOverEffect(sender as Button, false);

            if (sender is Button button && button.Tag is ButtonContext context)
            {
                // Only allow drop on empty buttons
                if (!context.Data.IsEmpty)
                {
                    MessageBox.Show("This button already has a sound assigned.", 
                        "Cannot Add Sound", MessageBoxButton.OK, MessageBoxImage.Information);
                    e.Handled = true;
                    return;
                }

                // Check for song request drag (from YoutubeForm)
                if (e.Data.GetDataPresent("SongRequestDrag"))
                {
                    var dragData = e.Data.GetData("SongRequestDrag") as YoutubeForm.DragSongData;
                    if (dragData != null)
                    {
                        try
                        {
                            await ProcessSongRequestDropAsync(dragData, context);
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show($"Error adding song to soundboard:\n{ex.Message}", 
                                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                            System.Diagnostics.Debug.WriteLine($"❌ Error processing song drop: {ex.Message}");
                        }
                    }
                    e.Handled = true;
                    return;
                }

                // Check for file drop (from Explorer)
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                    if (files != null && files.Length > 0)
                    {
                        var filePath = files[0];

                        if (!IsAudioFile(filePath))
                        {
                            MessageBox.Show($"Unsupported file format.\nSupported formats: {string.Join(", ", SupportedFormats)}", 
                                "Unsupported Format", MessageBoxButton.OK, MessageBoxImage.Warning);
                            e.Handled = true;
                            return;
                        }

                        try
                        {
                            await ProcessAudioFileAsync(filePath, context);
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show($"Error processing audio file:\n{ex.Message}", 
                                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                            System.Diagnostics.Debug.WriteLine($"❌ Error processing audio file: {ex.Message}");
                        }
                    }
                }
            }

            e.Handled = true;
        }

        private void SetDragOverEffect(Button button, bool isDraggingOver)
        {
            if (button == null) return;

            try
            {
                // Find the DragOverOverlay in the button's template
                var border = FindVisualChild<Border>(button, "DragOverOverlay");
                if (border != null)
                {
                    border.Opacity = isDraggingOver ? 1 : 0;
                }

                // Change button background and border for additional feedback
                if (isDraggingOver)
                {
                    button.Background = new SolidColorBrush(Color.FromRgb(76, 209, 124)) { Opacity = 0.3 }; // Green tint
                    button.BorderBrush = new SolidColorBrush(Color.FromRgb(76, 209, 124)); // Green border
                    button.BorderThickness = new Thickness(3);
                }
                else
                {
                    button.ClearValue(Button.BackgroundProperty); // Reset to style default
                    button.BorderBrush = new SolidColorBrush(Color.FromRgb(64, 64, 64));
                    button.BorderThickness = new Thickness(2);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"⚠ Error setting drag effect: {ex.Message}");
            }
        }

        private T FindVisualChild<T>(DependencyObject parent, string name) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);

                if (child is T typedChild && (child as FrameworkElement)?.Name == name)
                {
                    return typedChild;
                }

                var result = FindVisualChild<T>(child, name);
                if (result != null)
                {
                    return result;
                }
            }

            return null;
        }

        private bool IsAudioFile(string filePath)
        {
            var extension = Path.GetExtension(filePath)?.ToLowerInvariant();
            return SupportedFormats.Contains(extension);
        }

        private async System.Threading.Tasks.Task ProcessAudioFileAsync(string sourceFilePath, ButtonContext context)
        {
            var fileName = Path.GetFileName(sourceFilePath);
            var soundboardFolder = SoundboardConfiguration.GetSoundboardFolder();
            var destFilePath = Path.Combine(soundboardFolder, fileName);

            // Check if file already exists
            if (File.Exists(destFilePath))
            {
                var result = MessageBox.Show(
                    $"A file named '{fileName}' already exists.\nDo you want to overwrite it?",
                    "File Exists",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result != MessageBoxResult.Yes)
                {
                    return;
                }
            }

            // Copy file to soundboard folder
            await System.Threading.Tasks.Task.Run(() =>
            {
                File.Copy(sourceFilePath, destFilePath, true);
            });

            System.Diagnostics.Debug.WriteLine($"✓ Copied audio file to: {destFilePath}");

            // Get audio duration
            double duration = 0;
            try
            {
                using (var audioFile = new AudioFileReader(destFilePath))
                {
                    duration = audioFile.TotalTime.TotalSeconds;
                }
                System.Diagnostics.Debug.WriteLine($"✓ Audio duration: {duration:F2} seconds");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"⚠ Could not read audio duration: {ex.Message}");
            }

            // Update button data
            var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
            context.Data.Name = nameWithoutExt;
            context.Data.SoundFile = fileName;
            context.Data.Length = duration;
            context.Data.IsEnabled = true;

            // Save to page
            var currentPage = _config.GetCurrentPage();
            currentPage.SetButton(context.Row, context.Col, context.Data);

            // Save configuration
            _config.Save();

            // Refresh grid to show updated button
            InitializeSoundboardGrid();
            UpdatePageInfo();

            System.Diagnostics.Debug.WriteLine($"✓ Sound added: {nameWithoutExt} ({duration:F2}s) at position ({context.Row}, {context.Col})");
        }

        private async System.Threading.Tasks.Task ProcessSongRequestDropAsync(YoutubeForm.DragSongData dragData, ButtonContext context)
        {
            if (string.IsNullOrEmpty(dragData.FilePath) || !File.Exists(dragData.FilePath))
            {
                MessageBox.Show("Audio file not found for this song request.", 
                    "File Not Found", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Generate filename from title (sanitize for filesystem)
            var safeTitle = SanitizeFileName(dragData.Title);
            var sourceExtension = Path.GetExtension(dragData.FilePath);
            var fileName = safeTitle + sourceExtension;

            var soundboardFolder = SoundboardConfiguration.GetSoundboardFolder();
            var destFilePath = Path.Combine(soundboardFolder, fileName);

            // Check if file already exists
            if (File.Exists(destFilePath))
            {
                var result = MessageBox.Show(
                    $"A file named '{fileName}' already exists in the soundboard folder.\nDo you want to use it anyway?",
                    "File Exists",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result != MessageBoxResult.Yes)
                {
                    return;
                }
            }
            else
            {
                // Copy file to soundboard folder
                await System.Threading.Tasks.Task.Run(() =>
                {
                    File.Copy(dragData.FilePath, destFilePath, false);
                });

                System.Diagnostics.Debug.WriteLine($"✓ Copied song request audio to: {destFilePath}");
            }

            // Get audio duration
            double duration = 0;
            try
            {
                using (var audioFile = new AudioFileReader(destFilePath))
                {
                    duration = audioFile.TotalTime.TotalSeconds;
                }
                System.Diagnostics.Debug.WriteLine($"✓ Audio duration: {duration:F2} seconds");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"⚠ Could not read audio duration: {ex.Message}");
            }

            // Update button data with song title
            context.Data.Name = TruncateTitle(dragData.Title, 30); // Truncate long titles
            context.Data.SoundFile = fileName;
            context.Data.Length = duration;
            context.Data.IsEnabled = true;

            // Save to page
            var currentPage = _config.GetCurrentPage();
            currentPage.SetButton(context.Row, context.Col, context.Data);

            // Save configuration
            _config.Save();

            // Refresh grid to show updated button
            InitializeSoundboardGrid();
            UpdatePageInfo();

            System.Diagnostics.Debug.WriteLine($"✓ Song request added: {context.Data.Name} ({duration:F2}s) at position ({context.Row}, {context.Col})");
        }

        private string SanitizeFileName(string fileName)
        {
            // Remove invalid filename characters
            var invalid = Path.GetInvalidFileNameChars();
            var sanitized = string.Join("_", fileName.Split(invalid));

            // Limit length
            if (sanitized.Length > 100)
            {
                sanitized = sanitized.Substring(0, 100);
            }

            return sanitized;
        }

        private string TruncateTitle(string title, int maxLength)
        {
            if (string.IsNullOrEmpty(title))
                return "Untitled";

            if (title.Length <= maxLength)
                return title;

            return title.Substring(0, maxLength - 3) + "...";
        }

        #endregion

        private void UpdatePageInfo()
        {
            var currentPage = _config.GetCurrentPage();

            // Update grid size display
            GridInfoText.Text = $"Grid: {currentPage.Columns} × {currentPage.Rows}";

            // Update page info
            PageInfoText.Text = $"Page {_config.CurrentPageIndex + 1} of {_config.Pages.Count}: {currentPage.Name}";

            // Update page navigation buttons
            PrevPageButton.IsEnabled = _config.CurrentPageIndex > 0;
            NextPageButton.IsEnabled = _config.CurrentPageIndex < _config.Pages.Count - 1;
        }

        private void GridSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            var currentPage = _config.GetCurrentPage();

            // Show grid settings dialog
            var dialog = new GridSettingsDialog(currentPage.Columns, currentPage.Rows)
            {
                Owner = this
            };

            if (dialog.ShowDialog() == true)
            {
                currentPage.Columns = dialog.SelectedColumns;
                currentPage.Rows = dialog.SelectedRows;

                // Save and reinitialize grid with new dimensions
                _config.Save();
                InitializeSoundboardGrid();
                UpdatePageInfo();

                System.Diagnostics.Debug.WriteLine($"Grid resized to {currentPage.Columns}×{currentPage.Rows}");
            }
        }

        private void AddSoundButton_Click(object sender, RoutedEventArgs e)
        {
            // Placeholder for adding sounds
            MessageBox.Show("Add Sound functionality - Coming soon!", 
                "Soundboard", 
                MessageBoxButton.OK, 
                MessageBoxImage.Information);
        }

        private void PrevPageButton_Click(object sender, RoutedEventArgs e)
        {
            if (_config.CurrentPageIndex > 0)
            {
                _config.CurrentPageIndex--;
                _config.Save();
                InitializeSoundboardGrid();
                UpdatePageInfo();

                // Update MIDI feedback for new page
                UpdateAllMidiFeedback();
            }
        }

        private void NextPageButton_Click(object sender, RoutedEventArgs e)
        {
            if (_config.CurrentPageIndex < _config.Pages.Count - 1)
            {
                _config.CurrentPageIndex++;
                _config.Save();
                InitializeSoundboardGrid();
                UpdatePageInfo();

                // Update MIDI feedback for new page
                UpdateAllMidiFeedback();
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);

            // Stop all active playbacks
            StopAllSounds();

            // Cleanup MIDI
            _midiService?.Dispose();
        }

        private void StopAllButton_Click(object sender, RoutedEventArgs e)
        {
            StopAllSounds();
        }

        private void MasterVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_config == null) return;

            _config.MasterVolume = (float)(e.NewValue / 100.0);
            MasterVolumeLabel.Text = $"{(int)e.NewValue}%";

            // Apply volume to all active playbacks
            foreach (var playback in _activePlaybacks)
            {
                ApplyVolume(playback);
            }

            _config.Save();
        }

        private void AudioDeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_config == null || AudioDeviceCombo.SelectedItem == null) return;

            var selectedItem = (ComboBoxItem)AudioDeviceCombo.SelectedItem;
            int deviceNumber = (int)selectedItem.Tag;

            _config.OutputDeviceNumber = deviceNumber;
            _config.Save();

            System.Diagnostics.Debug.WriteLine($"✓ Audio device changed to: {selectedItem.Content} (#{deviceNumber})");

            // Note: Existing playbacks will continue on old device
            // New playbacks will use the new device
        }

        #region MIDI Support

        private void InitializeMidi()
        {
            // Set checkbox state from config
            MidiEnabledCheckBox.IsChecked = _config.MidiEnabled;

            // Enable MIDI if configured
            if (_config.MidiEnabled)
            {
                EnableMidi();
            }
        }

        private void EnableMidi()
        {
            try
            {
                _midiService.IsEnabled = true;

                // Connect input device if configured
                if (_config.MidiInputDevice >= 0)
                {
                    _midiService.ConnectInput(_config.MidiInputDevice);
                }

                // Connect output device if configured
                if (_config.MidiOutputDevice >= 0)
                {
                    _midiService.ConnectOutput(_config.MidiOutputDevice);
                }

                MidiSettingsButton.IsEnabled = true;
                MidiAssignModeButton.IsEnabled = true;

                // Send initial feedback for all buttons
                UpdateAllMidiFeedback();

                System.Diagnostics.Debug.WriteLine("✓ MIDI enabled");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to enable MIDI: {ex.Message}", "MIDI Error", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
                _config.MidiEnabled = false;
                MidiEnabledCheckBox.IsChecked = false;
            }
        }

        private void DisableMidi()
        {
            _midiService.IsEnabled = false;
            _midiService.DisconnectDevices();
            MidiSettingsButton.IsEnabled = false;
            MidiAssignModeButton.IsEnabled = false;
            System.Diagnostics.Debug.WriteLine("✓ MIDI disabled");
        }

        private void MidiEnabledCheckBox_CheckedChanged(object sender, RoutedEventArgs e)
        {
            _config.MidiEnabled = MidiEnabledCheckBox.IsChecked ?? false;
            _config.Save();

            if (_config.MidiEnabled)
            {
                EnableMidi();
            }
            else
            {
                DisableMidi();
            }
        }

        private void MidiSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new MidiSettingsDialog(_midiService, _config)
            {
                Owner = this
            };

            dialog.ShowDialog();

            // Refresh MIDI connections after settings change
            if (_config.MidiEnabled)
            {
                EnableMidi();
            }
        }

        private void MidiAssignModeButton_Click(object sender, RoutedEventArgs e)
        {
            _isAssignMode = !_isAssignMode;

            if (_isAssignMode)
            {
                MidiAssignModeButton.Content = "✓ Assigning...";
                MidiAssignModeButton.Background = new SolidColorBrush(Color.FromRgb(255, 193, 7)); // Yellow
                MessageBox.Show("MIDI Assign Mode Active\n\nClick on any soundboard button to assign a MIDI control to it.", 
                    "Assign Mode", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MidiAssignModeButton.Content = "🎯 Assign";
                MidiAssignModeButton.ClearValue(Button.BackgroundProperty); // Reset to style
            }
        }

        private void MidiService_MidiMessageReceived(object? sender, NAudio.Midi.MidiInMessageEventArgs e)
        {
            if (!_config.MidiEnabled || _isAssignMode) return;

            Dispatcher.Invoke(() =>
            {
                try
                {
                    // Find button with matching MIDI mapping
                    var currentPage = _config.GetCurrentPage();

                    for (int i = 0; i < currentPage.TotalSlots; i++)
                    {
                        int row = i / currentPage.Columns;
                        int col = i % currentPage.Columns;
                        var buttonData = currentPage.GetButton(row, col);

                        if (buttonData.MidiMapping != null && buttonData.MidiMapping.IsConfigured)
                        {
                            bool matched = false;

                            if (e.MidiEvent is NoteOnEvent noteOn)
                            {
                                matched = buttonData.MidiMapping.MessageType == "NoteOn" &&
                                         buttonData.MidiMapping.Channel == noteOn.Channel &&
                                         buttonData.MidiMapping.Note == noteOn.NoteNumber &&
                                         noteOn.Velocity > 0;
                            }
                            else if (e.MidiEvent is ControlChangeEvent cc)
                            {
                                matched = buttonData.MidiMapping.MessageType == "ControlChange" &&
                                         buttonData.MidiMapping.Channel == cc.Channel &&
                                         buttonData.MidiMapping.Note == (int)cc.Controller &&
                                         cc.ControllerValue > 0;
                            }

                            if (matched)
                            {
                                // Trigger the button
                                var button = FindButtonAtPosition(row, col);
                                if (button != null && !buttonData.IsEmpty)
                                {
                                    System.Diagnostics.Debug.WriteLine($"🎹 MIDI triggered button: {buttonData.Name}");
                                    TriggerButton(button);
                                }
                                break;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error processing MIDI: {ex.Message}");
                }
            });
        }

        private void MidiService_ErrorOccurred(object? sender, string error)
        {
            Dispatcher.Invoke(() =>
            {
                System.Diagnostics.Debug.WriteLine($"🎹 MIDI Error: {error}");
            });
        }

        private void UpdateMidiFeedback(SoundboardButton buttonData, bool isPressed)
        {
            if (!_config.MidiEnabled || _midiService.OutputDeviceNumber == null) return;
            if (buttonData.MidiMapping == null || !buttonData.MidiMapping.IsConfigured) return;

            try
            {
                int velocity = isPressed ? buttonData.MidiMapping.VelocityPressed : buttonData.MidiMapping.VelocityUnpressed;

                // Scale velocity from 0-127 to 0-255 for controllers that use extended range (like Launchpad X)
                // Launchpad X uses velocity 0-127 for colors, but we stored 0-127 in config
                // The velocity is used directly without scaling - Launchpad X interprets 0-127 as color palette indices

                if (buttonData.MidiMapping.MessageType == "NoteOn")
                {
                    if (velocity > 0)
                    {
                        _midiService.SendNoteOn(buttonData.MidiMapping.Channel, buttonData.MidiMapping.Note, velocity);
                    }
                    else
                    {
                        _midiService.SendNoteOff(buttonData.MidiMapping.Channel, buttonData.MidiMapping.Note);
                    }
                }
                else if (buttonData.MidiMapping.MessageType == "ControlChange")
                {
                    _midiService.SendControlChange(buttonData.MidiMapping.Channel, buttonData.MidiMapping.Note, velocity);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error sending MIDI feedback: {ex.Message}");
            }
        }

        private void UpdateAllMidiFeedback()
        {
            if (!_config.MidiEnabled) return;

            var currentPage = _config.GetCurrentPage();
            var emptyVelocity = ParseVelocityFromColor(_config.EmptyButtonFeedbackColor);

            for (int i = 0; i < currentPage.TotalSlots; i++)
            {
                int row = i / currentPage.Columns;
                int col = i % currentPage.Columns;
                var buttonData = currentPage.GetButton(row, col);

                if (buttonData.MidiMapping != null && buttonData.MidiMapping.IsConfigured)
                {
                    if (buttonData.IsEmpty)
                    {
                        // Send empty button feedback
                        if (buttonData.MidiMapping.MessageType == "NoteOn")
                        {
                            _midiService.SendNoteOn(buttonData.MidiMapping.Channel, buttonData.MidiMapping.Note, emptyVelocity);
                        }
                        else if (buttonData.MidiMapping.MessageType == "ControlChange")
                        {
                            _midiService.SendControlChange(buttonData.MidiMapping.Channel, buttonData.MidiMapping.Note, emptyVelocity);
                        }
                    }
                    else
                    {
                        // Check if button is currently playing
                        bool isPlaying = _activePlaybacks.Any(p => 
                            p.Context.Row == row && p.Context.Col == col && 
                            p.PageId == currentPage.Id);

                        UpdateMidiFeedback(buttonData, isPlaying);
                    }
                }
            }
        }

        private int ParseVelocityFromColor(string color)
        {
            try
            {
                if (string.IsNullOrEmpty(color) || !color.StartsWith("#"))
                    return 10;

                var hex = color.Substring(1);
                if (hex.Length >= 2)
                {
                    int brightness = Convert.ToInt32(hex.Substring(0, 2), 16);
                    return (brightness * 127) / 255;
                }
            }
            catch
            {
                // Fallback
            }

            return 10;
        }

        #endregion

        private void PageManagementButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new PageManagementDialog(_config)
            {
                Owner = this
            };

            if (dialog.ShowDialog() == true)
            {
                // Refresh UI after page management changes
                InitializeSoundboardGrid();
                UpdatePageInfo();
            }
        }
    }

    /// <summary>
    /// Dialog for configuring grid size
    /// </summary>
    public class GridSettingsDialog : Window
    {
        private ComboBox _columnsCombo;
        private ComboBox _rowsCombo;
        
        public int SelectedColumns { get; private set; }
        public int SelectedRows { get; private set; }

        public GridSettingsDialog(int currentColumns, int currentRows)
        {
            Title = "Grid Settings";
            Width = 350;
            Height = 250;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            Background = System.Windows.Media.Brushes.Black;

            SelectedColumns = currentColumns;
            SelectedRows = currentRows;

            // Create UI
            var grid = new Grid
            {
                Margin = new Thickness(20)
            };

            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(20) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(20) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });

            // Title
            var title = new TextBlock
            {
                Text = "Configure Soundboard Grid",
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Foreground = System.Windows.Media.Brushes.White,
                Margin = new Thickness(0, 0, 0, 20)
            };
            Grid.SetRow(title, 0);
            grid.Children.Add(title);

            // Columns
            var columnsPanel = new StackPanel { Orientation = Orientation.Horizontal };
            columnsPanel.Children.Add(new TextBlock 
            { 
                Text = "Columns:", 
                Width = 80, 
                Foreground = System.Windows.Media.Brushes.White,
                VerticalAlignment = VerticalAlignment.Center
            });

            _columnsCombo = new ComboBox { Width = 100 };
            for (int i = 1; i <= 12; i++)
            {
                _columnsCombo.Items.Add(i);
            }
            _columnsCombo.SelectedItem = currentColumns;
            columnsPanel.Children.Add(_columnsCombo);

            Grid.SetRow(columnsPanel, 2);
            grid.Children.Add(columnsPanel);

            // Rows
            var rowsPanel = new StackPanel { Orientation = Orientation.Horizontal };
            rowsPanel.Children.Add(new TextBlock 
            { 
                Text = "Rows:", 
                Width = 80, 
                Foreground = System.Windows.Media.Brushes.White,
                VerticalAlignment = VerticalAlignment.Center
            });

            _rowsCombo = new ComboBox { Width = 100 };
            for (int i = 1; i <= 12; i++)
            {
                _rowsCombo.Items.Add(i);
            }
            _rowsCombo.SelectedItem = currentRows;
            rowsPanel.Children.Add(_rowsCombo);
            
            Grid.SetRow(rowsPanel, 4);
            grid.Children.Add(rowsPanel);

            // Buttons
            var buttonPanel = new StackPanel 
            { 
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var okButton = new Button
            {
                Content = "OK",
                Width = 80,
                Height = 32,
                Margin = new Thickness(0, 0, 8, 0)
            };
            okButton.Click += OkButton_Click;
            buttonPanel.Children.Add(okButton);

            var cancelButton = new Button
            {
                Content = "Cancel",
                Width = 80,
                Height = 32
            };
            cancelButton.Click += (s, e) => { DialogResult = false; Close(); };
            buttonPanel.Children.Add(cancelButton);

            Grid.SetRow(buttonPanel, 5);
            grid.Children.Add(buttonPanel);

            Content = grid;
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            if (_columnsCombo.SelectedItem != null && _rowsCombo.SelectedItem != null)
            {
                SelectedColumns = (int)_columnsCombo.SelectedItem;
                SelectedRows = (int)_rowsCombo.SelectedItem;
                DialogResult = true;
                Close();
            }
        }
    }
}
