using System.Windows;
using GeminiDesktop.Views;

namespace GeminiDesktop.Services;

/// <summary>
/// Confirmation and password dialog abstraction so ViewModels remain testable without direct UI coupling.
/// </summary>
public interface IDialogService
{
    bool Confirm(string title, string message, bool isDestructive = false);
    void ShowError(string title, string message);
    string? PromptPassword(string title, string message);
}

public sealed class WpfDialogService : IDialogService
{
    private readonly IPrivacyService _privacyService;

    public WpfDialogService(IPrivacyService privacyService)
    {
        _privacyService = privacyService;
    }

    public bool Confirm(string title, string message, bool isDestructive = false)
    {
        var owner = Application.Current?.MainWindow;
        var icon = isDestructive ? MessageBoxImage.Warning : MessageBoxImage.Question;
        var result = owner is not null
            ? MessageBox.Show(owner, message, title, MessageBoxButton.YesNo, icon, MessageBoxResult.No)
            : MessageBox.Show(message, title, MessageBoxButton.YesNo, icon, MessageBoxResult.No);
        return result == MessageBoxResult.Yes;
    }

    public void ShowError(string title, string message)
    {
        var owner = Application.Current?.MainWindow;
        if (owner is not null)
        {
            MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        else
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public string? PromptPassword(string title, string message)
    {
        var dialog = new PasswordPromptWindow(title, message);
        var owner = Application.Current?.MainWindow;
        if (owner is not null && owner.IsVisible)
        {
            dialog.Owner = owner;
        }

        // Sync active display affinity so the password dialog matches Privacy Mode state.
        _privacyService.SyncSecondaryWindow(dialog);

        return dialog.ShowDialog() == true ? dialog.EnteredPassword : null;
    }
}
