using Microsoft.Extensions.DependencyInjection;
using Vigo.Core.Git;
using Vigo.Core.Services;
using Vigo.Git.Cli;
using Vigo.Git.Signing;
using Vigo.Git.Ssh;
using Vigo.Git.Watching;

namespace Vigo.Git;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddVigoGit(this IServiceCollection services)
    {
        // The SSH settings of the preferences apply to every network command (read at each one: changes apply at once);
        // so does the Git profile of the repository (identity, signing), resolved from its folder (J6.7, ADR-051).
        services.AddSingleton(sp =>
        {
            var preferences = sp.GetService<Preferences>();
            var profiles = sp.GetService<GitProfiles>();
            return new GitCli
            {
                SshSettings = preferences is null ? null : () => preferences.Current.Ssh,
                ProfileFor = profiles is null ? null : profiles.ResolveFor,
                CommandLog = sp.GetService<GitCommandLog>(),
                HttpCredentials = sp.GetService<IGitHttpCredentials>(),
            };
        });
        services.AddSingleton<IGitService, GitService>();
        services.AddSingleton<ISshKeyService, SshKeyService>(sp => new SshKeyService(sp.GetRequiredService<GitCli>()));
        services.AddSingleton<IGpgKeyService, GpgKeyService>(sp => new GpgKeyService(sp.GetRequiredService<GitCli>()));
        services.AddSingleton<IRepositoryWatcherFactory, RepositoryWatcherFactory>();
        return services;
    }
}
