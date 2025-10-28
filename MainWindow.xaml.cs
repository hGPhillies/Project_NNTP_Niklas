using System.Text;
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
                // Read username/password from UI controls.
                // Ensure your XAML TextBox/PasswordBox use these names (x:Name="TxtUsername" and x:Name="TxtPassword")
                string username = string.Empty;
                string password = string.Empty;

                var usernameBox = this.FindName("TxtUsername") as TextBox;
                if (usernameBox != null) username = usernameBox.Text;

                // Prefer PasswordBox for passwords to avoid plain-text binding in UI
                var passwordBox = this.FindName("TxtPassword") as PasswordBox;
                if (passwordBox != null) password = passwordBox.Password;
                else
                {
                    // Fallback to a TextBox named TxtPassword if you used one
                    var passwordText = this.FindName("TxtPassword") as TextBox;
                    if (passwordText != null) password = passwordText.Text;
                }

                username = username?.Trim() ?? string.Empty;

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

                var host = "news.sunsite.dk";
                var port = 119;

                var connector = new ConnectionAndAuthentication();
                var result = await connector.AuthenticateAsync(host, port, username, password, timeoutMs: 5000);

                // Show server greeting + final message for context
                var title = result.Success ? "Login succeeded" : "Login failed";
                var content = result.Message;
                if (!string.IsNullOrWhiteSpace(result.ServerGreeting))
                {
                    content = $"Server greeting: {result.ServerGreeting}\n\n{content}";
                }

                MessageBox.Show(content, title, MessageBoxButton.OK, result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
            finally
            {
                if (button != null) button.IsEnabled = true;
            }
        }
    }
}