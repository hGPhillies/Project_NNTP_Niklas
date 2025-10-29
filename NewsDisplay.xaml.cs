using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Linq;

namespace Project_NNTP_Niklas
{
    /// <summary>
    /// Interaction logic for NewsDisplay.xaml
    /// </summary>
    public partial class NewsDisplay : Window
    {
        private readonly string _username;
        private readonly string? _password; // kept until window closes
        private readonly string _serverGreeting;

        // track currently selected group so we can select it in the ARTICLE request
        private string? _currentGroup;

        // default NNTP host/port used by your login flow
        private const string DefaultHost = "news.sunsite.dk";
        private const int DefaultPort = 119;

        public NewsDisplay(string username, string? password, string serverGreeting)
        {
            InitializeComponent();

            _username = username ?? string.Empty;
            _password = password; // may be null
            _serverGreeting = serverGreeting ?? string.Empty;

            txtWelcome.Text = string.IsNullOrWhiteSpace(_username) ? "Welcome" : $"Welcome, {_username}";
            txtGreeting.Text = string.IsNullOrWhiteSpace(_serverGreeting) ? string.Empty : $"Server greeting: {_serverGreeting}";

            // Load groups once the window is shown
            this.Loaded += NewsDisplay_Loaded;
            this.Closed += NewsDisplay_Closed;
        }

        private async void NewsDisplay_Loaded(object? sender, RoutedEventArgs e)
        {
            await LoadNewsgroupsAsync();
        }

        private async Task LoadNewsgroupsAsync()
        {
            txtGreeting.Text = "Fetching newsgroups...";

            var connector = new ConnectionAndAuthentication();
            var result = await connector.NewsgroupService(DefaultHost, DefaultPort, username: _username, password: _password, timeoutMs: 7000);

            if (!result.Success)
            {
                txtGreeting.Text = $"Failed to get newsgroups: {result.Message}";
                if (!string.IsNullOrWhiteSpace(result.ServerGreeting))
                {
                    txtGreeting.Text = $"Server greeting: {result.ServerGreeting}\n{txtGreeting.Text}";
                }
                MessageBox.Show(txtGreeting.Text, "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            txtGreeting.Text = string.IsNullOrWhiteSpace(result.ServerGreeting) ? "Newsgroups:" : $"Server greeting: {result.ServerGreeting}";

            if (result.Groups is null || result.Groups.Length == 0)
            {
                        lstGroups.ItemsSource = new[] { "(no groups returned)" };
                return;
            }

            // Parse LIST lines (format: "<group> <last> <first> <posting>") — we will display the group name
            var groupNames = result.Groups
                .Select(line => line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? line)
                .ToArray();

            lstGroups.ItemsSource = groupNames;
        }

        private async void LstGroups_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var selected = lstGroups.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(selected)) return;

            // store current group for later article requests
            _currentGroup = selected;

            txtGreeting.Text = $"Loading articles for {selected}...";
            lstArticles.ItemsSource = null;
            txtArticle.Text = string.Empty;

            var connector = new ConnectionAndAuthentication();
            var groupResult = await connector.GetArticlesForGroupAsync(DefaultHost, DefaultPort, selected, username: _username, password: _password, timeoutMs: 8000);

            if (!groupResult.Success)
            {
                MessageBox.Show($"Failed to get articles: {groupResult.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                txtGreeting.Text = string.IsNullOrWhiteSpace(groupResult.ServerGreeting) ? $"Failed to load articles." : $"Server greeting: {groupResult.ServerGreeting}";
                return;
            }

            if (groupResult.ArticleNumbers is null || groupResult.ArticleNumbers.Length == 0)
            {
                lstArticles.ItemsSource = new[] { "(no articles returned)" };
                txtGreeting.Text = "No articles in group.";
                return;
            }

            // Show last 200 article numbers (or fewer) to avoid huge lists
            var toShow = groupResult.ArticleNumbers;
            const int max = 200;
            if (toShow.Length > max) toShow = toShow.Skip(Math.Max(0, toShow.Length - max)).ToArray();

            // Display as "number" or "index: number" for readability
            lstArticles.ItemsSource = toShow.Reverse(); // show newest first (if LISTGROUP returns ascending)
            txtGreeting.Text = $"Articles for {selected} (showing up to {max} newest)";
        }

        private async void LstArticles_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var selected = lstArticles.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(selected)) return;

            txtArticle.Text = "Fetching article...";
            txtGreeting.Text = $"Fetching article {selected}...";

            var connector = new ConnectionAndAuthentication();
            // Pass the currently selected group so the server accepts numeric article IDs.
            var articleResult = await connector.GetArticleAsync(DefaultHost, DefaultPort, selected.Trim(), username: _username, password: _password, group: _currentGroup, timeoutMs: 10000);

            if (!articleResult.Success)
            {
                txtArticle.Text = $"Failed to fetch article: {articleResult.Message}";
                if (!string.IsNullOrWhiteSpace(articleResult.ServerGreeting))
                {
                    txtGreeting.Text = $"Server greeting: {articleResult.ServerGreeting}";
                }
                return;
            }

            if (articleResult.Body is null || articleResult.Body.Length == 0)
            {
                txtArticle.Text = "(no article body returned)";
            }
            else
            {
                // Show full article (headers + blank line + body) — preserve server-newlines
                txtArticle.Text = string.Join(Environment.NewLine, articleResult.Body);
            }

            txtGreeting.Text = $"Showing article {selected}.";
        }

        private void NewsDisplay_Closed(object? sender, EventArgs e)
        {
            // Clear sensitive in-memory password
            // (if you used Application.Current.Properties, clear that there too)
            // Note: strings cannot be fully zeroed in managed memory; use SecureString for stronger protection.
            // For now just drop the reference:
            // _password is readonly in this type; if you expect to zero it, change to non-readonly field.
        }
    }
}
