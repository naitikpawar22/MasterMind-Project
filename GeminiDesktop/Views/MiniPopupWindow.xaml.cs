using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using GeminiDesktop.ViewModels;

namespace GeminiDesktop.Views;

/// <summary>
/// Compact floating MasterMind window displayed when the main window is minimized (when enabled).
/// </summary>
public partial class MiniPopupWindow : Window
{
    public MiniPopupWindow(MiniPopupViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    /// <summary>
    /// Prevents Alt+F4 from destroying the reusable mini popup unless the main window is shutting down.
    /// </summary>
    public bool AllowClose { get; set; }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    private void OnWindowMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
