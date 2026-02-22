using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace SongRequestDesktopV2Rewrite
{
    /// <summary>
    /// Represents a single page in the soundboard
    /// </summary>
    public class SoundboardPage
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public int Columns { get; set; }
        public int Rows { get; set; }

        /// <summary>
        /// 12x12 array of buttons (144 slots total, indexed as [row * 12 + col])
        /// </summary>
        public SoundboardButton[] Buttons { get; set; }

        public SoundboardPage()
        {
            Id = Guid.NewGuid().ToString();
            Name = "Untitled Page";
            Columns = 4;
            Rows = 3;
            Buttons = new SoundboardButton[144]; // 12x12 = 144

            // Initialize all slots with empty buttons
            for (int i = 0; i < 144; i++)
            {
                Buttons[i] = new SoundboardButton { Index = i };
            }
        }

        public SoundboardPage(string name, int columns, int rows)
        {
            Id = Guid.NewGuid().ToString();
            Name = name;
            Columns = columns;
            Rows = rows;
            Buttons = new SoundboardButton[144]; // 12x12 = 144

            // Initialize all slots with empty buttons
            for (int i = 0; i < 144; i++)
            {
                Buttons[i] = new SoundboardButton { Index = i };
            }
        }

        public int TotalSlots => Columns * Rows;

        /// <summary>
        /// Get button at grid position
        /// </summary>
        public SoundboardButton GetButton(int row, int col)
        {
            int index = row * 12 + col;
            if (index >= 0 && index < 144)
            {
                return Buttons[index];
            }
            return new SoundboardButton { Index = index };
        }

        /// <summary>
        /// Set button at grid position
        /// </summary>
        public void SetButton(int row, int col, SoundboardButton button)
        {
            int index = row * 12 + col;
            if (index >= 0 && index < 144)
            {
                button.Index = index;
                Buttons[index] = button;
            }
        }
    }

    /// <summary>
    /// Represents a button configuration with all sound properties
    /// </summary>
    public class SoundboardButton
    {
        public int Index { get; set; }
        public string Name { get; set; }
        public string SoundFile { get; set; } // Filename only (stored in soundboard folder)
        public string Icon { get; set; } // Optional emoji or icon name
        public string Color { get; set; } // Hex color code (e.g., "#FF5733")
        public double Length { get; set; } // Duration in seconds
        public bool FadeIn { get; set; }
        public bool FadeOut { get; set; }
        public string RepeatMode { get; set; } // "none", "loop", "repeat-n"
        public bool IsEnabled { get; set; }
        public float Volume { get; set; } // 0.0 to 1.0

        public SoundboardButton()
        {
            Index = 0;
            Name = "Empty";
            SoundFile = string.Empty;
            Icon = "🔊"; // Default speaker icon
            Color = "#2A2A2A"; // Default dark gray
            Length = 0;
            FadeIn = false;
            FadeOut = false;
            RepeatMode = "none";
            IsEnabled = false;
            Volume = 1.0f; // Default 100%
        }

        public bool IsEmpty => string.IsNullOrWhiteSpace(SoundFile);
    }

    /// <summary>
    /// Manages soundboard pages and persistence
    /// </summary>
    public class SoundboardConfiguration
    {
        public List<SoundboardPage> Pages { get; set; }
        public int CurrentPageIndex { get; set; }
        public float MasterVolume { get; set; } // 0.0 to 1.0
        public int OutputDeviceNumber { get; set; } // -1 for default device

        private const string SoundboardFolder = "soundboard";
        private const string ConfigFileName = "soundboard_config.json";
        private static readonly string DataFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
        private static readonly string SoundboardDataFolder = Path.Combine(DataFolder, SoundboardFolder);
        private static readonly string ConfigFilePath = Path.Combine(DataFolder, ConfigFileName);

        public SoundboardConfiguration()
        {
            Pages = new List<SoundboardPage>();
            CurrentPageIndex = 0;
            MasterVolume = 1.0f; // Default 100%
            OutputDeviceNumber = -1; // Default device
        }

        /// <summary>
        /// Ensure data folders exist
        /// </summary>
        private static void EnsureFoldersExist()
        {
            try
            {
                if (!Directory.Exists(DataFolder))
                {
                    Directory.CreateDirectory(DataFolder);
                    System.Diagnostics.Debug.WriteLine($"✓ Created data folder: {DataFolder}");
                }

                if (!Directory.Exists(SoundboardDataFolder))
                {
                    Directory.CreateDirectory(SoundboardDataFolder);
                    System.Diagnostics.Debug.WriteLine($"✓ Created soundboard folder: {SoundboardDataFolder}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to create folders: {ex.Message}");
            }
        }

        /// <summary>
        /// Load configuration from file
        /// </summary>
        public static SoundboardConfiguration Load()
        {
            EnsureFoldersExist();

            try
            {
                if (File.Exists(ConfigFilePath))
                {
                    var json = File.ReadAllText(ConfigFilePath);
                    var config = JsonConvert.DeserializeObject<SoundboardConfiguration>(json);

                    if (config != null && config.Pages.Count > 0)
                    {
                        // Validate and fix any missing button arrays
                        foreach (var page in config.Pages)
                        {
                            if (page.Buttons == null || page.Buttons.Length != 144)
                            {
                                page.Buttons = new SoundboardButton[144];
                                for (int i = 0; i < 144; i++)
                                {
                                    page.Buttons[i] = new SoundboardButton { Index = i };
                                }
                            }
                        }

                        System.Diagnostics.Debug.WriteLine($"✓ Loaded soundboard config: {config.Pages.Count} pages");
                        return config;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load soundboard config: {ex.Message}");
            }

            // Return default configuration
            var defaultConfig = new SoundboardConfiguration();
            defaultConfig.Pages.Add(new SoundboardPage("Page 1", 4, 3));
            defaultConfig.Save();
            return defaultConfig;
        }

        /// <summary>
        /// Save configuration to file
        /// </summary>
        public void Save()
        {
            EnsureFoldersExist();

            try
            {
                var json = JsonConvert.SerializeObject(this, Formatting.Indented);
                File.WriteAllText(ConfigFilePath, json);
                System.Diagnostics.Debug.WriteLine($"✓ Soundboard configuration saved to {ConfigFilePath}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save soundboard config: {ex.Message}");
            }
        }

        /// <summary>
        /// Get current page
        /// </summary>
        public SoundboardPage GetCurrentPage()
        {
            if (Pages.Count == 0)
            {
                Pages.Add(new SoundboardPage("Page 1", 4, 3));
            }

            if (CurrentPageIndex < 0 || CurrentPageIndex >= Pages.Count)
            {
                CurrentPageIndex = 0;
            }

            return Pages[CurrentPageIndex];
        }

        /// <summary>
        /// Add a new page
        /// </summary>
        public SoundboardPage AddPage(string name, int columns, int rows)
        {
            var page = new SoundboardPage(name, columns, rows);
            Pages.Add(page);
            Save();
            return page;
        }

        /// <summary>
        /// Delete a page
        /// </summary>
        public bool DeletePage(int index)
        {
            if (Pages.Count <= 1)
            {
                return false; // Can't delete last page
            }

            if (index >= 0 && index < Pages.Count)
            {
                Pages.RemoveAt(index);

                // Adjust current page index
                if (CurrentPageIndex >= Pages.Count)
                {
                    CurrentPageIndex = Pages.Count - 1;
                }

                Save();
                return true;
            }

            return false;
        }

        /// <summary>
        /// Move page to new position
        /// </summary>
        public bool MovePage(int fromIndex, int toIndex)
        {
            if (fromIndex < 0 || fromIndex >= Pages.Count ||
                toIndex < 0 || toIndex >= Pages.Count)
            {
                return false;
            }

            var page = Pages[fromIndex];
            Pages.RemoveAt(fromIndex);
            Pages.Insert(toIndex, page);

            // Update current page index if affected
            if (CurrentPageIndex == fromIndex)
            {
                CurrentPageIndex = toIndex;
            }
            else if (fromIndex < CurrentPageIndex && toIndex >= CurrentPageIndex)
            {
                CurrentPageIndex--;
            }
            else if (fromIndex > CurrentPageIndex && toIndex <= CurrentPageIndex)
            {
                CurrentPageIndex++;
            }

            Save();
            return true;
        }

        /// <summary>
        /// Get the soundboard folder path for audio files
        /// </summary>
        public static string GetSoundboardFolder()
        {
            EnsureFoldersExist();
            return SoundboardDataFolder;
        }
    }
}
