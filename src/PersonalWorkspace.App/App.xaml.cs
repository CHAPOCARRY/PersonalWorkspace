using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PersonalWorkspace.App.Infrastructure;
using PersonalWorkspace.App.ViewModels;
using PersonalWorkspace.Core;
using PersonalWorkspace.Services;
using Serilog;
using Serilog.Formatting.Json;

namespace PersonalWorkspace.App;

public partial class App : Application
{
    private ServiceProvider? services;
    private Serilog.Core.Logger? logger;
    private Window? window;
    private FileStream? instanceLease;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, args) => logger?.Fatal(args.Exception, "Unhandled UI error; application terminating");
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            logger?.Fatal(args.ExceptionObject as Exception, "Unhandled process error");
            logger?.Dispose();
        };
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            var paths = new ApplicationPaths();
            paths.EnsureDirectories();
            logger = new LoggerConfiguration().MinimumLevel.Information()
                .WriteTo.File(new JsonFormatter(), Path.Combine(paths.Logs, "app-.jsonl"),
                    rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14,
                    fileSizeLimitBytes: 5_000_000, rollOnFileSizeLimit: true, shared: true)
                .CreateLogger();
            logger.Information("Application startup");
            // Prevent a second process from retaining a context while the first deletes its workspace.
            try { instanceLease = new FileStream(paths.InstanceLock, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException exception) when ((exception.HResult & 0xFFFF) is 32 or 33)
            { throw new ProfileOperationException("Personal Workspace is already open. Close the other window before starting another copy.", exception); }
            services = CompositionRoot.Build(paths, logger);
            // Resolution happens only at the application composition boundary.
            await services.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
            await services.GetRequiredService<ProfilesViewModel>().InitializeAsync();
            await services.GetRequiredService<ShellViewModel>().InitializeAsync();
            await services.GetRequiredService<WindowStateController>().InitializeAsync();
            window = services.GetRequiredService<MainWindow>();
            window.Closed += (_, _) => Shutdown();
            window.Activate();
        }
        catch (Exception exception)
        {
            logger?.Fatal(exception, "Application startup failed");
            System.Diagnostics.Trace.TraceError("Application startup failed: {0}", exception);
            window = new Window
            {
                Title = "Personal Workspace — Unable to start",
                Content = new TextBlock
                {
                    Text = exception is ProfileOperationException ? exception.Message : "Personal Workspace could not start. Check access to the local application folder and review the logs in %LOCALAPPDATA%\\PersonalWorkspace\\Logs, then try again.",
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(24)
                }
            };
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(560, 240));
            window.Closed += (_, _) => Shutdown();
            window.Activate();
        }
    }

    private void Shutdown()
    {
        logger?.Information("Application shutdown");
        services?.Dispose();
        instanceLease?.Dispose();
        logger?.Dispose();
    }
}
