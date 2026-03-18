using System;
using System.IO;
using Newtonsoft.Json;

namespace SongRequestDesktopV2Rewrite
{
    public enum StartupMode
    {
        SongRequests,
        MusicPlayer,
        MusicShare,
        Soundboard
    }

    public class LaunchOptionsState
    {
        public bool RememberSelection { get; set; }
        public StartupMode LastSelectedMode { get; set; }

        public LaunchOptionsState()
        {
            RememberSelection = false;
            LastSelectedMode = StartupMode.SongRequests;
        }
    }

    public static class LaunchOptionsStorage
    {
        private static readonly string FilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "launch_options.json");

        public static LaunchOptionsState Load()
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    return new LaunchOptionsState();
                }

                var json = File.ReadAllText(FilePath);
                var state = JsonConvert.DeserializeObject<LaunchOptionsState>(json);
                return state ?? new LaunchOptionsState();
            }
            catch
            {
                return new LaunchOptionsState();
            }
        }

        public static void Save(LaunchOptionsState state)
        {
            try
            {
                var json = JsonConvert.SerializeObject(state, Formatting.Indented);
                File.WriteAllText(FilePath, json);
            }
            catch
            {
                // Ignore save errors to avoid blocking app startup.
            }
        }
    }
}
