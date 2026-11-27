using System.IO;
using System.Windows;
using System.Windows.Interop;
using GeminiDesktop.Helpers;
using GeminiDesktop.Models;
using GeminiDesktop.Services;
using Xunit;

namespace GeminiDesktop.Tests;

public sealed class PrivacyServiceTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "GeminiPrivacyTests_" + Guid.NewGuid().ToString("N"));

    private sealed class FakeAffinityAdapter : IDisplayAffinityAdapter
    {
        public WindowsVersionDetails Version { get; set; } = new(10, 0, 26300, "Windows 11 (Build 26300)");
        public WindowDisplayAffinityMode StoredAffinity { get; set; } = WindowDisplayAffinityMode.None;
        public bool ShouldSucceed { get; set; } = true;
        public int FailureErrorCode { get; set; } = 0;

        public WindowsVersionDetails GetWindowsVersion() => Version;

        public bool TrySetDisplayAffinity(IntPtr hwnd, WindowDisplayAffinityMode mode, out int win32Error)
        {
            if (!ShouldSucceed)
            {
                win32Error = FailureErrorCode;
                return false;
            }

            StoredAffinity = mode;
            win32Error = 0;
            return true;
        }

        public bool TryGetDisplayAffinity(IntPtr hwnd, out WindowDisplayAffinityMode mode, out int win32Error)
        {
            mode = StoredAffinity;
            win32Error = 0;
            return true;
        }

        public void SetDisallowPeek(IntPtr hwnd, bool disallowPeek) { }
        public void ApplyAffinityToOwnedPopups(WindowDisplayAffinityMode mode, IntPtr primaryHwnd) { }
    }

    private static T RunOnStaThread<T>(Func<T> action)
    {
        T? result = default;
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (captured is not null)
        {
            throw captured;
        }

        return result!;
    }

    [Fact]
    public void SetCaptureExclusion_OnSupportedWindows11_TogglesAffinityAndReportsStatus()
    {
        var result = RunOnStaThread(() =>
        {
            using var log = new LoggingService(_tempRoot);
            var adapter = new FakeAffinityAdapter();
            var service = new PrivacyService(adapter, log);

            var window = new Window { Width = 100, Height = 100, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
            new WindowInteropHelper(window).EnsureHandle();
            service.AttachWindow(window);

            var enabledStatus = service.SetCaptureExclusion(true);
            var disabledStatus = service.SetCaptureExclusion(false);
            window.Close();
            return (enabledStatus, disabledStatus);
        });

        Assert.True(result.enabledStatus.IsCaptureExclusionActive);
        Assert.Equal(WindowDisplayAffinityMode.ExcludeFromCapture, result.enabledStatus.AppliedAffinity);
        Assert.Contains("does NOT guarantee invisibility", result.enabledStatus.LimitationNotice);

        Assert.False(result.disabledStatus.IsCaptureExclusionActive);
        Assert.Equal(WindowDisplayAffinityMode.None, result.disabledStatus.AppliedAffinity);
    }

    [Fact]
    public void SetCaptureExclusion_OnUnsupportedOsBuild_ExplainsUnavailability()
    {
        var status = RunOnStaThread(() =>
        {
            using var log = new LoggingService(_tempRoot);
            var adapter = new FakeAffinityAdapter
            {
                Version = new WindowsVersionDetails(10, 0, 17763, "Windows 10 (Build 17763)")
            };
            var service = new PrivacyService(adapter, log);

            var window = new Window { Width = 100, Height = 100, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
            new WindowInteropHelper(window).EnsureHandle();
            service.AttachWindow(window);

            var res = service.SetCaptureExclusion(true);
            window.Close();
            return res;
        });

        Assert.False(status.IsCaptureExclusionActive);
        Assert.False(status.IsExcludeFromCaptureSupported);
        Assert.Equal(50, status.LastWin32ErrorCode);
        Assert.Contains("19041", status.LastErrorMessage);
    }

    [Fact]
    public void SetCaptureExclusion_WhenWin32RejectsCall_SurfacesErrorHonestly()
    {
        var status = RunOnStaThread(() =>
        {
            using var log = new LoggingService(_tempRoot);
            var adapter = new FakeAffinityAdapter { ShouldSucceed = false, FailureErrorCode = 5 };
            var service = new PrivacyService(adapter, log);

            var window = new Window { Width = 100, Height = 100, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
            new WindowInteropHelper(window).EnsureHandle();
            service.AttachWindow(window);

            var res = service.SetCaptureExclusion(true);
            window.Close();
            return res;
        });

        Assert.False(status.IsCaptureExclusionActive);
        Assert.Equal(5, status.LastWin32ErrorCode);
        Assert.Contains("Win32 code 5", status.LastErrorMessage);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors.
        }
    }
}
