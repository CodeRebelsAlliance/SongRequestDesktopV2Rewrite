using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;

namespace SongRequestDesktopV2Rewrite
{
    public class Config : INotifyPropertyChanged
    {
        private int _fetchingTimer = 30;
        private int _threads = 5;
        private string _bearerToken = "Bearer Multicore2024SR";
        private string _address = "http://127.0.0.1:5000";
        private string _defaultSorting = "none";

        public event PropertyChangedEventHandler? PropertyChanged;

        public int FetchingTimer
        {
            get => _fetchingTimer;
            set => SetField(ref _fetchingTimer, value);
        }

        public int Threads
        {
            get => _threads;
            set => SetField(ref _threads, value);
        }

        public string BearerToken
        {
            get => _bearerToken;
            set => SetField(ref _bearerToken, value);
        }

        public string Address
        {
            get => _address;
            set => SetField(ref _address, value);
        }

        public string DefaultSorting
        {
            get => _defaultSorting;
            set => SetField(ref _defaultSorting, value);
        }

        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value!;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            return true;
        }
    }

    public sealed class ConfigService
    {
        private static readonly object _sync = new object();
        private readonly string _configFilePath;

        public Config Current { get; private set; }

        // Singleton instance for easy access
        public static ConfigService Instance { get; } = new ConfigService();

        private ConfigService()
        {
            _configFilePath = GetDefaultConfigPath();
            Current = LoadOrCreateConfig();

            // Auto-save on any property change
            if (Current != null)
            {
                Current.PropertyChanged += (s, e) =>
                {
                    try
                    {
                        SaveConfig();
                    }
                    catch
                    {
                        // swallowing exceptions here to avoid UI disruption; callers can explicitly call SaveConfig
                    }
                };
            }
        }

        private static string GetDefaultConfigPath()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var appFolder = Path.Combine(appData, "SongRequestDesktopV2Rewrite");
            if (!Directory.Exists(appFolder)) Directory.CreateDirectory(appFolder);
            return Path.Combine(appFolder, "config.json");
        }

        private Config LoadOrCreateConfig()
        {
            lock (_sync)
            {
                try
                {
                    if (!File.Exists(_configFilePath))
                    {
                        var def = new Config();
                        File.WriteAllText(_configFilePath, JsonConvert.SerializeObject(def, Formatting.Indented));
                        return def;
                    }

                    var json = File.ReadAllText(_configFilePath);
                    if (string.IsNullOrWhiteSpace(json))
                    {
                        var def = new Config();
                        File.WriteAllText(_configFilePath, JsonConvert.SerializeObject(def, Formatting.Indented));
                        return def;
                    }

                    var cfg = JsonConvert.DeserializeObject<Config>(json);
                    if (cfg == null)
                    {
                        cfg = new Config();
                    }

                    // Validate/patch missing values with defaults
                    var patched = false;
                    if (cfg.FetchingTimer <= 0) { cfg.FetchingTimer = 30; patched = true; }
                    if (cfg.Threads <= 0) { cfg.Threads = 5; patched = true; }
                    if (string.IsNullOrWhiteSpace(cfg.BearerToken)) { cfg.BearerToken = "DefaultToken"; patched = true; }
                    if (string.IsNullOrWhiteSpace(cfg.Address)) { cfg.Address = "http://127.0.0.1:5000"; patched = true; }
                    if (string.IsNullOrWhiteSpace(cfg.DefaultSorting)) { cfg.DefaultSorting = "none"; patched = true; }

                    if (patched)
                    {
                        File.WriteAllText(_configFilePath, JsonConvert.SerializeObject(cfg, Formatting.Indented));
                    }

                    return cfg;
                }
                catch
                {
                    // If any problem reading config, create a default one
                    try
                    {
                        var def = new Config();
                        File.WriteAllText(_configFilePath, JsonConvert.SerializeObject(def, Formatting.Indented));
                        return def;
                    }
                    catch
                    {
                        // As a last resort, return in-memory defaults
                        return new Config();
                    }
                }
            }
        }

        public void SaveConfig()
        {
            lock (_sync)
            {
                var json = JsonConvert.SerializeObject(Current, Formatting.Indented);
                File.WriteAllText(_configFilePath, json);
            }
        }

        public void Reload()
        {
            lock (_sync)
            {
                Current = LoadOrCreateConfig();
            }
        }

        /// <summary>
        /// Helper to update the config in a thread-safe manner and persist immediately.
        /// </summary>
        public void Update(Action<Config> updater)
        {
            if (updater == null) return;
            lock (_sync)
            {
                updater(Current);
                SaveConfig();
            }
        }
    }
}
