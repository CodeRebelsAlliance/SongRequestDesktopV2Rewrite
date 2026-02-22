using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SongRequestDesktopV2Rewrite
{
    /// <summary>
    /// Dialog for managing soundboard pages (add, delete, move, rename, jump)
    /// </summary>
    public partial class PageManagementDialog : Window
    {
        private SoundboardConfiguration _config;
        private ListBox _pageListBox;

        public PageManagementDialog(SoundboardConfiguration config)
        {
            _config = config;

            Title = "Page Management";
            Width = 500;
            Height = 500;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            Background = new SolidColorBrush(Color.FromRgb(18, 18, 18));

            BuildUI();
        }

        private void BuildUI()
        {
            var mainGrid = new Grid
            {
                Margin = new Thickness(20)
            };

            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(20) });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(20) });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });

            // Title
            var title = new TextBlock
            {
                Text = "Manage Soundboard Pages",
                FontSize = 20,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White
            };
            Grid.SetRow(title, 0);
            mainGrid.Children.Add(title);

            // Page list with buttons
            var listGrid = new Grid();
            listGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            listGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            listGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });

            // ListBox showing pages
            _pageListBox = new ListBox
            {
                Background = new SolidColorBrush(Color.FromRgb(32, 32, 32)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(64, 64, 64)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8)
            };

            RefreshPageList();

            Grid.SetColumn(_pageListBox, 0);
            listGrid.Children.Add(_pageListBox);

            // Action buttons
            var actionStack = new StackPanel
            {
                Orientation = Orientation.Vertical,
                VerticalAlignment = VerticalAlignment.Top
            };

            var addPageButton = CreateActionButton("➕ Add Page", AddPage_Click);
            var deletePageButton = CreateActionButton("🗑️ Delete", DeletePage_Click);
            var renamePageButton = CreateActionButton("✏️ Rename", RenamePage_Click);
            var moveUpButton = CreateActionButton("⬆️ Move Up", MovePageUp_Click);
            var moveDownButton = CreateActionButton("⬇️ Move Down", MovePageDown_Click);
            var jumpToButton = CreateActionButton("🎯 Jump To", JumpToPage_Click);

            actionStack.Children.Add(addPageButton);
            actionStack.Children.Add(new Separator { Height = 10, Opacity = 0 });
            actionStack.Children.Add(deletePageButton);
            actionStack.Children.Add(renamePageButton);
            actionStack.Children.Add(new Separator { Height = 10, Opacity = 0 });
            actionStack.Children.Add(moveUpButton);
            actionStack.Children.Add(moveDownButton);
            actionStack.Children.Add(new Separator { Height = 10, Opacity = 0 });
            actionStack.Children.Add(jumpToButton);

            Grid.SetColumn(actionStack, 2);
            listGrid.Children.Add(actionStack);

            Grid.SetRow(listGrid, 2);
            mainGrid.Children.Add(listGrid);

            // Bottom buttons
            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var closeButton = new Button
            {
                Content = "Close",
                Width = 80,
                Height = 32,
                Margin = new Thickness(8, 0, 0, 0)
            };
            closeButton.Click += (s, e) => { DialogResult = true; Close(); };
            buttonPanel.Children.Add(closeButton);

            Grid.SetRow(buttonPanel, 4);
            mainGrid.Children.Add(buttonPanel);

            Content = mainGrid;
        }

        private Button CreateActionButton(string content, RoutedEventHandler handler)
        {
            var button = new Button
            {
                Content = content,
                Width = 110,
                Height = 32,
                Margin = new Thickness(0, 2, 0, 2),
                Background = new SolidColorBrush(Color.FromRgb(91, 141, 239)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            button.Click += handler;
            return button;
        }

        private void RefreshPageList()
        {
            _pageListBox.Items.Clear();

            for (int i = 0; i < _config.Pages.Count; i++)
            {
                var page = _config.Pages[i];
                var item = new ListBoxItem
                {
                    Content = $"{i + 1}. {page.Name} ({page.Columns}×{page.Rows})",
                    Tag = i,
                    Foreground = Brushes.White,
                    Background = i == _config.CurrentPageIndex 
                        ? new SolidColorBrush(Color.FromRgb(91, 141, 239)) 
                        : Brushes.Transparent
                };
                _pageListBox.Items.Add(item);
            }

            if (_config.CurrentPageIndex >= 0 && _config.CurrentPageIndex < _pageListBox.Items.Count)
            {
                _pageListBox.SelectedIndex = _config.CurrentPageIndex;
            }
        }

        private void AddPage_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new AddPageDialog
            {
                Owner = this
            };

            if (dialog.ShowDialog() == true)
            {
                _config.AddPage(dialog.PageName, dialog.PageColumns, dialog.PageRows);
                RefreshPageList();
            }
        }

        private void DeletePage_Click(object sender, RoutedEventArgs e)
        {
            if (_pageListBox.SelectedItem is ListBoxItem item && item.Tag is int index)
            {
                if (_config.Pages.Count <= 1)
                {
                    MessageBox.Show("Cannot delete the last page!", "Error", 
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var page = _config.Pages[index];
                var result = MessageBox.Show(
                    $"Are you sure you want to delete page '{page.Name}'?",
                    "Delete Page",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    _config.DeletePage(index);
                    RefreshPageList();
                }
            }
        }

        private void RenamePage_Click(object sender, RoutedEventArgs e)
        {
            if (_pageListBox.SelectedItem is ListBoxItem item && item.Tag is int index)
            {
                var page = _config.Pages[index];
                var dialog = new InputDialog("Rename Page", "Enter new name:", page.Name)
                {
                    Owner = this
                };

                if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.InputText))
                {
                    page.Name = dialog.InputText;
                    _config.Save();
                    RefreshPageList();
                }
            }
        }

        private void MovePageUp_Click(object sender, RoutedEventArgs e)
        {
            if (_pageListBox.SelectedItem is ListBoxItem item && item.Tag is int index)
            {
                if (index > 0)
                {
                    _config.MovePage(index, index - 1);
                    RefreshPageList();
                }
            }
        }

        private void MovePageDown_Click(object sender, RoutedEventArgs e)
        {
            if (_pageListBox.SelectedItem is ListBoxItem item && item.Tag is int index)
            {
                if (index < _config.Pages.Count - 1)
                {
                    _config.MovePage(index, index + 1);
                    RefreshPageList();
                }
            }
        }

        private void JumpToPage_Click(object sender, RoutedEventArgs e)
        {
            if (_pageListBox.SelectedItem is ListBoxItem item && item.Tag is int index)
            {
                _config.CurrentPageIndex = index;
                _config.Save();
                RefreshPageList();
                MessageBox.Show($"Jumped to page '{_config.Pages[index].Name}'", 
                    "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
    }

    /// <summary>
    /// Dialog for adding a new page
    /// </summary>
    public class AddPageDialog : Window
    {
        private TextBox _nameTextBox;
        private ComboBox _columnsCombo;
        private ComboBox _rowsCombo;

        public string PageName { get; private set; }
        public int PageColumns { get; private set; }
        public int PageRows { get; private set; }

        public AddPageDialog()
        {
            Title = "Add New Page";
            Width = 400;
            Height = 280;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            Background = new SolidColorBrush(Color.FromRgb(18, 18, 18));

            PageName = $"Page {DateTime.Now:HHmmss}";
            PageColumns = 4;
            PageRows = 3;

            BuildUI();
        }

        private void BuildUI()
        {
            var grid = new Grid { Margin = new Thickness(20) };
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(15) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(15) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(15) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });

            // Page Name
            var nameLabel = new TextBlock { Text = "Page Name:", Foreground = Brushes.White, FontSize = 14 };
            Grid.SetRow(nameLabel, 0);
            grid.Children.Add(nameLabel);

            _nameTextBox = new TextBox
            {
                Text = PageName,
                Background = new SolidColorBrush(Color.FromRgb(32, 32, 32)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(91, 141, 239)),
                BorderThickness = new Thickness(2),
                Padding = new Thickness(8),
                FontSize = 14
            };
            Grid.SetRow(_nameTextBox, 2);
            grid.Children.Add(_nameTextBox);

            // Columns
            var columnsPanel = new StackPanel { Orientation = Orientation.Horizontal };
            columnsPanel.Children.Add(new TextBlock
            {
                Text = "Columns:",
                Width = 80,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 14
            });

            _columnsCombo = new ComboBox
            {
                Width = 100,
                Background = new SolidColorBrush(Color.FromRgb(32, 32, 32)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(91, 141, 239))
            };
            for (int i = 1; i <= 12; i++)
            {
                _columnsCombo.Items.Add(i);
            }
            _columnsCombo.SelectedItem = PageColumns;
            columnsPanel.Children.Add(_columnsCombo);

            Grid.SetRow(columnsPanel, 4);
            grid.Children.Add(columnsPanel);

            // Rows
            var rowsPanel = new StackPanel { Orientation = Orientation.Horizontal };
            rowsPanel.Children.Add(new TextBlock
            {
                Text = "Rows:",
                Width = 80,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 14
            });

            _rowsCombo = new ComboBox
            {
                Width = 100,
                Background = new SolidColorBrush(Color.FromRgb(32, 32, 32)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(91, 141, 239))
            };
            for (int i = 1; i <= 12; i++)
            {
                _rowsCombo.Items.Add(i);
            }
            _rowsCombo.SelectedItem = PageRows;
            rowsPanel.Children.Add(_rowsCombo);

            Grid.SetRow(rowsPanel, 6);
            grid.Children.Add(rowsPanel);

            // Buttons
            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var okButton = new Button
            {
                Content = "Add Page",
                Width = 100,
                Height = 32,
                Margin = new Thickness(0, 0, 8, 0),
                Background = new SolidColorBrush(Color.FromRgb(76, 175, 80)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            okButton.Click += OkButton_Click;
            buttonPanel.Children.Add(okButton);

            var cancelButton = new Button
            {
                Content = "Cancel",
                Width = 80,
                Height = 32,
                Background = new SolidColorBrush(Color.FromRgb(80, 80, 80)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            cancelButton.Click += (s, e) => { DialogResult = false; Close(); };
            buttonPanel.Children.Add(cancelButton);

            Grid.SetRow(buttonPanel, 8);
            grid.Children.Add(buttonPanel);

            Content = grid;
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_nameTextBox.Text))
            {
                MessageBox.Show("Please enter a page name.", "Validation Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_columnsCombo.SelectedItem != null && _rowsCombo.SelectedItem != null)
            {
                PageName = _nameTextBox.Text;
                PageColumns = (int)_columnsCombo.SelectedItem;
                PageRows = (int)_rowsCombo.SelectedItem;
                DialogResult = true;
                Close();
            }
        }
    }

    /// <summary>
    /// Simple input dialog for text input
    /// </summary>
    public class InputDialog : Window
    {
        private TextBox _inputTextBox;

        public string InputText { get; private set; }

        public InputDialog(string title, string prompt, string defaultValue = "")
        {
            Title = title;
            Width = 400;
            Height = 200;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            Background = new SolidColorBrush(Color.FromRgb(18, 18, 18));

            InputText = defaultValue;

            var grid = new Grid { Margin = new Thickness(20) };
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(15) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });

            var promptLabel = new TextBlock
            {
                Text = prompt,
                Foreground = Brushes.White,
                FontSize = 14
            };
            Grid.SetRow(promptLabel, 0);
            grid.Children.Add(promptLabel);

            _inputTextBox = new TextBox
            {
                Text = defaultValue,
                Background = new SolidColorBrush(Color.FromRgb(32, 32, 32)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(91, 141, 239)),
                BorderThickness = new Thickness(2),
                Padding = new Thickness(8),
                FontSize = 14
            };
            _inputTextBox.SelectAll();
            Grid.SetRow(_inputTextBox, 2);
            grid.Children.Add(_inputTextBox);

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
                Margin = new Thickness(0, 0, 8, 0),
                Background = new SolidColorBrush(Color.FromRgb(76, 175, 80)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0)
            };
            okButton.Click += (s, e) =>
            {
                InputText = _inputTextBox.Text;
                DialogResult = true;
                Close();
            };
            buttonPanel.Children.Add(okButton);

            var cancelButton = new Button
            {
                Content = "Cancel",
                Width = 80,
                Height = 32,
                Background = new SolidColorBrush(Color.FromRgb(80, 80, 80)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0)
            };
            cancelButton.Click += (s, e) => { DialogResult = false; Close(); };
            buttonPanel.Children.Add(cancelButton);

            Grid.SetRow(buttonPanel, 4);
            grid.Children.Add(buttonPanel);

            Content = grid;

            Loaded += (s, e) => _inputTextBox.Focus();
        }
    }
}
