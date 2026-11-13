using System.Drawing;
using YouTubeVideoPlayer.Tests.TestSupport;
using YouTubeVideoPlayer.UI;

namespace YouTubeVideoPlayer.Tests.UI;

public class WindowModeControllerTests
{
    private sealed class Window : IDisposable
    {
        public Window()
        {
            Form = new Form { FormBorderStyle = FormBorderStyle.Sizable, Size = new Size(800, 600) };
            VideoArea = new Panel { Dock = DockStyle.Fill };
            ControlPanel = new Panel { Dock = DockStyle.Bottom, Height = 40 };
            Form.Controls.Add(VideoArea);
            Form.Controls.Add(ControlPanel);
            Form.ShowHidden();
            Controller = new WindowModeController(Form, VideoArea, ControlPanel);
            Controller.ModeChanged += (_, _) => ModeChanges++;
            OriginalBounds = Form.Bounds;
        }

        public Form Form { get; }

        public Panel VideoArea { get; }

        public Panel ControlPanel { get; }

        public WindowModeController Controller { get; }

        public Rectangle OriginalBounds { get; }

        public int ModeChanges { get; private set; }

        public void Dispose() => Form.Dispose();
    }

    [Fact]
    public void Fullscreen_HidesTheControlsAndMaximizesOnTop_AndRestoresEverything() => UiContext.Run(() =>
    {
        using var window = new Window();

        window.Controller.ToggleFullscreen();

        Assert.True(window.Controller.IsFullscreen);
        Assert.Equal(FormBorderStyle.None, window.Form.FormBorderStyle);
        Assert.Equal(FormWindowState.Maximized, window.Form.WindowState);
        Assert.True(window.Form.TopMost);
        Assert.False(window.ControlPanel.Visible);

        window.Controller.ToggleFullscreen();

        Assert.False(window.Controller.IsFullscreen);
        Assert.Equal(FormBorderStyle.Sizable, window.Form.FormBorderStyle);
        Assert.Equal(FormWindowState.Normal, window.Form.WindowState);
        Assert.False(window.Form.TopMost);
        Assert.True(window.ControlPanel.Visible);
        Assert.Equal(window.OriginalBounds, window.Form.Bounds);
        Assert.Equal(2, window.ModeChanges);
    }, sta: true);

    [Fact]
    public void Borderless_ShrinksTheWindowToTheVideo_AndRestoresIt() => UiContext.Run(() =>
    {
        using var window = new Window();
        var videoOnScreen = window.VideoArea.RectangleToScreen(window.VideoArea.ClientRectangle);

        window.Controller.ToggleBorderless();

        Assert.True(window.Controller.IsBorderless);
        Assert.Equal(FormBorderStyle.None, window.Form.FormBorderStyle);
        Assert.Equal(videoOnScreen, window.Form.Bounds);
        Assert.False(window.ControlPanel.Visible);
        Assert.Equal(DockStyle.Fill, window.VideoArea.Dock);

        window.Controller.ToggleBorderless();

        Assert.False(window.Controller.IsBorderless);
        Assert.Equal(FormBorderStyle.Sizable, window.Form.FormBorderStyle);
        Assert.Equal(window.OriginalBounds, window.Form.Bounds);
        Assert.True(window.ControlPanel.Visible);
        Assert.Equal(2, window.ModeChanges);
    }, sta: true);

    [Fact]
    public void LeaveCurrentMode_LeavesFullscreenFirst_ThenBorderless_ThenDoesNothing() => UiContext.Run(() =>
    {
        using var window = new Window();
        window.Controller.ToggleBorderless();
        window.Controller.ToggleFullscreen();

        Assert.True(window.Controller.LeaveCurrentMode());
        Assert.False(window.Controller.IsFullscreen);
        Assert.True(window.Controller.IsBorderless);

        Assert.True(window.Controller.LeaveCurrentMode());
        Assert.False(window.Controller.IsBorderless);

        Assert.False(window.Controller.LeaveCurrentMode());
        Assert.Equal(window.OriginalBounds, window.Form.Bounds);
    }, sta: true);
}
