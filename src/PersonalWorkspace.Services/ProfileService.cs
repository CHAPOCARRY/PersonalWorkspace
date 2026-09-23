using System.Text;
using Microsoft.Extensions.Logging;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.Services;

public sealed class ProfileService(IProfileRepository repository, ISettingsService settings, IProfileFiles files,
    IWorkspaceInitializer workspaces, CurrentProfile current, ILogger<ProfileService> logger) : IProfileService, IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public Task<IReadOnlyList<Profile>> GetAllAsync(CancellationToken cancellationToken = default) =>
        RunAsync(() => repository.GetAllAsync(cancellationToken), cancellationToken);

    public Task RestoreAsync(CancellationToken cancellationToken = default) => RunAsync(async () =>
    {
        var profiles = await repository.GetAllAsync(cancellationToken);
        string? cleanupWarning = null;
        foreach (var id in await repository.GetPendingDeletionsAsync(cancellationToken))
        {
            if (profiles.Any(p => p.Id == id)) throw new InvalidDataException("A live profile has a pending deletion marker.");
            try { await CleanupAsync(id); }
            catch (Exception exception)
            {
                logger.LogError(exception, "Pending profile cleanup failed for {ProfileId}", id);
                cleanupWarning = "Some previously deleted profile files could not be removed. Close programs using those files and restart to retry.";
            }
        }
        var last = await settings.GetAsync<Guid?>(SettingKeys.LastOpenedProfileId, null, cancellationToken);
        var selected = profiles.FirstOrDefault(p => p.Id == last) ?? ChooseFallback(profiles);
        if (selected is null)
        {
            await settings.SetAsync<Guid?>(SettingKeys.LastOpenedProfileId, null, cancellationToken);
            current.Set(null);
        }
        else await OpenAsync(selected, cancellationToken);
        if (cleanupWarning is not null) throw new ProfileOperationException(cleanupWarning);
        return true;
    }, cancellationToken);

    public Task<Profile> CreateAsync(string name, CancellationToken cancellationToken = default) => RunAsync(async () =>
    {
        var profiles = await repository.GetAllAsync(cancellationToken);
        var validName = ValidateName(name, profiles);
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var profile = new Profile(id, validName, id.ToString("D"), now, now,
            profiles.Count == 0 ? 0 : checked(profiles.Max(p => p.SortOrder) + 1));
        var created = false;
        try
        {
            files.Create(id);
            created = true;
            await workspaces.InitializeAsync(id, create: true, cancellationToken);
            await repository.CreateAndSelectAsync(profile, cancellationToken);
        }
        catch
        {
            if (created)
                try { files.Delete(id); }
                catch (Exception cleanup) { logger.LogError(cleanup, "Creation rollback left unregistered files for {ProfileId}", id); }
            throw;
        }
        current.Set(profile);
        logger.LogInformation("Profile created and selected {ProfileId}", id);
        return profile;
    }, cancellationToken);

    public Task RenameAsync(Guid id, string name, CancellationToken cancellationToken = default) => RunAsync(async () =>
    {
        var profiles = await repository.GetAllAsync(cancellationToken);
        var profile = Find(profiles, id);
        var validName = ValidateName(name, profiles, id);
        await repository.RenameAsync(id, validName, cancellationToken);
        if (current.Current?.Id == id) current.Set(profile with { Name = validName });
        logger.LogInformation("Profile renamed {ProfileId}", id);
        return true;
    }, cancellationToken);

    public Task SwitchAsync(Guid id, CancellationToken cancellationToken = default) => RunAsync(async () =>
    {
        var profile = Find(await repository.GetAllAsync(cancellationToken), id);
        await OpenAsync(profile, cancellationToken);
        return true;
    }, cancellationToken);

    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => RunAsync(async () =>
    {
        var profiles = await repository.GetAllAsync(cancellationToken);
        Find(profiles, id);
        var remaining = profiles.Where(p => p.Id != id).ToArray();
        var replacement = remaining.FirstOrDefault(p => p.Id == current.Current?.Id) ?? ChooseFallback(remaining);
        if (replacement is not null)
        {
            // If the replacement cannot open, leave the original profile and its files untouched.
            await workspaces.InitializeAsync(replacement.Id, create: false, cancellationToken);
            if (current.Current?.Id != replacement.Id) replacement = replacement with { LastOpenedAtUtc = DateTimeOffset.UtcNow };
        }
        await repository.DeleteAndSelectAsync(id, replacement, cancellationToken);
        current.Set(replacement);
        try
        {
            // After the commit, cancellation must not interrupt mandatory file cleanup.
            await CleanupAsync(id);
            logger.LogInformation("Profile deleted {ProfileId}", id);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Profile metadata deleted; file cleanup pending for {ProfileId}", id);
            throw new ProfileOperationException("The profile was removed, but some files could not be deleted. Close programs using those files and restart to retry cleanup.", exception);
        }
        return true;
    }, cancellationToken);

    private async Task CleanupAsync(Guid id)
    {
        files.Delete(id);
        await repository.CompleteDeletionAsync(id, CancellationToken.None);
    }

    private async Task OpenAsync(Profile profile, CancellationToken cancellationToken)
    {
        await workspaces.InitializeAsync(profile.Id, create: false, cancellationToken);
        var opened = profile with { LastOpenedAtUtc = DateTimeOffset.UtcNow };
        await repository.SelectAsync(opened, cancellationToken);
        // Only publish the new context after workspace validation and metadata commit succeed.
        current.Set(opened);
        logger.LogInformation("Profile switched {ProfileId}", profile.Id);
    }

    private static Profile? ChooseFallback(IEnumerable<Profile> profiles) => profiles
        .OrderByDescending(p => p.LastOpenedAtUtc).ThenBy(p => p.SortOrder).ThenBy(p => p.CreatedAtUtc).ThenBy(p => p.Id).FirstOrDefault();

    private static Profile Find(IEnumerable<Profile> profiles, Guid id) => profiles.FirstOrDefault(p => p.Id == id)
        ?? throw new ProfileValidationException("This profile is no longer available.");

    private static string ValidateName(string name, IEnumerable<Profile> profiles, Guid? excluding = null)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ProfileValidationException("Enter a profile name.");
        var trimmed = name.Trim().Normalize(NormalizationForm.FormC);
        if (profiles.Any(p => p.Id != excluding && StringComparer.OrdinalIgnoreCase.Equals(p.Name, trimmed)))
            throw new ProfileValidationException("A profile with that name already exists.");
        return trimmed;
    }

    private async Task<T> RunAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try { return await operation(); }
        catch (ProfileValidationException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (ProfileOperationException) { throw; }
        catch (Exception exception)
        {
            logger.LogError(exception, "Profile operation failed");
            throw new ProfileOperationException("The profile could not be opened or updated. Check access to its local files and try again. Your previous selection has been kept where possible.", exception);
        }
        finally { gate.Release(); }
    }

    public void Dispose() => gate.Dispose();
}
