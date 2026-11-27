using System.Globalization;
using System.Windows;
using System.Windows.Input;
using GeminiDesktop.Helpers;
using GeminiDesktop.Models;
using GeminiDesktop.Services;

namespace GeminiDesktop.ViewModels;

/// <summary>
/// ViewModel for transparent Privacy & Window Management controls, password-protected OFF unlock, and window opacity.
/// </summary>
public sealed class PrivacyViewModel : ViewModelBase
{
    private readonly IPrivacyService _privacyService;
    private readonly IWindowManagementService _windowService;
    private readonly IDialogService _dialogService;
    private PrivacyStatus _status;

    public PrivacyViewModel(
        IPrivacyService privacyService,
        IWindowManagementService windowService,
        IDialogService dialogService)
    {
        _privacyService = privacyService;
        _windowService = windowService;
        _dialogService = dialogService;
        _status = _privacyService.CurrentStatus;

        MinimizeWindowCommand = new RelayCommand(() => _windowService.Minimize());
        ToggleMaximizeRestoreCommand = new RelayCommand(() => _windowService.ToggleMaximizeRestore());
        EnsureOnScreenCommand = new RelayCommand(() => _windowService.EnsureWindowVisibleOnDesktop());
        RefreshStatusCommand = new RelayCommand(RefreshFromService);
        SetOpacityPresetCommand = new RelayCommand<string>(ApplyOpacityPreset);

        _privacyService.PrivacyStatusChanged += (_, s) => ApplyStatus(s);
        _windowService.AlwaysOnTopChanged += (_, _) => OnPropertyChanged(nameof(IsAlwaysOnTop));
        _windowService.WindowOpacityChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(WindowOpacity));
            OnPropertyChanged(nameof(WindowOpacityPercent));
            OnPropertyChanged(nameof(BackgroundVisibilityPercent));
        };
    }

    public ICommand MinimizeWindowCommand { get; }
    public ICommand ToggleMaximizeRestoreCommand { get; }
    public ICommand EnsureOnScreenCommand { get; }
    public ICommand RefreshStatusCommand { get; }
    public ICommand SetOpacityPresetCommand { get; }

    public bool IsCaptureExclusionEnabled
    {
        get => _status.IsCaptureExclusionActive;
        set
        {
            if (_status.IsCaptureExclusionActive == value)
            {
                return;
            }

            if (!PrivacyService.TryChangePrivacyModeWithPassword(_privacyService, _dialogService, value))
            {
                ReconcileToggleVisual();
                return;
            }

            ApplyStatus(_privacyService.CurrentStatus);
        }
    }

    public bool IsAlwaysOnTop
    {
        get => _windowService.IsAlwaysOnTop;
        set
        {
            if (_windowService.IsAlwaysOnTop == value)
            {
                return;
            }

            _windowService.SetAlwaysOnTop(value);
            OnPropertyChanged();
        }
    }

    public double WindowOpacity
    {
        get => _windowService.WindowOpacity;
        set
        {
            var clamped = AppSettings.ClampOpacity(value);
            if (Math.Abs(_windowService.WindowOpacity - clamped) > 0.001)
            {
                _windowService.SetWindowOpacity(clamped, persist: true);
                OnPropertyChanged();
                OnPropertyChanged(nameof(WindowOpacityPercent));
                OnPropertyChanged(nameof(BackgroundVisibilityPercent));
            }
        }
    }

    public int WindowOpacityPercent => (int)Math.Round(WindowOpacity * 100);
    public int BackgroundVisibilityPercent => Math.Max(0, 100 - WindowOpacityPercent);

    public bool IsCaptureExclusionSupported => _status.IsExcludeFromCaptureSupported;
    public bool IsCaptureExclusionActive => _status.IsCaptureExclusionActive;
    public bool HasAnyPrivacyModeActive => _status.HasAnyActivePrivacyMode;
    public string StatusSummary => _status.StatusSummary;
    public string OsVersionDescription => _status.OsVersionDescription;
    public string CapabilitySummary => _status.CapabilitySummary;
    public string SharingExplanation => _status.SharingExplanation;
    public string LimitationNotice => _status.LimitationNotice;
    public string LastErrorMessage => _status.LastErrorMessage ?? string.Empty;

    public string AppliedAffinityDisplay => _status.AppliedAffinity switch
    {
        WindowDisplayAffinityMode.ExcludeFromCapture => "WDA_EXCLUDEFROMCAPTURE (0x11)",
        WindowDisplayAffinityMode.MonitorOnly => "WDA_MONITOR (0x01)",
        _ => "WDA_NONE (0x00)"
    };

    public void RefreshFromService() => ApplyStatus(_privacyService.RefreshStatus());

    private void ApplyOpacityPreset(string? rawPercent)
    {
        if (double.TryParse(rawPercent, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
        {
            WindowOpacity = pct / 100.0;
        }
    }

    private void ReconcileToggleVisual()
    {
        OnPropertyChanged(nameof(IsCaptureExclusionEnabled));
        Application.Current?.Dispatcher?.BeginInvoke(() => OnPropertyChanged(nameof(IsCaptureExclusionEnabled)));
    }

    private void ApplyStatus(PrivacyStatus status)
    {
        _status = status;
        OnPropertyChanged(nameof(IsCaptureExclusionEnabled));
        OnPropertyChanged(nameof(IsCaptureExclusionSupported));
        OnPropertyChanged(nameof(IsCaptureExclusionActive));
        OnPropertyChanged(nameof(HasAnyPrivacyModeActive));
        OnPropertyChanged(nameof(StatusSummary));
        OnPropertyChanged(nameof(OsVersionDescription));
        OnPropertyChanged(nameof(CapabilitySummary));
        OnPropertyChanged(nameof(SharingExplanation));
        OnPropertyChanged(nameof(LimitationNotice));
        OnPropertyChanged(nameof(AppliedAffinityDisplay));
        OnPropertyChanged(nameof(LastErrorMessage));
        OnPropertyChanged(nameof(IsAlwaysOnTop));
    }
}
