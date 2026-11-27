using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using GeminiDesktop.ViewModels;

namespace GeminiDesktop.Views;

/// <summary>
/// Hosts the WebView2 control, triggers async initialization, and keeps live speech transcript auto-scrolled.
/// </summary>
public partial class GeminiView : UserControl
{
    private bool _initializationStarted;
    private GeminiViewModel? _attachedViewModel;

    public GeminiView()
    {
        InitializeComponent();
        Loaded += OnViewLoadedOrVisible;
        IsVisibleChanged += OnViewVisibilityChanged;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_attachedViewModel is not null)
        {
            _attachedViewModel.ReinitializeRequested -= OnReinitializeRequested;
            _attachedViewModel.TranscriptEntries.CollectionChanged -= OnTranscriptEntriesChanged;
        }

        _attachedViewModel = e.NewValue as GeminiViewModel;
        if (_attachedViewModel is not null)
        {
            _attachedViewModel.ReinitializeRequested += OnReinitializeRequested;
            _attachedViewModel.TranscriptEntries.CollectionChanged += OnTranscriptEntriesChanged;
        }
    }

    private void OnTranscriptEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
        {
            Dispatcher.BeginInvoke(() => TranscriptScrollViewer?.ScrollToBottom());
        }
    }

    private async void OnReinitializeRequested(object? sender, EventArgs e)
    {
        if (DataContext is GeminiViewModel vm)
        {
            _initializationStarted = true;
            await vm.InitializeBrowserAsync(GeminiWebView);
        }
    }

    private async void OnViewLoadedOrVisible(object sender, RoutedEventArgs e) =>
        await TryInitializeIfVisibleAsync();

    private async void OnViewVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        await TryInitializeIfVisibleAsync();

    private async Task TryInitializeIfVisibleAsync()
    {
        if (_initializationStarted || !IsVisible || DataContext is not GeminiViewModel vm)
        {
            return;
        }

        _initializationStarted = true;
        await vm.InitializeBrowserAsync(GeminiWebView);

        if (!vm.IsWebView2Available || (vm.HasError && !vm.IsInitializing))
        {
            _initializationStarted = vm.IsWebView2Available;
        }
    }
}
