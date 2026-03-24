using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.Win32;
using PrMonitor.Models;
using PrMonitor.Settings;

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
        _showTeamReviewSection = settings.ShowTeamReviewSection;
        _flakinessAnalysisEnabled = settings.FlakinessAnalysisEnabled;
        _flakinessAutoMergeOnly = settings.FlakinessAutoMergeOnly;
        foreach (var rule in settings.FlakinessRules)
            FlakinessRules.Add(new FlakinessRuleViewModel(rule));
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

    private bool _showTeamReviewSection;
    public bool ShowTeamReviewSection
    {
        get => _showTeamReviewSection;
        set => SetField(ref _showTeamReviewSection, value);
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

    public ObservableCollection<FlakinessRuleViewModel> FlakinessRules { get; } = [];

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
        _settings.ShowTeamReviewSection = _showTeamReviewSection;
        _settings.FlakinessAnalysisEnabled = _flakinessAnalysisEnabled;
        _settings.FlakinessAutoMergeOnly = _flakinessAutoMergeOnly;
        _settings.FlakinessRules = FlakinessRules.Select(vm => new FlakinessRule
        {
            Id = vm.Id,
            Pattern = vm.Pattern,
            Description = vm.Description,
            IsEnabled = vm.IsEnabled,
        }).ToList();
        _settings.Save();

        ApplyAutoStart(_autoStartWithWindows);
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
    }}
