using Microsoft.Extensions.DependencyInjection;
using PersonalWorkspace.App.ViewModels;
using PersonalWorkspace.Core;
using PersonalWorkspace.Data;
using PersonalWorkspace.Data.Migrations;
using PersonalWorkspace.Services;
using Serilog;

namespace PersonalWorkspace.App.Infrastructure;

internal static class CompositionRoot
{
    public static ServiceProvider Build(IApplicationPaths paths, ILogger logger)
    {
        var services = new ServiceCollection();
        services.AddSingleton(paths);
        services.AddLogging(builder => builder.AddSerilog(logger, dispose: false));
        services.AddSingleton<SqliteConnectionFactory>();
        services.AddSingleton<IEnumerable<DatabaseMigration>>(MigrationCatalog.All);
        services.AddSingleton<IDatabaseInitializer, DatabaseInitializer>();
        services.AddSingleton<ISettingsService, SqliteSettingsService>();
        services.AddSingleton<IWorkspaceOperationGate, WorkspaceOperationGate>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ITaskRepository, SqliteTaskRepository>();
        services.AddSingleton<ITaskService, TaskService>();
        services.AddSingleton<IOrganizationRepository, SqliteOrganizationRepository>();
        services.AddSingleton<IOrganizationService, OrganizationService>();
        services.AddSingleton<OrganizationViewModel>();
        services.AddSingleton<TaskWorkspaceViewModel>();
        services.AddSingleton<IProfileRepository, SqliteProfileRepository>();
        services.AddSingleton<IWorkspaceInitializer, WorkspaceInitializer>();
        services.AddSingleton<IProfileFiles, ProfileFiles>();
        services.AddSingleton<CurrentProfile>();
        services.AddSingleton<ICurrentProfile>(provider => provider.GetRequiredService<CurrentProfile>());
        services.AddSingleton<IProfileService, ProfileService>();
        services.AddSingleton<ProfilesViewModel>();
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<WindowStateController>();
        services.AddSingleton<MainWindow>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }
}
