using ClubRuns.App.Config;
using ClubRuns.App.Data;
using Microsoft.Extensions.Options;

namespace ClubRuns.App.Services;

public sealed class RuntimeSettingsService(SqliteRepository repository, IOptions<AppOptions> options)
{
    private readonly AppOptions _options = options.Value;

    public async Task<string?> GetStravaClientIdAsync(CancellationToken ct = default)
    {
        var value = await repository.GetSettingAsync("strava.client_id", ct);
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return string.IsNullOrWhiteSpace(_options.Strava.ClientId) ? null : _options.Strava.ClientId;
    }

    public async Task<string?> GetStravaClientSecretAsync(CancellationToken ct = default)
    {
        var value = await repository.GetSettingAsync("strava.client_secret", ct);
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return string.IsNullOrWhiteSpace(_options.Strava.ClientSecret) ? null : _options.Strava.ClientSecret;
    }

    public async Task SetStravaCredentialsAsync(string clientId, string clientSecret, CancellationToken ct = default)
    {
        await repository.UpsertSettingAsync("strava.client_id", clientId, ct);
        await repository.UpsertSettingAsync("strava.client_secret", clientSecret, ct);
    }
}
