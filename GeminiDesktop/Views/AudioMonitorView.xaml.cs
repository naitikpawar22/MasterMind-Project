using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using GeminiDesktop.ViewModels;

namespace GeminiDesktop.Views;

/// <summary>
/// Full-page live audio monitor and source-tagged speech transcript view accessible from the left navigation sidebar.
/// </summary>
public partial class AudioMonitorView : UserControl
{
    private GeminiViewModel? _attachedViewModel;

    public AudioMonitorView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_attachedViewModel is not null)
        {
            _attachedViewModel.TranscriptEntries.CollectionChanged -= OnTranscriptEntriesChanged;
        }

        _attachedViewModel = e.NewValue as GeminiViewModel;
        if (_attachedViewModel is not null)
        {
            _attachedViewModel.TranscriptEntries.CollectionChanged += OnTranscriptEntriesChanged;
        }
    }

    private void OnTranscriptEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
        {
            Dispatcher.BeginInvoke(() => AudioPageTranscriptScrollViewer?.ScrollToBottom());
        }
    }
}
