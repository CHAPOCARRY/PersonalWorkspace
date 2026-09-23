using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using PersonalWorkspace.App.ViewModels;
using PersonalWorkspace.Core;
using Windows.Graphics;

namespace PersonalWorkspace.App.Infrastructure;

public sealed class WindowStateController(ISettingsService settings, ILogger<WindowStateController> logger)
{
    private WindowPreferences preferences = new();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var stored = await settings.GetAsync(SettingKeys.Window, new WindowPreferences(), cancellationToken);
        preferences = stored.IsValid ? stored : new();
    }

    public void Attach(Window window, ShellViewModel viewModel)
    {
        var appWindow = window.AppWindow;
        var area = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var width = Math.Min(preferences.Width, area.Width);
        var height = Math.Min(preferences.Height, area.Height);
        appWindow.MoveAndResize(new RectInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
        if (preferences.Maximized && appWindow.Presenter is OverlappedPresenter presenter) presenter.Maximize();
        appWindow.Changed += (_, args) =>
        {
            if (args.DidSizeChange && appWindow.Presenter is OverlappedPresenter p && p.State == OverlappedPresenterState.Restored)
            {
                var candidate = new WindowPreferences(appWindow.Size.Width, appWindow.Size.Height);
                if (candidate.IsValid) preferences = candidate;
            }
        };
        var closeApproved = false;
        var saving = false;
        appWindow.Closing += async (_, args) =>
        {
            if (closeApproved) return;
            args.Cancel = true;
            if (saving) return;
            saving = true;
            try
            {
                var maximized = appWindow.Presenter is OverlappedPresenter p && p.State == OverlappedPresenterState.Maximized;
                await settings.SetAsync(SettingKeys.Window, preferences with { Maximized = maximized });
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Window preferences could not be saved during shutdown");
                viewModel.ReportError("Window preferences could not be saved. The application will close with the previous settings.");
            }
            finally
            {
                closeApproved = true;
                window.DispatcherQueue.TryEnqueue(() => window.Close());
            }
        };
    }
}
