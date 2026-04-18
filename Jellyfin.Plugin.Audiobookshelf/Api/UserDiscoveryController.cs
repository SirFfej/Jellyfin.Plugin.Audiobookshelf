using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Audiobookshelf.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Audiobookshelf.Api;

/// <summary>
/// Controller for user discovery and mapping between Jellyfin and Audiobookshelf.
/// </summary>
[ApiController]
[Authorize]
[Route("Audiobookshelf/[controller]")]
public class UserDiscoveryController : ControllerBase
{
    private readonly UserMappingService _mappingService;
    private readonly TokenVault _tokenVault;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<UserDiscoveryController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserDiscoveryController"/> class.
    /// </summary>
    /// <param name="mappingService">User mapping service.</param>
    /// <param name="tokenVault">Token vault for secure storage.</param>
    /// <param name="httpClientFactory">HTTP client factory for proxied requests.</param>
    /// <param name="libraryManager">Library manager for Jellyfin libraries.</param>
    /// <param name="logger">Logger instance.</param>
    public UserDiscoveryController(
        UserMappingService mappingService,
        TokenVault tokenVault,
        IHttpClientFactory httpClientFactory,
        ILibraryManager libraryManager,
        ILogger<UserDiscoveryController> logger)
    {
        _mappingService = mappingService;
        _tokenVault = tokenVault;
        _httpClientFactory = httpClientFactory;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>
    /// Proxies a connection test to an Audiobookshelf server.
    /// The token is read from the <c>X-Abs-Token</c> request header so it is never
    /// exposed in the browser's network inspector as a direct outbound call to ABS.
    /// </summary>
    /// <param name="serverUrl">ABS server URL (query string).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Username on success, error message on failure.</returns>
    [HttpGet("TestConnection")]
    public async Task<ActionResult<TestConnectionResponse>> TestConnection(
        [FromQuery] string serverUrl,
        CancellationToken ct = default)
    {
        var token = Request.Headers["X-Abs-Token"].ToString();
        if (string.IsNullOrWhiteSpace(token))
        {
            return BadRequest("X-Abs-Token header is required");
        }

        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            return BadRequest("serverUrl query parameter is required");
        }

        string normalizedUrl = serverUrl.TrimEnd('/');

        try
        {
            var httpClient = _httpClientFactory.CreateClient(AbsApiClient.HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, normalizedUrl + "/api/me");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await httpClient.SendAsync(request, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return Ok(new TestConnectionResponse
                {
                    Success = false,
                    Error = $"ABS returned HTTP {(int)response.StatusCode}"
                });
            }

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            string username = doc.RootElement.TryGetProperty("username", out var u) ? u.GetString() ?? string.Empty : string.Empty;

            return Ok(new TestConnectionResponse { Success = true, Username = username });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ABS connection test failed for {Url}", normalizedUrl);
            return Ok(new TestConnectionResponse { Success = false, Error = "Connection failed — check URL and token" });
        }
    }

    /// <summary>
    /// Discovers and matches users between Jellyfin and Audiobookshelf.
    /// The ABS admin token is read from the <c>X-Abs-Token</c> request header to
    /// avoid logging it in Jellyfin's access log.
    /// </summary>
    /// <param name="serverUrl">ABS server URL (query string).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of user matches.</returns>
    [HttpGet("Discover")]
    public async Task<ActionResult<UserDiscoveryResponse>> DiscoverUsers(
        [FromQuery] string serverUrl,
        CancellationToken ct = default)
    {
        var adminToken = Request.Headers["X-Abs-Token"].ToString();
        if (string.IsNullOrWhiteSpace(adminToken))
        {
            return BadRequest("X-Abs-Token header is required");
        }

        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            return BadRequest("serverUrl query parameter is required");
        }

        var matches = await _mappingService.DiscoverUsersAsync(adminToken, serverUrl.Trim(), ct);
        return Ok(new UserDiscoveryResponse { Matches = ConvertToDto(matches) });
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

