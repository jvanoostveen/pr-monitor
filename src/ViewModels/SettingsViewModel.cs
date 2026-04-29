using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.Win32;
using PrMonitor.Models;
using PrMonitor.Settings;
using NotificationMode = PrMonitor.Models.NotificationMode;

namespace PrMonitor.ViewModels;

/// <summary>
/// ViewModel for the Settings window.
/// </summary>
public sealed class SettingsViewModel : INotifyPropertyChanged
{
    private const string AutoStartRegistryKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string AutoStartValueName = "PrBot";

    private readonly AppSettings _settings;

    public SettingsViewModel(AppSettings settings)
    {
        _settings = settings;
        _organizationsText = string.Join(Environment.NewLine, settings.Organizations);
        _pollingIntervalSeconds = settings.PollingIntervalSeconds;
        _autoStartWithWindows = settings.AutoStartWithWindows;
        _notifyCiFailed = settings.NotifyCiFailed;
        _notifyCiPassed = settings.NotifyCiPassed;
        _notifyCiError = settings.NotifyCiError;
        _notifyReviewRequested = settings.NotifyReviewRequested;
        _notifyPrMergedOrClosed = settings.NotifyPrMergedOrClosed;
        _notifyFlakinessRerun = settings.NotifyFlakinessRerun;
        _notifyFlakinessRealFailure = settings.NotifyFlakinessRealFailure;
        _notifyStartupSummary = settings.NotifyStartupSummary;
        _notifyMentioned = settings.NotifyMentioned;
        _notificationMode = settings.NotificationMode;
        _showTeamReviewSection = settings.ShowTeamReviewSection;
        _teamReviewCountsForTrayIcon = settings.TeamReviewCountsForTrayIcon;
        _flakinessAnalysisEnabled = settings.FlakinessAnalysisEnabled;
        _flakinessAutoMergeOnly = settings.FlakinessAutoMergeOnly;
        _flakinessMaxReruns = Math.Clamp(settings.FlakinessMaxReruns, 1, 10);
        _flakinessCustomHints = settings.FlakinessCustomHints;
        _autoMergeMergeMethod = settings.AutoMergeMergeMethod switch
        {
            "squash" => "squash",
            "rebase" => "rebase",
            _        => "merge",
        };
        foreach (var rule in settings.FlakinessRules)
            FlakinessRules.Add(new FlakinessRuleViewModel(rule));

        foreach (var key in settings.HiddenPrKeys
            .Where(k => !settings.SnoozedPrs.ContainsKey(k))
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            HiddenPrs.Add(new HiddenPrEntryViewModel(key));
        }
    }

    // ── Bindable properties ─────────────────────────────────────────

    private string _organizationsText;
    /// <summary>
    /// One org per line.
    /// </summary>
    public string OrganizationsText
    {
        get => _organizationsText;
        set => SetField(ref _organizationsText, value);
    }

    private int _pollingIntervalSeconds;
    public int PollingIntervalSeconds
    {
        get => _pollingIntervalSeconds;
        set => SetField(ref _pollingIntervalSeconds, Math.Max(30, value)); // minimum 30s
    }

    private bool _autoStartWithWindows;
    public bool AutoStartWithWindows
    {
        get => _autoStartWithWindows;
        set => SetField(ref _autoStartWithWindows, value);
    }

