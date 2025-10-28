using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Project_NNTP_Niklas
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {
        }

        // Wire this method to your button's Click in XAML (Click="BtnLogin_Click")
        private async void BtnLogin_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button != null) button.IsEnabled = false;

            try
            {
                // Read username/password from the named controls in XAML
                string username = txtUsername?.Text?.Trim() ?? string.Empty;
                string password = txtPassword?.Password ?? string.Empty;

                if (string.IsNullOrEmpty(username))
                {
                    MessageBox.Show("Please enter a username.", "Input required", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (string.IsNullOrEmpty(password))
                {
                    MessageBox.Show("Please enter a password.", "Input required", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Save credentials in Application.Properties for later reuse (in-memory only)
                Application.Current.Properties["NntpUsername"] = username;
                Application.Current.Properties["NntpPassword"] = password;

                var host = "news.sunsite.dk";
                var port = 119;

                var connector = new ConnectionAndAuthentication();
                var result = await connector.AuthenticateAsync(host, port, username, password, timeoutMs: 5000);

                // Update UI with result
                txtStatus.Text = result.Message;
                if (!string.IsNullOrWhiteSpace(result.ServerGreeting))
                {
                    txtStatus.Text = $"Server greeting: {result.ServerGreeting}\n{result.Message}";
                }

                var title = result.Success ? "Login succeeded" : "Login failed";
                MessageBox.Show(txtStatus.Text, title, MessageBoxButton.OK, result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);

                // Clear PasswordBox UI (we still keep the password in-memory in Application.Properties briefly)
                txtPassword.Clear();

                if (result.Success)
                {
                    // Open NewsDisplay and pass saved credentials; make it the application's main window
                    var newsWindow = new NewsDisplay(username, password, result.ServerGreeting ?? string.Empty);
                    Application.Current.MainWindow = newsWindow;
                    newsWindow.Show();

                    // Close the login window
                    this.Close();
                }
                else
                {
                    // On failed authentication we keep credentials in Application.Properties so the user can retry/list groups.
                    // Consider removing the stored password on user cancel or logout.
                }
            }
            finally
            {
                if (button != null) button.IsEnabled = true;
            }
        }

        private void txtUsername_TextChanged(object sender, TextChangedEventArgs e)
        {
        }
    }
}