using System.Text.Json.Serialization;
using ClubRuns.App.Config;
using Microsoft.Extensions.Options;

namespace ClubRuns.App.Services;

public sealed class StravaApiClient(HttpClient httpClient, IOptions<AppOptions> options, RuntimeSettingsService runtimeSettings)
{
    private readonly AppOptions _options = options.Value;

    public async Task<string> BuildAuthorizeUrlAsync(string state, CancellationToken ct)
    {
        var clientId = await runtimeSettings.GetStravaClientIdAsync(ct);
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new InvalidOperationException("Strava ClientId is not configured. Use /setstrava or environment variables.");
        }

        // Scope is configurable: read or read_all for private activities.
        var scope = _options.Strava.UseReadAllScope ? "activity:read_all" : "activity:read";
        return $"https://www.strava.com/oauth/authorize?client_id={Uri.EscapeDataString(clientId)}&redirect_uri={Uri.EscapeDataString(_options.Strava.RedirectUri)}&response_type=code&approval_prompt=auto&scope={Uri.EscapeDataString(scope)}&state={Uri.EscapeDataString(state)}";
    }

    public async Task<TokenResponse> ExchangeCodeAsync(string code, CancellationToken ct)
    {
        var creds = await GetCredentialsAsync(ct);

        // OAuth code exchange (authorization_code grant).
        var payload = new Dictionary<string, string>
        {
            ["client_id"] = creds.clientId,
            ["client_secret"] = creds.clientSecret,
            ["code"] = code,
            ["grant_type"] = "authorization_code"
        };

        using var content = new FormUrlEncodedContent(payload);
        using var response = await httpClient.PostAsync("https://www.strava.com/oauth/token", content, ct);
        // Throwing here keeps error flow explicit for caller-level per-user handling.
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: ct))!;
    }

    public async Task<TokenResponse> RefreshTokenAsync(string refreshToken, CancellationToken ct)
    {
        var creds = await GetCredentialsAsync(ct);

        // OAuth token refresh; Strava can return a rotated refresh token.
        var payload = new Dictionary<string, string>
        {
            ["client_id"] = creds.clientId,
            ["client_secret"] = creds.clientSecret,
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token"
        };

        using var content = new FormUrlEncodedContent(payload);
        using var response = await httpClient.PostAsync("https://www.strava.com/oauth/token", content, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: ct))!;
    }

    public async Task<List<ActivityResponse>> GetActivitiesAsync(string accessToken, long afterUnix, long beforeUnix, CancellationToken ct)
    {
        // Use unix boundaries to query only the configured morning window.
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://www.strava.com/api/v3/athlete/activities?after={afterUnix}&before={beforeUnix}&per_page=200&page=1");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var items = await response.Content.ReadFromJsonAsync<List<ActivityResponse>>(cancellationToken: ct);
        return items ?? [];
    }

    private async Task<(string clientId, string clientSecret)> GetCredentialsAsync(CancellationToken ct)
    {
        var clientId = await runtimeSettings.GetStravaClientIdAsync(ct);
        var clientSecret = await runtimeSettings.GetStravaClientSecretAsync(ct);

        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            throw new InvalidOperationException("Strava credentials are not configured. Use /setstrava or environment variables.");
        }

        return (clientId, clientSecret);
    }
}

public sealed class TokenResponse
{
    // DTOs map Strava JSON directly and are used by repository/job services.
    [JsonPropertyName("token_type")] public string TokenType { get; set; } = string.Empty;
    [JsonPropertyName("access_token")] public string AccessToken { get; set; } = string.Empty;
    [JsonPropertyName("refresh_token")] public string RefreshToken { get; set; } = string.Empty;
    [JsonPropertyName("expires_at")] public long ExpiresAt { get; set; }
    [JsonPropertyName("scope")] public string Scope { get; set; } = string.Empty;
    [JsonPropertyName("athlete")] public AthleteResponse Athlete { get; set; } = new();
}

public sealed class AthleteResponse
{
    [JsonPropertyName("id")] public long Id { get; set; }
}

public sealed class ActivityResponse
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
    [JsonPropertyName("distance")] public double DistanceMeters { get; set; }
    [JsonPropertyName("start_date_local")] public string StartDateLocal { get; set; } = string.Empty;
    [JsonPropertyName("start_latlng")] public List<double>? StartLatLng { get; set; }
}
