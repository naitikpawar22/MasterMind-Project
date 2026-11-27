using GeminiDesktop.Helpers;
using Xunit;

namespace GeminiDesktop.Tests;

public sealed class WindowHelperTests
{
    private static readonly ScreenWorkArea Primary = new(0, 0, 1920, 1040, true);
    private static readonly ScreenWorkArea SecondaryRight = new(1920, 0, 1920, 1040, false);
    private static readonly ScreenWorkArea SecondaryLeft = new(-1920, 0, 1920, 1040, false);

    [Fact]
    public void ClampBounds_PreservesValidPrimaryBounds()
    {
        var result = WindowHelper.ClampBoundsToVisibleMonitors(100, 80, 1280, 820, [Primary]);
        Assert.Equal((100d, 80d, 1280d, 820d), result);
    }

    [Fact]
    public void ClampBounds_RecoversOffscreenDisconnectedMonitorWindowToPrimaryCenter()
    {
        // Window was saved on a disconnected monitor at X = 2500.
        var result = WindowHelper.ClampBoundsToVisibleMonitors(2500, 100, 1280, 820, [Primary]);

        Assert.Equal(320d, result.Left);
        Assert.Equal(110d, result.Top);
        Assert.Equal(1280d, result.Width);
        Assert.Equal(820d, result.Height);
    }

    [Fact]
    public void ClampBounds_SupportsNegativeCoordinatesOnLeftSecondaryMonitor()
    {
        var result = WindowHelper.ClampBoundsToVisibleMonitors(-1600, 120, 1200, 800, [Primary, SecondaryLeft]);
        Assert.Equal((-1600d, 120d, 1200d, 800d), result);
    }

    [Fact]
    public void ClampBounds_ShrinksOversizedWindowToCurrentMonitorWorkArea()
    {
        var smallMonitor = new ScreenWorkArea(0, 0, 1024, 720, true);
        var result = WindowHelper.ClampBoundsToVisibleMonitors(50, 50, 1600, 1000, [smallMonitor]);

        Assert.Equal(0d, result.Left);
        Assert.Equal(0d, result.Top);
        Assert.Equal(1024d, result.Width);
        Assert.Equal(720d, result.Height);
    }

    [Fact]
    public void ClampBounds_PullsPartiallyOffscreenWindowInsideMonitor()
    {
        var result = WindowHelper.ClampBoundsToVisibleMonitors(3400, 600, 1280, 820, [Primary, SecondaryRight]);

        // SecondaryRight runs from X=1920 to X=3840; max left = 3840 - 1280 = 2560
        Assert.Equal(2560d, result.Left);
        Assert.Equal(220d, result.Top); // 1040 - 820
    }
}
