using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Audiobookshelf.Services;

/// <summary>
/// Token storage backed by plugin configuration.
/// Tokens are stored in Jellyfin's standard plugin config file (XML, on-disk).
/// A system keyring is not used because the plugin runs inside a Docker container
/// where no keyring daemon or D-Bus session is available.
/// </summary>
public class TokenVault
{
    private readonly ILogger<TokenVault> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TokenVault"/> class.
    /// </summary>
    public TokenVault(ILogger<TokenVault> logger)
    {
        _logger = logger;
    }

    // Always access Plugin.Instance.Configuration directly — PluginConfiguration is not
    // registered in the DI container, so constructor injection would yield a detached
    // default instance that is never saved.
    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    /// <summary>
    /// Stores an ABS API token for a Jellyfin user.
    /// </summary>
    public Task<bool> StoreTokenAsync(string jellyfinUserId, string absToken)
    {
        var existing = Config.UserTokenEntries.FirstOrDefault(e => e.UserId == jellyfinUserId);
        if (existing != null)
        {
            existing.Token = absToken;
        }
        else
        {
            Config.UserTokenEntries.Add(new UserTokenEntry { UserId = jellyfinUserId, Token = absToken });
        }

        Plugin.Instance!.SaveConfiguration();
        _logger.LogDebug("Stored ABS token for user {UserId}", jellyfinUserId);
        return Task.FromResult(true);
    }

    /// <summary>
    /// Retrieves the ABS API token for a Jellyfin user, or <c>null</c> if not mapped.
    /// </summary>
    public Task<string?> GetTokenAsync(string jellyfinUserId)
    {
        var entry = Config.UserTokenEntries.FirstOrDefault(e => e.UserId == jellyfinUserId);
        return Task.FromResult(entry?.Token);
    }

    /// <summary>
    /// Removes the ABS API token for a Jellyfin user.
    /// </summary>
    public Task DeleteTokenAsync(string jellyfinUserId)
    {
        Config.UserTokenEntries.RemoveAll(e => e.UserId == jellyfinUserId);
        Plugin.Instance!.SaveConfiguration();
        _logger.LogDebug("Deleted ABS token for user {UserId}", jellyfinUserId);
        return Task.CompletedTask;
    }
}
