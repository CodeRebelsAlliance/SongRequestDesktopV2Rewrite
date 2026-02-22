using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Forms;

namespace SongRequestDesktopV2Rewrite
{
    /// <summary>
    /// Manages application-level operations like force closing, restarting, tray icon, etc.
    /// </summary>
    public class AppManager : IDisposable
    {
        private NotifyIcon? _trayIcon;
        
        private YoutubeForm? _ytform;
        private MusicPlayer? _musicPlayer;

        public AppManager(YoutubeForm _ytform, MusicPlayer _musicPlayer)
        {
            this._ytform = _ytform;
            this._musicPlayer = _musicPlayer;
            InitializeTrayIcon();
        }
        
        private void InitializeTrayIcon()
        {
            try
            {
                // Create the tray icon
                _trayIcon = new NotifyIcon();

                // Try multiple methods to load the icon
                Icon? appIcon = null;

                // Method 1: Extract from application resources (WPF pack URI)
                try
                {
                    var iconUri = new Uri("pack://application:,,,/SRshortLogo.ico");
                    var streamInfo = System.Windows.Application.GetResourceStream(iconUri);
                    if (streamInfo != null)
                    {
                        using (var stream = streamInfo.Stream)
                        {
                            appIcon = new Icon(stream);
                            Debug.WriteLine("✓ Loaded tray icon from application resources");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Method 1 failed: {ex.Message}");
                }

                // Method 2: Extract from executable icon
                if (appIcon == null)
                {
                    try
                    {
                        var exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
                        if (!string.IsNullOrEmpty(exePath))
                        {
                            appIcon = Icon.ExtractAssociatedIcon(exePath);
                            Debug.WriteLine("✓ Loaded tray icon from executable");
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Method 2 failed: {ex.Message}");
                    }
                }

                // Method 3: Load from file in base directory (fallback)
                if (appIcon == null)
                {
                    try
                    {
                        string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SRshortLogo.ico");
                        if (File.Exists(iconPath))
                        {
                            appIcon = new Icon(iconPath);
                            Debug.WriteLine("✓ Loaded tray icon from file");
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Method 3 failed: {ex.Message}");
                    }
                }

                // Set icon or use system default
                _trayIcon.Icon = appIcon ?? SystemIcons.Application;

                _trayIcon.Text = "SongRequest V2";
                _trayIcon.Visible = true;
                
                // Create context menu
                var contextMenu = new ContextMenuStrip();
                
                // Show/Hide window option
                var showItem = new ToolStripMenuItem("Show Main Window");
                showItem.Click += (s, e) => ShowMainWindow(false);
                contextMenu.Items.Add(showItem);

                var showItem2 = new ToolStripMenuItem("Show Music Player");
                showItem2.Click += (s, e) => ShowMainWindow(true);
                contextMenu.Items.Add(showItem2);

                contextMenu.Items.Add(new ToolStripSeparator());
                
                // Exit option
                var exitItem = new ToolStripMenuItem("Exit SongRequest");
                exitItem.Click += (s, e) => ShutdownWithTimeout(3000);
                contextMenu.Items.Add(exitItem);
                
                _trayIcon.ContextMenuStrip = contextMenu;
                
                // Double-click to show main window
                _trayIcon.DoubleClick += (s, e) => ShowMainWindow(false);
                
                Debug.WriteLine("✓ System tray icon initialized");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to initialize tray icon: {ex.Message}");
            }
        }
        
        private void ShowMainWindow(bool mp)
        {
            try
            {
                if (mp)
                {
                    var mainWindow = _musicPlayer;
                    if (mainWindow != null)
                    {
                        if (mainWindow.WindowState == WindowState.Minimized)
                        {
                            mainWindow.WindowState = WindowState.Normal;
                        }

                        mainWindow.Show();
                        mainWindow.Activate();
                        mainWindow.Focus();
                    }
                }
                else
                {
                    var mainWindow = _ytform;
                    if (mainWindow != null)
                    {
                        if (mainWindow.WindowState == WindowState.Minimized)
                        {
                            mainWindow.WindowState = WindowState.Normal;
                        }

                        mainWindow.Show();
                        mainWindow.Activate();
                        mainWindow.Focus();
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to show main window: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Show a balloon notification in the system tray
        /// </summary>
        public void ShowNotification(string title, string message, ToolTipIcon icon = ToolTipIcon.Info)
        {
            try
            {
                _trayIcon?.ShowBalloonTip(3000, title, message, icon);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to show notification: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Force closes the application immediately, bypassing all window closing events and cancellation logic.
        /// This will terminate the process without giving any window a chance to cancel.
        /// </summary>
        /// <param name="exitCode">The exit code to return to the operating system (default: 0)</param>
        public void ForceClose(int exitCode = 0)
        {
            try
            {
                Debug.WriteLine($"AppManager: Force closing application with exit code {exitCode}");
                
                // Immediately terminate the process - no cancellation possible
                Environment.Exit(exitCode);
            }
            catch (Exception ex)
            {
                // As a last resort, if Environment.Exit fails (extremely rare)
                Debug.WriteLine($"AppManager: Environment.Exit failed: {ex.Message}");
                Process.GetCurrentProcess().Kill();
            }
        }

        /// <summary>
        /// Gracefully shuts down the application, allowing windows to handle their closing events.
        /// This CAN be cancelled by windows setting e.Cancel = true.
        /// </summary>
        public void GracefulShutdown()
        {
            try
            {
                Debug.WriteLine("AppManager: Initiating graceful shutdown");
                System.Windows.Application.Current?.Shutdown();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"AppManager: Graceful shutdown failed: {ex.Message}, forcing exit");
                ForceClose(1);
            }
        }

        /// <summary>
        /// Restarts the application by starting a new instance and force closing the current one.
        /// </summary>
        public void Restart()
        {
            try
            {
                Debug.WriteLine("AppManager: Restarting application");
                
                // Get the executable path
                var exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
                
                if (string.IsNullOrEmpty(exePath))
                {
                    Debug.WriteLine("AppManager: Could not determine executable path for restart");
                    return;
                }

                // Start a new instance
                Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = true,
                    WorkingDirectory = Environment.CurrentDirectory
                });

                // Force close this instance
                ForceClose(0);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"AppManager: Restart failed: {ex.Message}");
                System.Windows.MessageBox.Show(
                    $"Failed to restart application: {ex.Message}",
                    "Restart Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Attempts graceful shutdown first, then force closes if shutdown takes too long.
        /// </summary>
        /// <param name="timeoutMs">Timeout in milliseconds before force closing (default: 3000ms)</param>
        public async void ShutdownWithTimeout(int timeoutMs = 3000)
        {
            try
            {
                Debug.WriteLine($"AppManager: Attempting graceful shutdown with {timeoutMs}ms timeout");
                
                var shutdownTask = System.Threading.Tasks.Task.Run(() =>
                {
                    System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                    {
                        System.Windows.Application.Current?.Shutdown();
                    });
                });

                // Wait for graceful shutdown with timeout
                var completed = await System.Threading.Tasks.Task.WhenAny(
                    shutdownTask,
                    System.Threading.Tasks.Task.Delay(timeoutMs)
                );

                // If timeout occurred, force close
                if (completed != shutdownTask)
                {
                    Debug.WriteLine("AppManager: Graceful shutdown timed out, forcing exit");
                    ForceClose(0);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"AppManager: ShutdownWithTimeout failed: {ex.Message}, forcing exit");
                ForceClose(1);
            }
        }

        /// <summary>
        /// Checks if the application is currently running elevated (as administrator).
        /// </summary>
        public bool IsRunningElevated()
        {
            try
            {
                var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Gets information about the current application instance.
        /// </summary>
        public string GetAppInfo()
        {
            try
            {
                var process = Process.GetCurrentProcess();
                return $"Process: {process.ProcessName} (PID: {process.Id})\n" +
                       $"Path: {Environment.ProcessPath}\n" +
                       $"Working Dir: {Environment.CurrentDirectory}\n" +
                       $"Elevated: {IsRunningElevated()}\n" +
                       $".NET Version: {Environment.Version}";
            }
            catch (Exception ex)
            {
                return $"Error getting app info: {ex.Message}";
            }
        }
        
        /// <summary>
        /// Dispose of the tray icon resources
        /// </summary>
        public void Dispose()
        {
            try
            {
                if (_trayIcon != null)
                {
                    _trayIcon.Visible = false;
                    _trayIcon.Dispose();
                    _trayIcon = null;
                }
                
                Debug.WriteLine("✓ Tray icon disposed");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error disposing tray icon: {ex.Message}");
            }
        }
    }
}
