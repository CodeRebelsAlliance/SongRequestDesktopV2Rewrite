using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NAudio.Wave;

namespace SongRequestDesktopV2Rewrite
{
    public partial class SoundboardWindow : Window
    {
        private SoundboardConfiguration _config;

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

            InitializeSoundboardGrid();
            UpdatePageInfo();
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
                if (context.Data.IsEmpty)
                {
                    System.Diagnostics.Debug.WriteLine("Cannot play empty button");
                    return;
                }

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
                var outputDevice = new WaveOutEvent();

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

                System.Diagnostics.Debug.WriteLine($"▶ Started playback: {context.Data.Name} (Loop: {shouldLoop})");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error playing sound:\n{ex.Message}", 
                    "Playback Error", MessageBoxButton.OK, MessageBoxImage.Error);
                System.Diagnostics.Debug.WriteLine($"❌ Playback error: {ex.Message}");
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

                if (!remainingPlaybacks.Any() && playback.Button != null)
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
                context.Data.SoundFile = Path.GetFileName(dialog.SoundFilePath);

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

        #region Drag and Drop Handlers

        private void Button_PreviewDragEnter(object sender, DragEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine("🎯 PreviewDragEnter triggered");

            if (sender is Button button && button.Tag is ButtonContext context)
            {
                System.Diagnostics.Debug.WriteLine($"   Button: {button.Content}, IsEmpty: {context.Data.IsEmpty}");

                // Only show effect for empty buttons with audio files
                if (context.Data.IsEmpty && e.Data.GetDataPresent(DataFormats.FileDrop))
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
                    System.Diagnostics.Debug.WriteLine($"   ✗ Button not empty or no file drop");
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
                if (context.Data.IsEmpty && e.Data.GetDataPresent(DataFormats.FileDrop))
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
        }

        private void StopAllButton_Click(object sender, RoutedEventArgs e)
        {
            StopAllSounds();
        }

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
