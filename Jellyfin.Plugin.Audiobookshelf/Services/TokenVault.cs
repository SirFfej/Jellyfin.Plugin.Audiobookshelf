using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using KeySharp;

namespace Jellyfin.Plugin.Audiobookshelf.Services;

/// <summary>
/// Secure token storage wrapper that attempts system keyring storage first,
/// with fallback to plugin config requiring explicit user approval.
/// </summary>
public class TokenVault
{
    private const string ServiceName = "Jellyfin.Plugin.Audiobookshelf";
    private const string KeyringKey = "abs-token";

    private readonly PluginConfiguration _config;
    private readonly ILogger<TokenVault> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TokenVault"/> class.
    /// </summary>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="logger">Logger instance.</param>
    public TokenVault(PluginConfiguration config, ILogger<TokenVault> logger)
    {
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Gets a value indicating whether the system keyring is available.
    /// </summary>
    public bool IsKeyringAvailable { get; private set; }

    /// <summary>
    /// Gets a value indicating whether user confirmation is required before using fallback storage.
    /// True when keyring is unavailable and user hasn't approved plugin config fallback.
    /// </summary>
    public bool RequiresUserConfirmation => !IsKeyringAvailable && !_config.UsePluginConfigFallback;

    /// <summary>
    /// Gets or sets whether the user has approved storing tokens in plugin config as fallback.
    /// </summary>
    public bool HasUserApprovedFallback
    {
        get => _config.UsePluginConfigFallback;
        set => _config.UsePluginConfigFallback = value;
    }

    /// <summary>
    /// Checks if the system keyring is available on this platform.
    /// </summary>
    public void CheckKeyringAvailability()
    {
        try
        {
            Keyring.GetPassword(ServiceName, KeyringKey, "availability-check");
            IsKeyringAvailable = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "System keyring unavailable. Tokens will require fallback storage.");
            IsKeyringAvailable = false;
        }
    }

    /// <summary>
    /// Stores an ABS API token for a user.
    /// </summary>
    /// <param name="jellyfinUserId">The Jellyfin user ID.</param>
    /// <param name="absToken">The ABS API token.</param>
    /// <returns>True if stored successfully, false otherwise.</returns>
    public Task<bool> StoreTokenAsync(string jellyfinUserId, string absToken)
    {
        if (IsKeyringAvailable)
        {
            return StoreInKeyringAsync(jellyfinUserId, absToken);
        }
        else if (_config.UsePluginConfigFallback)
        {
            return StoreInConfigAsync(jellyfinUserId, absToken);
        }
        else
        {
            _logger.LogError("Attempted to store token without user approval for fallback storage");
            return Task.FromResult(false);
        }
    }

    /// <summary>
    /// Retrieves an ABS API token for a user.
    /// </summary>
    /// <param name="jellyfinUserId">The Jellyfin user ID.</param>
    /// <returns>The ABS token if found, null otherwise.</returns>
    public Task<string?> GetTokenAsync(string jellyfinUserId)
    {
        if (IsKeyringAvailable)
        {
            return GetFromKeyringAsync(jellyfinUserId);
        }
        else if (_config.UsePluginConfigFallback)
        {
            return GetFromConfigAsync(jellyfinUserId);
        }
        else
        {
            _logger.LogWarning("Attempted to get token but no storage method is configured");
            return Task.FromResult<string?>(null);
        }
    }

    /// <summary>
    /// Deletes an ABS API token for a user.
    /// </summary>
    /// <param name="jellyfinUserId">The Jellyfin user ID.</param>
    public Task DeleteTokenAsync(string jellyfinUserId)
    {
        if (IsKeyringAvailable)
        {
            return DeleteFromKeyringAsync(jellyfinUserId);
        }
        else if (_config.UsePluginConfigFallback)
        {
            return DeleteFromConfigAsync(jellyfinUserId);
        }
        else
        {
            _logger.LogWarning("Attempted to delete token but no storage method is configured");
            return Task.CompletedTask;
        }
    }

    private Task<bool> StoreInKeyringAsync(string jellyfinUserId, string absToken)
    {
        try
        {
            Keyring.SetPassword(ServiceName, KeyringKey, jellyfinUserId, absToken);
            _logger.LogDebug("Stored ABS token for user {UserId} in system keyring", jellyfinUserId);
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to store token in system keyring");
            IsKeyringAvailable = false;
            return Task.FromResult(false);
        }
    }

    private Task<string?> GetFromKeyringAsync(string jellyfinUserId)
    {
        try
        {
            var token = Keyring.GetPassword(ServiceName, KeyringKey, jellyfinUserId);
            return Task.FromResult<string?>(token);
        }
        catch (KeyringException)
        {
            return Task.FromResult<string?>(null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve token from system keyring");
            return Task.FromResult<string?>(null);
        }
    }

    private Task DeleteFromKeyringAsync(string jellyfinUserId)
    {
        try
        {
            Keyring.DeletePassword(ServiceName, KeyringKey, jellyfinUserId);
            _logger.LogDebug("Deleted ABS token for user {UserId} from system keyring", jellyfinUserId);
        }
        catch (KeyringException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete token from system keyring");
        }

        return Task.CompletedTask;
    }

    private Task<bool> StoreInConfigAsync(string jellyfinUserId, string absToken)
    {
        _config.UserTokenMap[jellyfinUserId] = absToken;
        _logger.LogDebug("Stored ABS token for user {UserId} in plugin config", jellyfinUserId);
        return Task.FromResult(true);
    }

    private Task<string?> GetFromConfigAsync(string jellyfinUserId)
    {
        return Task.FromResult(_config.UserTokenMap.TryGetValue(jellyfinUserId, out var token) ? token : null);
    }

    private Task DeleteFromConfigAsync(string jellyfinUserId)
    {
        _config.UserTokenMap.Remove(jellyfinUserId);
        _logger.LogDebug("Deleted ABS token for user {UserId} from plugin config", jellyfinUserId);
        return Task.CompletedTask;
    }
}
