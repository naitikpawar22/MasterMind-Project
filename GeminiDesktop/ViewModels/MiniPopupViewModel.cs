using System.Windows.Input;
using GeminiDesktop.Helpers;
using GeminiDesktop.Models;
using GeminiDesktop.Services;

namespace GeminiDesktop.ViewModels;

/// <summary>
/// ViewModel for the compact floating restore popup shown when the main MasterMind window is minimized.
/// </summary>
public sealed class MiniPopupViewModel : ViewModelBase
{
    private readonly IPrivacyService _privacyService;
    private bool _isPrivacyModeActive;
    private string _privacyStatusLabel = "Privacy Mode: Off";

    public MiniPopupViewModel(IPrivacyService privacyService)
    {
        _privacyService = privacyService;

        RestoreCommand = new RelayCommand(() => RestoreRequested?.Invoke(this, false));
        OpenGeminiCommand = new RelayCommand(() => RestoreRequested?.Invoke(this, true));
        DismissCommand = new RelayCommand(() => DismissRequested?.Invoke(this, EventArgs.Empty));

        _privacyService.PrivacyStatusChanged += (_, status) => ApplyPrivacyStatus(status);
        ApplyPrivacyStatus(_privacyService.CurrentStatus);
    }

    public event EventHandler<bool>? RestoreRequested;
    public event EventHandler? DismissRequested;

    public ICommand RestoreCommand { get; }
    public ICommand OpenGeminiCommand { get; }
    public ICommand DismissCommand { get; }

    public bool IsPrivacyModeActive
    {
        get => _isPrivacyModeActive;
        private set => SetProperty(ref _isPrivacyModeActive, value);
    }

    public string PrivacyStatusLabel
    {
        get => _privacyStatusLabel;
        private set => SetProperty(ref _privacyStatusLabel, value);
    }

    private void ApplyPrivacyStatus(PrivacyStatus status)
    {
        IsPrivacyModeActive = status.IsCaptureExclusionActive;
        PrivacyStatusLabel = status.IsCaptureExclusionActive ? "Privacy: Active" : "Privacy: Off";
    }
}
