using System.Windows;

namespace GeminiDesktop.Views;

/// <summary>
/// Modal password prompt shown only when disabling an active Privacy Mode lock.
/// </summary>
public partial class PasswordPromptWindow : Window
{
    public PasswordPromptWindow(string title, string message)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        PromptMessageText.Text = message;
        Loaded += (_, _) => PasswordInputBox.Focus();
    }

    public string EnteredPassword { get; private set; } = string.Empty;

    private void OnConfirmClicked(object sender, RoutedEventArgs e)
    {
        EnteredPassword = PasswordInputBox.Password;
        DialogResult = true;
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        EnteredPassword = string.Empty;
        DialogResult = false;
    }
}
