using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Audiobookshelf.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Audiobookshelf.Api;

/// <summary>
/// Controller for user discovery and mapping between Jellyfin and Audiobookshelf.
/// </summary>
[ApiController]
[Route("Audiobookshelf/[controller]")]
public class UserDiscoveryController : ControllerBase
{
    private readonly UserMappingService _mappingService;
    private readonly TokenVault _tokenVault;
    private readonly ILogger<UserDiscoveryController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserDiscoveryController"/> class.
    /// </summary>
    /// <param name="mappingService">User mapping service.</param>
    /// <param name="tokenVault">Token vault for secure storage.</param>
    /// <param name="logger">Logger instance.</param>
    public UserDiscoveryController(
        UserMappingService mappingService,
        TokenVault tokenVault,
        ILogger<UserDiscoveryController> logger)
    {
        _mappingService = mappingService;
        _tokenVault = tokenVault;
        _logger = logger;
    }

    /// <summary>
    /// Gets the current keyring status.
    /// </summary>
    /// <returns>Keyring status including availability and fallback settings.</returns>
    [HttpGet("KeyringStatus")]
    public ActionResult<KeyringStatusResponse> GetKeyringStatus()
    {
        return Ok(new KeyringStatusResponse
        {
            IsKeyringAvailable = _tokenVault.IsKeyringAvailable,
            RequiresUserConfirmation = _tokenVault.RequiresUserConfirmation,
            UsePluginConfigFallback = _tokenVault.HasUserApprovedFallback
        });
    }

    /// <summary>
    /// Approves fallback storage when keyring is unavailable.
    /// </summary>
    [HttpPost("ApproveFallback")]
    public ActionResult ApproveFallbackStorage()
    {
        _tokenVault.HasUserApprovedFallback = true;
        _logger.LogInformation("User approved fallback token storage in plugin config");
        return Ok();
    }

    /// <summary>
    /// Discovers and matches users between Jellyfin and Audiobookshelf.
    /// </summary>
    /// <param name="adminToken">Admin API token for ABS.</param>
    /// <param name="serverUrl">ABS server URL.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of user matches with keyring status.</returns>
    [HttpGet("Discover")]
    public async Task<ActionResult<UserDiscoveryResponse>> DiscoverUsers(
        [FromQuery] string adminToken,
        [FromQuery] string serverUrl,
        CancellationToken ct = default)
    {
        var matches = await _mappingService.DiscoverUsersAsync(adminToken, serverUrl, ct);

        return Ok(new UserDiscoveryResponse
        {
            Matches = ConvertToDto(matches),
            IsKeyringAvailable = _tokenVault.IsKeyringAvailable,
            RequiresUserConfirmation = _tokenVault.RequiresUserConfirmation
        });
    }

    /// <summary>
    /// Saves the selected user mappings.
    /// </summary>
    /// <param name="request">Request containing matches to save.</param>
    /// <returns>Number of mappings saved.</returns>
    [HttpPost("SaveMappings")]
    public async Task<ActionResult<SaveMappingsResponse>> SaveMappings([FromBody] SaveMappingsRequest request)
    {
        var matches = ConvertFromDto(request.Matches);
        var saved = await _mappingService.SaveMappingsAsync(matches);

        return Ok(new SaveMappingsResponse { SavedCount = saved });
    }

    private List<UserMatchDto> ConvertToDto(List<UserMatch> matches)
    {
        var dtos = new List<UserMatchDto>();

        foreach (var match in matches)
        {
            var dto = new UserMatchDto
            {
                IsSelected = match.IsSelected,
                MatchType = match.MatchType.ToString().ToLowerInvariant()
            };

            if (match.JellyfinUser != null)
            {
                dto.JellyfinUserId = match.JellyfinUser.Id;
                dto.JellyfinUsername = match.JellyfinUser.Username;
            }

            if (match.AbsUser != null)
            {
                dto.AbsUserId = match.AbsUser.Id;
                dto.AbsUsername = match.AbsUser.Username;
            }

            dtos.Add(dto);
        }

        return dtos;
    }

    private List<UserMatch> ConvertFromDto(List<UserMatchDto> dtos)
    {
        var matches = new List<UserMatch>();

        foreach (var dto in dtos)
        {
            var match = new UserMatch
            {
                IsSelected = dto.IsSelected,
                MatchType = dto.MatchType.ToLowerInvariant() switch
                {
                    "exact" => UserMatchType.Exact,
                    "fuzzy" => UserMatchType.Fuzzy,
                    _ => UserMatchType.None
                }
            };

            if (!string.IsNullOrEmpty(dto.JellyfinUserId))
            {
                match.JellyfinUser = new JellyfinUserInfo
                {
                    Id = dto.JellyfinUserId,
                    Username = dto.JellyfinUsername
                };
            }

            if (!string.IsNullOrEmpty(dto.AbsUserId))
            {
                match.AbsUser = new Api.Models.AbsUser
                {
                    Id = dto.AbsUserId,
                    Username = dto.AbsUsername
                };
            }

            matches.Add(match);
        }

        return matches;
    }
}

/// <summary>
/// Response containing user discovery results.
/// </summary>
public class UserDiscoveryResponse
{
    /// <summary>
    /// Gets or sets the list of user matches.
    /// </summary>
    public List<UserMatchDto> Matches { get; set; } = new();

    /// <summary>
    /// Gets or sets a value indicating whether the system keyring is available.
    /// </summary>
    public bool IsKeyringAvailable { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether user confirmation is required for fallback storage.
    /// </summary>
    public bool RequiresUserConfirmation { get; set; }
}

/// <summary>
/// DTO for a user match between Jellyfin and ABS.
/// </summary>
public class UserMatchDto
{
    /// <summary>
    /// Gets or sets the Jellyfin user ID.
    /// </summary>
    public string JellyfinUserId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Jellyfin username.
    /// </summary>
    public string JellyfinUsername { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the ABS user ID.
    /// </summary>
    public string AbsUserId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the ABS username.
    /// </summary>
    public string AbsUsername { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the match type (exact, fuzzy, none).
    /// </summary>
    public string MatchType { get; set; } = "none";

    /// <summary>
    /// Gets or sets a value indicating whether this match is selected for linking.
    /// </summary>
    public bool IsSelected { get; set; }
}

/// <summary>
/// Request to save user mappings.
/// </summary>
public class SaveMappingsRequest
{
    /// <summary>
    /// Gets or sets the list of matches to save.
    /// </summary>
    public List<UserMatchDto> Matches { get; set; } = new();
}

/// <summary>
/// Response containing the number of mappings saved.
/// </summary>
public class SaveMappingsResponse
{
    /// <summary>
    /// Gets or sets the number of mappings saved.
    /// </summary>
    public int SavedCount { get; set; }
}

/// <summary>
/// Response containing keyring status information.
/// </summary>
public class KeyringStatusResponse
{
    /// <summary>
    /// Gets or sets a value indicating whether the system keyring is available.
    /// </summary>
    public bool IsKeyringAvailable { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether user confirmation is required for fallback storage.
    /// </summary>
    public bool RequiresUserConfirmation { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether plugin config fallback is enabled.
    /// </summary>
    public bool UsePluginConfigFallback { get; set; }
}