    /// <summary>
    /// Gets the Jellyfin libraries that can be linked to Audiobookshelf.
    /// Returns book and music libraries with their media type.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of Jellyfin libraries.</returns>
    [HttpGet("Libraries")]
    public ActionResult<GetLibrariesResponse> GetLibraries(CancellationToken ct = default)
    {
        try
        {
            var libraries = _libraryManager.GetVirtualFolders()
                .Where(lf => lf.CollectionType == CollectionTypeOptions.books)
                .Select(lf => new LibraryDto
                {
                    Id = lf.ItemId.ToString(),
                    Name = lf.Name ?? string.Empty,
                    MediaType = "book"
                })
                .Concat(_libraryManager.GetVirtualFolders()
                    .Where(lf => lf.CollectionType == CollectionTypeOptions.music)
                    .Select(lf => new LibraryDto
                    {
                        Id = lf.ItemId.ToString(),
                        Name = lf.Name ?? string.Empty,
                        MediaType = "audio"
                    }))
                .ToList();

            return Ok(new GetLibrariesResponse { Success = true, Libraries = libraries });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get Jellyfin libraries");
            return Ok(new GetLibrariesResponse { Success = false, Error = "Failed to fetch libraries" });
        }
    }

    /// <summary>
    /// Gets all Jellyfin library IDs that match a given media type.
    /// Used by sync tasks to filter by selected library.
    /// </summary>
    /// <param name="mediaType">The media type filter ("book" or "audio").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of library IDs.</returns>
    [HttpGet("LibraryIds")]
    public ActionResult<GetLibraryIdsResponse> GetLibraryIds(
        [FromQuery] string mediaType,
        CancellationToken ct = default)
    {
        try
        {
            var collectionType = string.Equals(mediaType, "audio", StringComparison.OrdinalIgnoreCase)
                ? CollectionTypeOptions.music
                : CollectionTypeOptions.books;

            var ids = _libraryManager.GetVirtualFolders()
                .Where(lf => lf.CollectionType == collectionType)
                .Select(lf => lf.ItemId.ToString())
                .ToList();

            return Ok(new GetLibraryIdsResponse { LibraryIds = ids });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get library IDs for media type {MediaType}", mediaType);
            return Ok(new GetLibraryIdsResponse { LibraryIds = new List<string>() });
        }
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
                dto.AbsUsername = match.AbsUser.Username ?? string.Empty;
                dto.AbsToken = match.AbsUser.Token;
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
                    Username = dto.AbsUsername,
                    Token = dto.AbsToken
                };
            }

            matches.Add(match);
        }

        return matches;
    }
}

/// <summary>
/// Response from the TestConnection proxy endpoint.
/// </summary>
public class TestConnectionResponse
{
    /// <summary>
    /// Gets or sets a value indicating whether the connection succeeded.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets the ABS username returned by <c>GET /api/me</c> on success.
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a human-readable error message on failure.
    /// </summary>
    public string? Error { get; set; }
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
    /// Gets or sets the ABS API token for the matched ABS user.
    /// Only populated when the admin token grants access to user tokens.
    /// </summary>
    public string? AbsToken { get; set; }

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
/// Response containing available Audiobookshelf libraries.
/// </summary>
public class GetLibrariesResponse
{
    /// <summary>
    /// Gets or sets a value indicating whether the request succeeded.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets the list of available libraries.
    /// </summary>
    public List<LibraryDto> Libraries { get; set; } = new();

    /// <summary>
    /// Gets or sets an error message on failure.
    /// </summary>
    public string? Error { get; set; }
}

/// <summary>
/// DTO for an Audiobookshelf library.
/// </summary>
public class LibraryDto
{
    /// <summary>
    /// Gets or sets the library ID.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the library name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the media type (e.g., "book", "podcast").
    /// </summary>
    public string MediaType { get; set; } = string.Empty;
}

/// <summary>
/// Response containing library IDs for a media type.
/// </summary>
public class GetLibraryIdsResponse
{
    /// <summary>
    /// Gets or sets the list of library IDs.
    /// </summary>
    public List<string> LibraryIds { get; set; } = new();
}

