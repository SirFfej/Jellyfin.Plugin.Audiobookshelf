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
        _config.UserTokenMap[jellyfinUserId] = absToken;
        _logger.LogDebug("Stored ABS token for user {UserId}", jellyfinUserId);
        return Task.FromResult(true);
    }

    /// <summary>
    /// Retrieves the ABS API token for a Jellyfin user, or <c>null</c> if not mapped.
    /// </summary>
    public Task<string?> GetTokenAsync(string jellyfinUserId)
    {
        return Task.FromResult(
            _config.UserTokenMap.TryGetValue(jellyfinUserId, out var token) ? token : null);
    }

    /// <summary>
    /// Removes the ABS API token for a Jellyfin user.
    /// </summary>
    public Task DeleteTokenAsync(string jellyfinUserId)
    {
        _config.UserTokenMap.Remove(jellyfinUserId);
        _logger.LogDebug("Deleted ABS token for user {UserId}", jellyfinUserId);
        return Task.CompletedTask;
    }
}
