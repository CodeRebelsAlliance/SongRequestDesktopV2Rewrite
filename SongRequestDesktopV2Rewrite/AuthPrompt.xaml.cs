using System;
using System.Diagnostics;
using System.Windows;

namespace SongRequestDesktopV2Rewrite
{
    public partial class AuthPrompt : Window
    {
        public AuthPrompt()
        {
            InitializeComponent();
        }

        private void AuthenticateButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var address = ConfigService.Instance.Current?.Address ?? "http://127.0.0.1:5000";
                var url = $"https://redstefan.software/schuelerapp/sign-jwt?audience={Uri.EscapeDataString(address)}";
                var psi = new ProcessStartInfo { FileName = url, UseShellExecute = true };
                Process.Start(psi);
                
                // Close the dialog after opening the browser
                this.DialogResult = true;
                this.Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to open authentication page:\n{ex.Message}", 
                    "Error", 
                    MessageBoxButton.OK, 
                    MessageBoxImage.Error);
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = false;
            this.Close();
        }

        private void ApplyAuthButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Reload configuration to get the new authentication token
                ConfigService.Instance.Reload();

                // Close the dialog with success result
                this.DialogResult = true;
                this.Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to reload configuration:\n{ex.Message}", 
                    "Error", 
                    MessageBoxButton.OK, 
                    MessageBoxImage.Error);
            }
        }
    }
}
