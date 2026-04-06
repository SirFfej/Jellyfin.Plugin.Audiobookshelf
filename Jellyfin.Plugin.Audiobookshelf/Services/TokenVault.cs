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
    private readonly PluginConfiguration _config;
    private readonly ILogger<TokenVault> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TokenVault"/> class.
    /// </summary>
    public TokenVault(PluginConfiguration config, ILogger<TokenVault> logger)
    {
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Stores an ABS API token for a Jellyfin user.
    /// </summary>
    public Task<bool> StoreTokenAsync(string jellyfinUserId, string absToken)
    {
        var existing = _config.UserTokenEntries.FirstOrDefault(e => e.UserId == jellyfinUserId);
        if (existing != null)
        {
            existing.Token = absToken;
        }
        else
        {
            _config.UserTokenEntries.Add(new UserTokenEntry { UserId = jellyfinUserId, Token = absToken });
        }

        _logger.LogDebug("Stored ABS token for user {UserId}", jellyfinUserId);
        return Task.FromResult(true);
    }

    /// <summary>
    /// Retrieves the ABS API token for a Jellyfin user, or <c>null</c> if not mapped.
    /// </summary>
    public Task<string?> GetTokenAsync(string jellyfinUserId)
    {
        var entry = _config.UserTokenEntries.FirstOrDefault(e => e.UserId == jellyfinUserId);
        return Task.FromResult(entry?.Token);
    }

    /// <summary>
    /// Removes the ABS API token for a Jellyfin user.
    /// </summary>
    public Task DeleteTokenAsync(string jellyfinUserId)
    {
        _config.UserTokenEntries.RemoveAll(e => e.UserId == jellyfinUserId);
        _logger.LogDebug("Deleted ABS token for user {UserId}", jellyfinUserId);
        return Task.CompletedTask;
    }
}
