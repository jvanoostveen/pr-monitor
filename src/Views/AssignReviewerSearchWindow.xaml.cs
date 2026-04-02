using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using PrMonitor.Services;
using PrMonitor.Settings;

namespace PrMonitor.Views;

public partial class AssignReviewerSearchWindow : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
    private const int DwmwaUseImmersiveDarkMode = 20;

    // Small VM for recents: allows DataTemplate binding to Login and Label
    private sealed class RecentItemVM
    {
        public string Login { get; }
        public string Label { get; }
        public RecentItemVM(string login, string? name)
        {
            Login = login;
            Label = string.IsNullOrWhiteSpace(name) ? login : $"{name} ({login})";
        }
    }

    private readonly GitHubService _github;
    private readonly AppSettings _settings;
    private readonly IReadOnlyList<string> _orgs;
    private CancellationTokenSource? _searchCts;

    private static readonly TimeSpan CacheMaxAge = TimeSpan.FromDays(30);

    // In-memory mirror of the persisted cache (projected from AppSettings)
    private List<(string Login, string? Name)>? _cachedMembers;
    private bool _isFetchingMembers;

    // Search results mapped by index → login (for AcceptSelectedItem)
    private List<(string Login, string? Name)> _currentResults = [];

    /// <summary>The login selected by the user, or null if cancelled.</summary>
    public string? SelectedLogin { get; private set; }

    public AssignReviewerSearchWindow(GitHubService github, AppSettings settings)
    {
        _github = github;
        _settings = settings;
        _orgs = settings.Organizations;
        InitializeComponent();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        int value = 1;
        DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref value, Marshal.SizeOf(value));
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        RefreshButton.Visibility = _orgs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // Load persistent cache into memory
        if (_orgs.Count > 0 && _settings.OrgMembersCache.Count > 0)
            _cachedMembers = _settings.OrgMembersCache
                .Select(e => (e.Login, e.Name))
                .ToList();

        SearchBox.Focus();
        ShowRecents();

        // Only fetch from GitHub when cache is absent or older than 30 days
        if (_orgs.Count > 0 && !_isFetchingMembers)
        {
            bool cacheExpired = _settings.OrgMembersCachedAt is null
                || DateTimeOffset.UtcNow - _settings.OrgMembersCachedAt.Value > CacheMaxAge;
            if (_cachedMembers is null || cacheExpired)
                _ = PrefetchOrgMembersAsync(forceRefresh: false);
        }
    }

    private async Task PrefetchOrgMembersAsync(bool forceRefresh)
    {
        _isFetchingMembers = true;
        try
        {
            var all = await _github.FetchOrgMembersAsync(_orgs);
            _cachedMembers = all;

            // Persist to settings
            _settings.OrgMembersCache = all
                .Select(m => new PrMonitor.Models.OrgMemberEntry { Login = m.Login, Name = m.Name })
                .ToList();
            _settings.OrgMembersCachedAt = DateTimeOffset.UtcNow;
            _settings.Save();
        }
        catch { /* logged by GitHubService */ }
        finally
        {
            _isFetchingMembers = false;
        }

        // If the user already typed something while we were fetching, apply the filter now
        await Dispatcher.InvokeAsync(() =>
        {
            var query = SearchBox.Text;
            if (!string.IsNullOrWhiteSpace(query) && _cachedMembers is not null)
            {
                _searchCts?.Cancel();
                var filtered = GitHubService.FilterOrgMembers(_cachedMembers, query);
                ShowSearchResults(filtered, query.Trim());
            }
            // Refresh recents labels now that cache has names
            if (string.IsNullOrWhiteSpace(SearchBox.Text))
                ShowRecents();
        });
    }

    // ── Recents panel ────────────────────────────────────────────────────

    private void ShowRecents()
    {
        var logins = _settings.RecentReviewers;
        if (logins.Count == 0)
        {
            RecentsPanel.Visibility = Visibility.Collapsed;
            AssignButton.IsEnabled = false;
            return;
        }

        var items = logins.Select(login =>
        {
            // Try to resolve name from in-memory cache first, then persisted cache
            string? name = _cachedMembers?.FirstOrDefault(m =>
                    string.Equals(m.Login, login, StringComparison.OrdinalIgnoreCase)).Name
                ?? _settings.OrgMembersCache.FirstOrDefault(m =>
                    string.Equals(m.Login, login, StringComparison.OrdinalIgnoreCase))?.Name;
            return new RecentItemVM(login, name);
        }).ToList();

        RecentsList.ItemsSource = items;
        RecentsPanel.Visibility = Visibility.Visible;
        ResultsList.Visibility = Visibility.Collapsed;
        NoResultsText.Visibility = Visibility.Collapsed;
        AssignButton.IsEnabled = RecentsList.SelectedIndex >= 0;
    }

    private void RemoveRecent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string login }) return;
        _settings.RecentReviewers.RemoveAll(r =>
            string.Equals(r, login, StringComparison.OrdinalIgnoreCase));
        _settings.Save();
        ShowRecents();
        // Keep focus on search box
        SearchBox.Focus();
    }

    // ── Search box events ────────────────────────────────────────────────

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = SearchBox.Text;

        if (string.IsNullOrWhiteSpace(query))
        {
            _searchCts?.Cancel();
            _currentResults = [];
            ResultsList.Items.Clear();
            ResultsList.Visibility = Visibility.Collapsed;
            SpinnerText.Visibility = Visibility.Collapsed;
            NoResultsText.Visibility = Visibility.Collapsed;
            AssignButton.IsEnabled = false;
            ShowRecents();
            return;
        }

        RecentsPanel.Visibility = Visibility.Collapsed;

        if (_orgs.Count > 0 && _cachedMembers is not null && !_isFetchingMembers)
        {
            // Cache warm: filter client-side immediately, no debounce
            var filtered = GitHubService.FilterOrgMembers(_cachedMembers, query);
            ShowSearchResults(filtered, query.Trim());
            return;
        }

        // Cache cold or global search: debounce then fetch
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;

        Task.Delay(400, token).ContinueWith(async t =>
        {
            if (t.IsCanceled) return;

            await Dispatcher.InvokeAsync(() =>
            {
                SpinnerText.Visibility = Visibility.Visible;
                NoResultsText.Visibility = Visibility.Collapsed;
                ResultsList.Items.Clear();
                ResultsList.Visibility = Visibility.Collapsed;
                AssignButton.IsEnabled = false;
            });

            List<(string Login, string? Name)>? found = null;
            try
            {
                if (_orgs.Count > 0)
                {
                    _isFetchingMembers = true;
                    var all = await _github.FetchOrgMembersAsync(_orgs);
                    _cachedMembers = all;
                    _isFetchingMembers = false;
                    found = GitHubService.FilterOrgMembers(all, query.Trim());
                }
                else
                {
                    found = await _github.SearchUsersAsync(query.Trim(), _orgs);
                }
            }
            catch
            {
                _isFetchingMembers = false;
            }

            if (token.IsCancellationRequested) return;
            await Dispatcher.InvokeAsync(() => ShowSearchResults(found ?? [], query.Trim()));
        }, TaskScheduler.Default);
    }

    private void SearchBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        switch (e.Key)
        {
            case System.Windows.Input.Key.Down:
                // Try results list first, then recents list
                if (ResultsList.Visibility == Visibility.Visible && ResultsList.Items.Count > 0)
                {
                    if (ResultsList.SelectedIndex < 0)
                        ResultsList.SelectedIndex = 0;
                    FocusListItem(ResultsList, ResultsList.SelectedIndex);
                    e.Handled = true;
                }
                else if (RecentsPanel.Visibility == Visibility.Visible && RecentsList.Items.Count > 0)
                {
                    if (RecentsList.SelectedIndex < 0)
                        RecentsList.SelectedIndex = 0;
                    FocusListItem(RecentsList, RecentsList.SelectedIndex);
                    e.Handled = true;
                }
                break;
            case System.Windows.Input.Key.Enter:
                AcceptSelectedItem();
                e.Handled = true;
                break;
        }
    }

    // ── Results list events ──────────────────────────────────────────────

    private void ResultsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        AssignButton.IsEnabled = ResultsList.SelectedIndex >= 0;
    }

    private void ResultsList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        AcceptSelectedItem();
    }

    private void ResultsList_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        switch (e.Key)
        {
            case System.Windows.Input.Key.Enter:
                AcceptSelectedItem();
                e.Handled = true;
                break;
            case System.Windows.Input.Key.Up when ResultsList.SelectedIndex == 0:
                ResultsList.SelectedIndex = -1;
                SearchBox.Focus();
                SearchBox.SelectAll();
                e.Handled = true;
                break;
        }
    }

    // ── Recents list events ──────────────────────────────────────────────

    private void RecentsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        AssignButton.IsEnabled = RecentsList.SelectedIndex >= 0;
    }

    private void RecentsList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        AcceptFromRecentsList();
    }

    private void RecentsList_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        switch (e.Key)
        {
            case System.Windows.Input.Key.Enter:
                AcceptFromRecentsList();
                e.Handled = true;
                break;
            case System.Windows.Input.Key.Up when RecentsList.SelectedIndex == 0:
                RecentsList.SelectedIndex = -1;
                SearchBox.Focus();
                SearchBox.SelectAll();
                e.Handled = true;
                break;
            case System.Windows.Input.Key.Delete:
                if (RecentsList.SelectedItem is RecentItemVM vm)
                {
                    _settings.RecentReviewers.RemoveAll(r =>
                        string.Equals(r, vm.Login, StringComparison.OrdinalIgnoreCase));
                    _settings.Save();
                    ShowRecents();
                    SearchBox.Focus();
                }
                e.Handled = true;
                break;
        }
    }

    // ── Toolbar buttons ──────────────────────────────────────────────────

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        _cachedMembers = null;
        _settings.OrgMembersCache = [];
        _settings.OrgMembersCachedAt = null;
        _ = PrefetchOrgMembersAsync(forceRefresh: true);
        var text = SearchBox.Text;
        if (!string.IsNullOrWhiteSpace(text))
        {
            SearchBox.Text = "";
            SearchBox.Text = text;
        }
        SearchBox.Focus();
    }

    private void Assign_Click(object sender, RoutedEventArgs e)
    {
        AcceptSelectedItem();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private void ShowSearchResults(List<(string Login, string? Name)> results, string query)
    {
        SpinnerText.Visibility = Visibility.Collapsed;
        _currentResults = results;
        ResultsList.Items.Clear();

        if (_currentResults.Count > 0)
        {
            foreach (var (login, name) in _currentResults)
                ResultsList.Items.Add(string.IsNullOrWhiteSpace(name) ? login : $"{name} ({login})");
            ResultsList.Visibility = Visibility.Visible;
            NoResultsText.Visibility = Visibility.Collapsed;
        }
        else
        {
            ResultsList.Visibility = Visibility.Collapsed;
            NoResultsText.Visibility = Visibility.Visible;
        }
        AssignButton.IsEnabled = false;
    }

    /// <summary>Focus a specific ListBoxItem, forcing layout if needed.</summary>
    private void FocusListItem(System.Windows.Controls.ListBox list, int index)
    {
        // UpdateLayout forces WPF to generate item containers synchronously
        list.UpdateLayout();
        if (list.ItemContainerGenerator.ContainerFromIndex(index) is System.Windows.Controls.ListBoxItem item)
        {
            item.Focus();
            list.ScrollIntoView(list.Items[index]);
        }
        else
        {
            // Fallback: defer one render pass and retry
            Dispatcher.InvokeAsync(() =>
            {
                if (list.ItemContainerGenerator.ContainerFromIndex(index) is System.Windows.Controls.ListBoxItem deferred)
                {
                    deferred.Focus();
                    list.ScrollIntoView(list.Items[index]);
                }
            }, DispatcherPriority.Render);
        }
    }

    private void AcceptSelectedItem()
    {
        // Check results list first (only visible when searching)
        if (ResultsList.Visibility == Visibility.Visible)
        {
            var idx = ResultsList.SelectedIndex;
            if (idx >= 0 && idx < _currentResults.Count)
            {
                SelectedLogin = _currentResults[idx].Login;
                DialogResult = true;
                return;
            }
        }
        AcceptFromRecentsList();
    }

    private void AcceptFromRecentsList()
    {
        if (RecentsList.SelectedItem is RecentItemVM vm)
        {
            SelectedLogin = vm.Login;
            DialogResult = true;
        }
    }
}