    public bool CompactMode
    {
        get => _settings.CompactMode;
        set { _settings.CompactMode = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CompactMode))); }
    }

    public bool VerboseLogging
    {
        get => _settings.VerboseLogging;
        set { _settings.VerboseLogging = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VerboseLogging))); }
    }

    // ── Notification toggles ─────────────────────────────────────────

    private bool _notifyCiFailed;
    public bool NotifyCiFailed
    {
        get => _notifyCiFailed;
        set => SetField(ref _notifyCiFailed, value);
    }

    private bool _notifyCiPassed;
    public bool NotifyCiPassed
    {
        get => _notifyCiPassed;
        set => SetField(ref _notifyCiPassed, value);
    }

    private bool _notifyCiError;
    public bool NotifyCiError
    {
        get => _notifyCiError;
        set => SetField(ref _notifyCiError, value);
    }

    private bool _notifyReviewRequested;
    public bool NotifyReviewRequested
    {
        get => _notifyReviewRequested;
        set => SetField(ref _notifyReviewRequested, value);
    }

    private bool _notifyPrMergedOrClosed;
    public bool NotifyPrMergedOrClosed
    {
        get => _notifyPrMergedOrClosed;
        set => SetField(ref _notifyPrMergedOrClosed, value);
    }

    private bool _notifyFlakinessRerun;
    public bool NotifyFlakinessRerun
    {
        get => _notifyFlakinessRerun;
        set => SetField(ref _notifyFlakinessRerun, value);
    }

    private bool _notifyFlakinessRealFailure;
    public bool NotifyFlakinessRealFailure
    {
        get => _notifyFlakinessRealFailure;
        set => SetField(ref _notifyFlakinessRealFailure, value);
    }

    private bool _notifyStartupSummary;
    public bool NotifyStartupSummary
    {
        get => _notifyStartupSummary;
        set => SetField(ref _notifyStartupSummary, value);
    }

    private bool _notifyMentioned;
    public bool NotifyMentioned
    {
        get => _notifyMentioned;
        set => SetField(ref _notifyMentioned, value);
    }

    // ── Notification mode ────────────────────────────────────────────────

    private NotificationMode _notificationMode;

    public bool NotificationModeAlways
    {
        get => _notificationMode == NotificationMode.Always;
        set { if (value) { _notificationMode = NotificationMode.Always; OnModeChanged(); } }
    }

    public bool NotificationModeWhenWindowClosed
    {
        get => _notificationMode == NotificationMode.WhenWindowClosed;
        set { if (value) { _notificationMode = NotificationMode.WhenWindowClosed; OnModeChanged(); } }
    }

    public bool NotificationModeNever
    {
        get => _notificationMode == NotificationMode.Never;
        set { if (value) { _notificationMode = NotificationMode.Never; OnModeChanged(); } }
    }

    /// <summary>False when Never is selected — disables the per-type checkboxes.</summary>
    public bool IndividualTogglesEnabled => _notificationMode != NotificationMode.Never;

    private void OnModeChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NotificationModeAlways)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NotificationModeWhenWindowClosed)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NotificationModeNever)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IndividualTogglesEnabled)));
    }

    private bool _showTeamReviewSection;
    public bool ShowTeamReviewSection
    {
        get => _showTeamReviewSection;
        set => SetField(ref _showTeamReviewSection, value);
    }

    private bool _teamReviewCountsForTrayIcon;
    public bool TeamReviewCountsForTrayIcon
    {
        get => _teamReviewCountsForTrayIcon;
        set => SetField(ref _teamReviewCountsForTrayIcon, value);
    }

    // ── Flakiness analysis ───────────────────────────────────────────────

    private bool _flakinessAnalysisEnabled;
    public bool FlakinessAnalysisEnabled
    {
        get => _flakinessAnalysisEnabled;
        set => SetField(ref _flakinessAnalysisEnabled, value);
    }

    private bool _flakinessAutoMergeOnly;
    public bool FlakinessAutoMergeOnly
    {
        get => _flakinessAutoMergeOnly;
        set => SetField(ref _flakinessAutoMergeOnly, value);
    }

    private int _flakinessMaxReruns;
    public int FlakinessMaxReruns
    {
        get => _flakinessMaxReruns;
        set => SetField(ref _flakinessMaxReruns, Math.Clamp(value, 1, 10));
    }

    private string _flakinessCustomHints = "";
    public string FlakinessCustomHints
    {
        get => _flakinessCustomHints;
        set => SetField(ref _flakinessCustomHints, value is null ? "" : value.Length <= 500 ? value : value[..500]);
    }

    public ObservableCollection<FlakinessRuleViewModel> FlakinessRules { get; } = [];
    public ObservableCollection<HiddenPrEntryViewModel> HiddenPrs { get; } = [];

    public void RemoveHiddenPr(string key)
    {
        var vm = HiddenPrs.FirstOrDefault(h => h.Key.Equals(key, StringComparison.Ordinal));
        if (vm is null)
            return;

        HiddenPrs.Remove(vm);
    }

    // ── Auto-merge merge method ────────────────────────────────────────────

    private string _autoMergeMergeMethod;

    public bool AutoMergeMethodMerge
    {
        get => _autoMergeMergeMethod == "merge";
        set { if (value) { _autoMergeMergeMethod = "merge"; OnAutoMergeMethodChanged(); } }
    }

    public bool AutoMergeMethodSquash
    {
        get => _autoMergeMergeMethod == "squash";
        set { if (value) { _autoMergeMergeMethod = "squash"; OnAutoMergeMethodChanged(); } }
    }

    public bool AutoMergeMethodRebase
    {
        get => _autoMergeMergeMethod == "rebase";
        set { if (value) { _autoMergeMergeMethod = "rebase"; OnAutoMergeMethodChanged(); } }
    }

    private void OnAutoMergeMethodChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AutoMergeMethodMerge)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AutoMergeMethodSquash)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AutoMergeMethodRebase)));
    }

    public void DeleteRule(string id)
    {
        var vm = FlakinessRules.FirstOrDefault(r => r.Id == id);
        if (vm is null) return;
        FlakinessRules.Remove(vm);
    }

    // ── Save ────────────────────────────────────────────────────────

    public void Save()
    {
        _settings.Organizations = _organizationsText
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(o => o.Trim())
            .Where(o => o.Length > 0)
            .Distinct()
            .ToList();

        _settings.PollingIntervalSeconds = _pollingIntervalSeconds;
        _settings.AutoStartWithWindows = _autoStartWithWindows;
        _settings.NotifyCiFailed = _notifyCiFailed;
        _settings.NotifyCiPassed = _notifyCiPassed;
        _settings.NotifyCiError = _notifyCiError;
        _settings.NotifyReviewRequested = _notifyReviewRequested;
        _settings.NotifyPrMergedOrClosed = _notifyPrMergedOrClosed;
        _settings.NotifyFlakinessRerun = _notifyFlakinessRerun;
        _settings.NotifyFlakinessRealFailure = _notifyFlakinessRealFailure;
        _settings.NotifyStartupSummary = _notifyStartupSummary;
        _settings.NotifyMentioned = _notifyMentioned;
        _settings.NotificationMode = _notificationMode;
        _settings.ShowTeamReviewSection = _showTeamReviewSection;
        _settings.TeamReviewCountsForTrayIcon = _teamReviewCountsForTrayIcon;
        _settings.FlakinessAnalysisEnabled = _flakinessAnalysisEnabled;
        _settings.FlakinessAutoMergeOnly = _flakinessAutoMergeOnly;
        _settings.FlakinessMaxReruns = _flakinessMaxReruns;
        _settings.FlakinessCustomHints = _flakinessCustomHints;
        _settings.AutoMergeMergeMethod = _autoMergeMergeMethod;

        var snoozedKeys = _settings.SnoozedPrs.Keys.ToHashSet(StringComparer.Ordinal);
        var permanentlyHiddenKeys = HiddenPrs.Select(h => h.Key).ToHashSet(StringComparer.Ordinal);

        _settings.HiddenPrKeys = _settings.HiddenPrKeys
            .Where(k => snoozedKeys.Contains(k) || permanentlyHiddenKeys.Contains(k))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var key in permanentlyHiddenKeys)
            _settings.HiddenPrKeys.Add(key);

        var now = DateTimeOffset.UtcNow;
        foreach (var key in permanentlyHiddenKeys.Where(k => !_settings.HiddenPrLastSeen.ContainsKey(k)))
            _settings.HiddenPrLastSeen[key] = now;

        foreach (var key in _settings.HiddenPrLastSeen.Keys.Where(k => !_settings.HiddenPrKeys.Contains(k)).ToList())
            _settings.HiddenPrLastSeen.Remove(key);

        _settings.FlakinessRules = FlakinessRules.Select(vm => new FlakinessRule
        {
            Id = vm.Id,
            Pattern = vm.Pattern,
            Description = vm.Description,
            IsEnabled = vm.IsEnabled,
        }).ToList();
        _settings.Save();

        ApplyAutoStart(_autoStartWithWindows);
        if (System.Windows.Application.Current is not null)
            App.ApplyCompactMode(_settings.CompactMode);
    }

    // ── Auto-start registry ─────────────────────────────────────────

    public static void ApplyAutoStart(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AutoStartRegistryKey, writable: true);
            if (key is null) return;

            if (enable)
            {
                var exePath = Environment.ProcessPath;
                if (exePath is not null)
                    key.SetValue(AutoStartValueName, $"\"{exePath}\"");
            }
            else
            {
                key.DeleteValue(AutoStartValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // Silently fail if we can't write the registry
        }
    }

    // ── INotifyPropertyChanged ──────────────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
    // ── Flakiness rule view model ──────────────────────────────────────────────

    public sealed class FlakinessRuleViewModel : INotifyPropertyChanged
    {
        public string Id { get; }
        public string Pattern { get; }
        public string Description { get; }

        private bool _isEnabled;
        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (_isEnabled == value) return;
                _isEnabled = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled)));
            }
        }

        public int MatchCount { get; }

        public FlakinessRuleViewModel(FlakinessRule rule)
        {
            Id = rule.Id;
            Pattern = rule.Pattern;
            Description = rule.Description;
            _isEnabled = rule.IsEnabled;
            MatchCount = rule.MatchCount;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public sealed class HiddenPrEntryViewModel
    {
        public HiddenPrEntryViewModel(string key)
        {
            Key = key;
        }

        public string Key { get; }
    }
}
