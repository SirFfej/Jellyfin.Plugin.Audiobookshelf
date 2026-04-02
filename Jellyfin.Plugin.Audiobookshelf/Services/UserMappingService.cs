using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Audiobookshelf.Api;
using Jellyfin.Plugin.Audiobookshelf.Api.Models;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Audiobookshelf.Services;

/// <summary>
/// Represents a potential match between a Jellyfin user and an ABS user.
/// </summary>
public class UserMatch
{
    /// <summary>
    /// Gets or sets the Jellyfin user.
    /// </summary>
    public JellyfinUserInfo? JellyfinUser { get; set; }

    /// <summary>
    /// Gets or sets the Audiobookshelf user.
    /// </summary>
    public AbsUser? AbsUser { get; set; }

    /// <summary>
    /// Gets or sets the match type.
    /// </summary>
    public UserMatchType MatchType { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this match is selected for linking.
    /// </summary>
    public bool IsSelected { get; set; }
}

/// <summary>
/// The type of user match.
/// </summary>
public enum UserMatchType
{
    /// <summary>
    /// Exact username match (case-insensitive).
    /// </summary>
    Exact,

    /// <summary>
    /// Fuzzy username match.
    /// </summary>
    Fuzzy,

    /// <summary>
    /// No match found.
    /// </summary>
    None
}

/// <summary>
/// Information about a Jellyfin user for display.
/// </summary>
public class JellyfinUserInfo
{
    /// <summary>
    /// Gets or sets the user ID.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the username.
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the display name.
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;
}

/// <summary>
/// Service for discovering and mapping users between Jellyfin and Audiobookshelf.
/// </summary>
public class UserMappingService
{
    private readonly IUserManager _userManager;
    private readonly AbsApiClientFactory _clientFactory;
    private readonly TokenVault _tokenVault;
    private readonly ILogger<UserMappingService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserMappingService"/> class.
    /// </summary>
    /// <param name="userManager">Jellyfin user manager.</param>
    /// <param name="clientFactory">ABS API client factory.</param>
    /// <param name="tokenVault">Token vault for secure storage.</param>
    /// <param name="logger">Logger instance.</param>
    public UserMappingService(
        IUserManager userManager,
        AbsApiClientFactory clientFactory,
        TokenVault tokenVault,
        ILogger<UserMappingService> logger)
    {
        _userManager = userManager;
        _clientFactory = clientFactory;
        _tokenVault = tokenVault;
        _logger = logger;
    }

    /// <summary>
    /// Discovers and matches users between Jellyfin and Audiobookshelf.
    /// </summary>
    /// <param name="adminToken">Admin API token for ABS.</param>
    /// <param name="serverUrl">ABS server URL.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of user matches with match types.</returns>
    public async Task<List<UserMatch>> DiscoverUsersAsync(string adminToken, string serverUrl, CancellationToken ct = default)
    {
        var jellyfinUsers = await GetJellyfinUsersAsync(ct).ConfigureAwait(false);
        var absUsers = await GetAbsUsersAsync(adminToken, serverUrl, ct).ConfigureAwait(false);

        return MatchUsers(jellyfinUsers, absUsers);
    }

    /// <summary>
    /// Saves the selected user mappings to the token vault.
    /// </summary>
    /// <param name="matches">The user matches to save.</param>
    /// <returns>Number of mappings saved.</returns>
    public async Task<int> SaveMappingsAsync(List<UserMatch> matches)
    {
        int saved = 0;
        foreach (var match in matches.Where(m => m.IsSelected && m.JellyfinUser != null && m.AbsUser?.Token != null))
        {
            var success = await _tokenVault.StoreTokenAsync(match.JellyfinUser!.Id, match.AbsUser!.Token!).ConfigureAwait(false);
            if (success)
            {
                saved++;
                _logger.LogInformation("Linked user {JellyfinUser} to ABS user {AbsUser}",
                    match.JellyfinUser.Username, match.AbsUser.Username);
            }
        }

        return saved;
    }

    private Task<List<JellyfinUserInfo>> GetJellyfinUsersAsync(CancellationToken ct)
    {
        var jellyfinUsers = _userManager.Users
            .Select(u => new JellyfinUserInfo
            {
                Id = u.Id.ToString(),
                Username = u.Username ?? string.Empty,
                DisplayName = u.Username ?? string.Empty
            })
            .ToList();

        return Task.FromResult(jellyfinUsers);
    }

    private async Task<List<AbsUser>> GetAbsUsersAsync(string adminToken, string serverUrl, CancellationToken ct)
    {
        try
        {
            var client = _clientFactory.GetClientForToken(adminToken);
            var response = await client.GetAllUsersAsync(ct).ConfigureAwait(false);
            return response?.Users?.ToList() ?? new List<AbsUser>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch users from Audiobookshelf");
            return new List<AbsUser>();
        }
    }

    private List<UserMatch> MatchUsers(List<JellyfinUserInfo> jellyfinUsers, List<AbsUser> absUsers)
    {
        var matches = new List<UserMatch>();
        var matchedAbsUsers = new HashSet<string>();

        foreach (var jfUser in jellyfinUsers)
        {
            var match = new UserMatch { JellyfinUser = jfUser };

            var exactMatch = absUsers.FirstOrDefault(u =>
                string.Equals(u.Username, jfUser.Username, StringComparison.OrdinalIgnoreCase));

            if (exactMatch != null && !matchedAbsUsers.Contains(exactMatch.Id))
            {
                match.AbsUser = exactMatch;
                match.MatchType = UserMatchType.Exact;
                match.IsSelected = true;
                matchedAbsUsers.Add(exactMatch.Id);
            }
            else
            {
                match.MatchType = UserMatchType.None;
            }

            matches.Add(match);
        }

        foreach (var absUser in absUsers.Where(u => !matchedAbsUsers.Contains(u.Id)))
        {
            matches.Add(new UserMatch
            {
                AbsUser = absUser,
                MatchType = UserMatchType.None
            });
        }

        return matches;
    }
}
