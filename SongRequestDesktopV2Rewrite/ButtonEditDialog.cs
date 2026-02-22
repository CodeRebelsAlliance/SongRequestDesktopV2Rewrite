using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace SongRequestDesktopV2Rewrite
{
    public class ButtonEditDialog : Window
    {
        private TextBox _nameTextBox;
        private StackPanel _colorPanel;
        private TextBox _iconTextBox;
        private CheckBox _fadeInCheckBox;
        private CheckBox _fadeOutCheckBox;
        private ComboBox _repeatModeCombo;
        private TextBox _soundFileTextBox;
        private Button _browseSoundButton;
        private TextBox _customColorTextBox;
        private Border _customColorPreview;
        private Slider _volumeSlider;
        private TextBlock _volumeLabel;

        // Predefined colors
        private static readonly string[] PredefinedColors =
        {
            "#2A2A2A", // Default Gray
            "#FF5733", // Red-Orange
            "#33FF57", // Green
            "#3357FF", // Blue
            "#FF33F5", // Magenta
            "#F5FF33", // Yellow
            "#33FFF5", // Cyan
            "#FF8C33", // Orange
            "#8C33FF", // Purple
            "#FF3383"  // Pink
        };

        public string ButtonName { get; private set; }
        public string ButtonColor { get; private set; }
        public string ButtonIcon { get; private set; }
        public bool FadeIn { get; private set; }
        public bool FadeOut { get; private set; }
        public string RepeatMode { get; private set; }
        public string SoundFilePath { get; private set; }
        public float ButtonVolume { get; private set; }

        public ButtonEditDialog(SoundboardButton button)
        {
            Title = "Edit Sound Button";
            Width = 500;
            Height = 600;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            Background = new SolidColorBrush(Color.FromRgb(18, 18, 18));

            // Initialize properties
            ButtonName = button.Name;
            ButtonColor = button.Color;
            ButtonIcon = button.Icon;
            FadeIn = button.FadeIn;
            FadeOut = button.FadeOut;
            RepeatMode = button.RepeatMode;
            ButtonVolume = button.Volume;

            var soundboardFolder = SoundboardConfiguration.GetSoundboardFolder();
            SoundFilePath = string.IsNullOrEmpty(button.SoundFile) 
                ? string.Empty 
                : Path.Combine(soundboardFolder, button.SoundFile);

            BuildUI();
        }

        private void BuildUI()
        {
            var mainGrid = new Grid { Margin = new Thickness(20) };

            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) }); // Title
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(15) });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) }); // Name
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(15) });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) }); // Icon
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(15) });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) }); // Color
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(15) });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) }); // Sound File
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(15) });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) }); // Fade Options
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(15) });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) }); // Repeat Mode
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Spacer
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) }); // Buttons

            int rowIndex = 0;

            // Title
            var title = new TextBlock
            {
                Text = "Edit Sound Button",
                FontSize = 20,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White
            };
            Grid.SetRow(title, rowIndex);
            mainGrid.Children.Add(title);
            rowIndex += 2;

            // Name
            AddLabel(mainGrid, "Name:", rowIndex);
            _nameTextBox = new TextBox
            {
                Text = ButtonName,
                Background = new SolidColorBrush(Color.FromRgb(32, 32, 32)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(91, 141, 239)),
                BorderThickness = new Thickness(2),
                Padding = new Thickness(8),
                FontSize = 14
            };
            Grid.SetRow(_nameTextBox, rowIndex);
            mainGrid.Children.Add(_nameTextBox);
            rowIndex += 2;

            // Icon
            AddLabel(mainGrid, "Icon (emoji):", rowIndex);
            _iconTextBox = new TextBox
            {
                Text = ButtonIcon,
                Background = new SolidColorBrush(Color.FromRgb(32, 32, 32)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(91, 141, 239)),
                BorderThickness = new Thickness(2),
                Padding = new Thickness(8),
                FontSize = 14,
                MaxLength = 4
            };
            Grid.SetRow(_iconTextBox, rowIndex);
            mainGrid.Children.Add(_iconTextBox);
            rowIndex += 2;

            // Color Picker
            AddLabel(mainGrid, "Button Color:", rowIndex);

            var colorContainer = new StackPanel { Orientation = Orientation.Vertical };

            // Predefined colors row
            _colorPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Left
            };

            foreach (var color in PredefinedColors)
            {
                var colorButton = new Button
                {
                    Width = 40,
                    Height = 40,
                    Margin = new Thickness(2),
                    Background = (Brush)new BrushConverter().ConvertFromString(color),
                    BorderThickness = new Thickness(2),
                    BorderBrush = color == ButtonColor 
                        ? Brushes.White 
                        : new SolidColorBrush(Color.FromRgb(64, 64, 64)),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Tag = color
                };
                colorButton.Click += ColorButton_Click;
                _colorPanel.Children.Add(colorButton);
            }

            colorContainer.Children.Add(_colorPanel);

            // Custom color input
            var customColorRow = new StackPanel 
            { 
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 8, 0, 0)
            };

            var customLabel = new TextBlock
            {
                Text = "Custom:",
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                Width = 60
            };
            customColorRow.Children.Add(customLabel);

            _customColorTextBox = new TextBox
            {
                Text = IsCustomColor(ButtonColor) ? ButtonColor : "#",
                Width = 100,
                Background = new SolidColorBrush(Color.FromRgb(32, 32, 32)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(91, 141, 239)),
                BorderThickness = new Thickness(2),
                Padding = new Thickness(8, 4, 8, 4),
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            _customColorTextBox.TextChanged += CustomColorTextBox_TextChanged;
            customColorRow.Children.Add(_customColorTextBox);

            _customColorPreview = new Border
            {
                Width = 40,
                Height = 28,
                Margin = new Thickness(8, 0, 0, 0),
                BorderBrush = new SolidColorBrush(Color.FromRgb(64, 64, 64)),
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(4)
            };

            // Set initial preview color
            if (IsCustomColor(ButtonColor))
            {
                try
                {
                    _customColorPreview.Background = (Brush)new BrushConverter().ConvertFromString(ButtonColor);
                }
                catch { }
            }

            customColorRow.Children.Add(_customColorPreview);

            var colorHint = new TextBlock
            {
                Text = "Format: #RRGGBB",
                Foreground = new SolidColorBrush(Color.FromRgb(136, 136, 136)),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };
            customColorRow.Children.Add(colorHint);

            colorContainer.Children.Add(customColorRow);

            Grid.SetRow(colorContainer, rowIndex);
            mainGrid.Children.Add(colorContainer);
            rowIndex += 2;

            // Sound File
            AddLabel(mainGrid, "Sound File:", rowIndex);
            var soundFileGrid = new Grid();
            soundFileGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            soundFileGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });

            _soundFileTextBox = new TextBox
            {
                Text = Path.GetFileName(SoundFilePath),
                Background = new SolidColorBrush(Color.FromRgb(32, 32, 32)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(91, 141, 239)),
                BorderThickness = new Thickness(2),
                Padding = new Thickness(8),
                FontSize = 14,
                IsReadOnly = true
            };
            Grid.SetColumn(_soundFileTextBox, 0);
            soundFileGrid.Children.Add(_soundFileTextBox);

            _browseSoundButton = new Button
            {
                Content = "Browse...",
                Width = 90,
                Height = 32,
                Margin = new Thickness(8, 0, 0, 0),
                Background = new SolidColorBrush(Color.FromRgb(91, 141, 239)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            _browseSoundButton.Click += BrowseSoundButton_Click;
            Grid.SetColumn(_browseSoundButton, 1);
            soundFileGrid.Children.Add(_browseSoundButton);

            Grid.SetRow(soundFileGrid, rowIndex);
            mainGrid.Children.Add(soundFileGrid);
            rowIndex += 2;

            // Fade Options
            AddLabel(mainGrid, "Audio Effects:", rowIndex);
            var fadePanel = new StackPanel { Orientation = Orientation.Horizontal };

            _fadeInCheckBox = new CheckBox
            {
                Content = "Fade In",
                IsChecked = FadeIn,
                Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 20, 0)
            };
            fadePanel.Children.Add(_fadeInCheckBox);

            _fadeOutCheckBox = new CheckBox
            {
                Content = "Fade Out",
                IsChecked = FadeOut,
                Foreground = Brushes.White
            };
            fadePanel.Children.Add(_fadeOutCheckBox);

            Grid.SetRow(fadePanel, rowIndex);
            mainGrid.Children.Add(fadePanel);
            rowIndex += 2;

            // Repeat Mode
            AddLabel(mainGrid, "Playback Mode:", rowIndex);
            _repeatModeCombo = new ComboBox
            {
                Background = new SolidColorBrush(Color.FromRgb(32, 32, 32)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(91, 141, 239)),
                Padding = new Thickness(8)
            };
            _repeatModeCombo.Items.Add(new ComboBoxItem { Content = "Play-Stop (once)", Tag = "none" });
            _repeatModeCombo.Items.Add(new ComboBoxItem { Content = "Play-Loop (repeat)", Tag = "loop" });
            _repeatModeCombo.Items.Add(new ComboBoxItem { Content = "Play-Overlay (multiple)", Tag = "overlay" });

            foreach (ComboBoxItem item in _repeatModeCombo.Items)
            {
                if ((string)item.Tag == RepeatMode)
                {
                    _repeatModeCombo.SelectedItem = item;
                    break;
                }
            }

            if (_repeatModeCombo.SelectedItem == null)
            {
                _repeatModeCombo.SelectedIndex = 0;
            }

            Grid.SetRow(_repeatModeCombo, rowIndex);
            mainGrid.Children.Add(_repeatModeCombo);
            rowIndex += 2;

            // Volume Control
            AddLabel(mainGrid, "Volume:", rowIndex);
            var volumePanel = new StackPanel { Orientation = Orientation.Horizontal };

            _volumeSlider = new Slider
            {
                Minimum = 0,
                Maximum = 100,
                Value = ButtonVolume * 100,
                Width = 300,
                TickFrequency = 10,
                IsSnapToTickEnabled = false,
                VerticalAlignment = VerticalAlignment.Center
            };
            _volumeSlider.ValueChanged += VolumeSlider_ValueChanged;
            volumePanel.Children.Add(_volumeSlider);

            _volumeLabel = new TextBlock
            {
                Text = $"{(int)(_volumeSlider.Value)}%",
                Foreground = Brushes.White,
                FontSize = 14,
                Width = 50,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0)
            };
            volumePanel.Children.Add(_volumeLabel);

            Grid.SetRow(volumePanel, rowIndex);
            mainGrid.Children.Add(volumePanel);
            rowIndex += 2; // Skip spacer

            // Bottom Buttons
            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var saveButton = new Button
            {
                Content = "Save",
                Width = 100,
                Height = 36,
                Margin = new Thickness(0, 0, 8, 0),
                Background = new SolidColorBrush(Color.FromRgb(76, 175, 80)),
                Foreground = Brushes.White,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            saveButton.Click += SaveButton_Click;
            buttonPanel.Children.Add(saveButton);

            var cancelButton = new Button
            {
                Content = "Cancel",
                Width = 100,
                Height = 36,
                Background = new SolidColorBrush(Color.FromRgb(80, 80, 80)),
                Foreground = Brushes.White,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            cancelButton.Click += (s, e) => { DialogResult = false; Close(); };
            buttonPanel.Children.Add(cancelButton);

            Grid.SetRow(buttonPanel, rowIndex + 1);
            mainGrid.Children.Add(buttonPanel);

            Content = mainGrid;
        }

        private void AddLabel(Grid grid, string text, int row)
        {
            var label = new TextBlock
            {
                Text = text,
                Foreground = Brushes.White,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 5)
            };
            Grid.SetRow(label, row);
            grid.Children.Add(label);
        }

        private void ColorButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button)
            {
                // Update selected color
                ButtonColor = (string)button.Tag;

                // Update all color buttons' borders
                foreach (Button colorBtn in _colorPanel.Children.OfType<Button>())
                {
                    colorBtn.BorderBrush = (string)colorBtn.Tag == ButtonColor
                        ? Brushes.White
                        : new SolidColorBrush(Color.FromRgb(64, 64, 64));
                }

                // Clear custom color input if predefined color selected
                _customColorTextBox.Text = "#";
            }
        }

        private void CustomColorTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var hexColor = _customColorTextBox.Text;

            // Validate hex color format
            if (System.Text.RegularExpressions.Regex.IsMatch(hexColor, @"^#[0-9A-Fa-f]{6}$"))
            {
                try
                {
                    var brush = (Brush)new BrushConverter().ConvertFromString(hexColor);
                    _customColorPreview.Background = brush;
                    ButtonColor = hexColor.ToUpperInvariant();

                    // Clear predefined color selection
                    foreach (Button colorBtn in _colorPanel.Children.OfType<Button>())
                    {
                        colorBtn.BorderBrush = new SolidColorBrush(Color.FromRgb(64, 64, 64));
                    }
                }
                catch
                {
                    // Invalid color
                    _customColorPreview.Background = new SolidColorBrush(Color.FromRgb(32, 32, 32));
                }
            }
            else
            {
                // Invalid format
                _customColorPreview.Background = new SolidColorBrush(Color.FromRgb(32, 32, 32));
            }
        }

        private bool IsCustomColor(string color)
        {
            return !PredefinedColors.Contains(color?.ToUpperInvariant());
        }

        private void BrowseSoundButton_Click(object sender, RoutedEventArgs e)
        {
            var soundboardFolder = SoundboardConfiguration.GetSoundboardFolder();

            var dialog = new OpenFileDialog
            {
                Title = "Select Sound File",
                Filter = "Audio Files|*.mp3;*.wav;*.m4a;*.aac;*.ogg;*.flac;*.wma|All Files|*.*",
                InitialDirectory = soundboardFolder
            };

            if (dialog.ShowDialog() == true)
            {
                var selectedFile = dialog.FileName;
                var fileName = Path.GetFileName(selectedFile);

                // Check if file is already in soundboard folder
                var destPath = Path.Combine(soundboardFolder, fileName);
                
                if (!File.Exists(destPath))
                {
                    // Copy file to soundboard folder
                    try
                    {
                        File.Copy(selectedFile, destPath, false);
                        System.Diagnostics.Debug.WriteLine($"✓ Copied new sound file to soundboard folder");
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error copying file:\n{ex.Message}", "Error",
                            MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }
                }

                SoundFilePath = destPath;
                _soundFileTextBox.Text = fileName;
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            // Validate inputs
            if (string.IsNullOrWhiteSpace(_nameTextBox.Text))
            {
                MessageBox.Show("Please enter a button name.", "Validation Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(SoundFilePath))
            {
                MessageBox.Show("Please select a sound file.", "Validation Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Save values
            ButtonName = _nameTextBox.Text;
            ButtonIcon = _iconTextBox.Text;
            FadeIn = _fadeInCheckBox.IsChecked ?? false;
            FadeOut = _fadeOutCheckBox.IsChecked ?? false;
            ButtonVolume = (float)(_volumeSlider.Value / 100.0);

            if (_repeatModeCombo.SelectedItem is ComboBoxItem selectedItem)
            {
                RepeatMode = (string)selectedItem.Tag;
            }

            DialogResult = true;
            Close();
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_volumeLabel != null)
            {
                _volumeLabel.Text = $"{(int)e.NewValue}%";
                ButtonVolume = (float)(e.NewValue / 100.0);
            }
        }
    }
}
