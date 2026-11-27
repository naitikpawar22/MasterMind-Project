using System.Windows;
using System.Windows.Threading;
using GeminiDesktop.Services;
using GeminiDesktop.ViewModels;
using GeminiDesktop.Views;
using Microsoft.Extensions.DependencyInjection;

namespace GeminiDesktop;

/// <summary>
/// Application entry point configuring Dependency Injection, Serilog diagnostics, theme initialization, and crash safety.
/// </summary>
public partial class App : Application
{
    private ServiceProvider? _serviceProvider;
    private ILoggingService? _loggingService;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ShutdownMode = ShutdownMode.OnMainWindowClose;

        RegisterGlobalExceptionHandlers();

        var services = new ServiceCollection();
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider();

        _loggingService = _serviceProvider.GetRequiredService<ILoggingService>();
        _loggingService.Information("MyLaptop starting up (Args={ArgCount}).", e.Args.Length);

        var settingsService = _serviceProvider.GetRequiredService<ISettingsService>();
        var themeService = _serviceProvider.GetRequiredService<IThemeService>();
        themeService.ApplyTheme(settingsService.CurrentSettings.Theme);

        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        MainWindow = mainWindow;
        mainWindow.Show();

        var micService = _serviceProvider.GetRequiredService<IMicrophoneService>();
        Dispatcher.BeginInvoke(() => micService.Initialize());
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // Core Singletons
        services.AddSingleton<ILoggingService>(_ => new LoggingService());
        services.AddSingleton<ISettingsService>(sp => new SettingsService(sp.GetRequiredService<ILoggingService>()));
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<IDialogService, WpfDialogService>();
        services.AddSingleton<IDisplayAffinityAdapter, Win32DisplayAffinityAdapter>();
        services.AddSingleton<IWindowManagementService, WindowManagementService>();
        services.AddSingleton<IPrivacyService, PrivacyService>();
        services.AddSingleton<IMicrophoneBackend, WasapiMicrophoneBackend>();
        services.AddSingleton<IMicrophoneService, MicrophoneService>();
        services.AddSingleton<IWebViewService, WebViewService>();
        services.AddSingleton<IGeminiBrowserService, GeminiBrowserService>();

        // ViewModels
        services.AddSingleton<HomeViewModel>();
        services.AddSingleton<GeminiViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<PrivacyViewModel>();
        services.AddSingleton<AboutViewModel>();
        services.AddSingleton<MiniPopupViewModel>();
        services.AddSingleton<MainViewModel>();

        // Windows
        services.AddSingleton<MiniPopupWindow>();
        services.AddSingleton<MainWindow>();
    }

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _loggingService?.Error(e.Exception, "Unhandled UI dispatcher exception encountered.");

        var logFolder = _loggingService?.LogsDirectory ?? "local app logs";
        MessageBox.Show(
            $"MyLaptop encountered an unexpected error:\n\n{e.Exception.Message}\n\nDiagnostic details were written to:\n{logFolder}",
            "MyLaptop Error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            _loggingService?.Error(ex, "Fatal AppDomain unhandled exception (IsTerminating={Terminating}).", e.IsTerminating);
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _loggingService?.Error(e.Exception, "Unobserved background task exception.");
        e.SetObserved();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _loggingService?.Information("MyLaptop shutting down (ExitCode={ExitCode}).", e.ApplicationExitCode);

        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;

        _serviceProvider?.Dispose();
        base.OnExit(e);
    }
}
