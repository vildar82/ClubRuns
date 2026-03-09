using Microsoft.Extensions.Options;
using Serilog;
using Telegram.Bot;
using TRC_Bot;

var builder = WebApplication.CreateBuilder(args);

// Single runtime mode: long-running Telegram bot + OAuth callback endpoint + scheduler.
ConfigureConfiguration(builder.Configuration);
ConfigureSerilog(builder.Configuration);

builder.Host.UseSerilog();
RegisterCoreServices(builder.Services, builder.Configuration);
builder.Services.AddHostedService<TelegramBotHostedService>();
builder.Services.AddHostedService<SchedulerService>();
builder.Services.AddTransient<AttendanceJobService>();
builder.Services.AddTransient<LegacyStatsImporterService>();
builder.Services.AddTransient<LeaderboardService>();
builder.Services.AddSingleton<RuntimeSettingsService>();

var app = builder.Build();

// Ensure SQLite schema exists before receiving commands or OAuth callbacks.
var repository = app.Services.GetRequiredService<SqliteRepository>();
await repository.InitializeAsync();

// Seed bootstrap admins from config; dynamic additions are stored in DB.
var appOptions = app.Services.GetRequiredService<IOptions<AppOptions>>().Value;
await repository.EnsureAdminsAsync(appOptions.Telegram.AdminTelegramUserIds);

ConfigureListenFromRedirectUri(app);
app.MapGet("/", () => Results.Text("ClubRuns is running."));
app.MapStravaEndpoints();

await app.RunAsync();

static void ConfigureConfiguration(ConfigurationManager configuration)
{
    // appsettings.json is the base source, env vars can override everything for deployment.
    configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
    configuration.AddJsonFile("local.settings.json", optional: true, reloadOnChange: true);
    configuration.AddEnvironmentVariables();
}

static void RegisterCoreServices(IServiceCollection services, IConfiguration configuration)
{
    services.Configure<AppOptions>(configuration);
    // Plain-text mode keeps DB portable across users/machines.
    services.AddSingleton<ITokenProtector, PlainTextTokenProtector>();

    services.AddSingleton(sp =>
    {
        var options = sp.GetRequiredService<IOptions<AppOptions>>().Value;
        var path = options.Database.Path;
        if (!Path.IsPathRooted(path))
            path = Path.Combine(AppContext.BaseDirectory, path);

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            // Create DB directory on first start to avoid runtime file-open errors.
            Directory.CreateDirectory(dir);
        }

        var tokenProtector = sp.GetRequiredService<ITokenProtector>();
        return new SqliteRepository(path, tokenProtector);
    });

    // Typed HttpClient for Strava API calls.
    services.AddHttpClient<StravaApiClient>();

    services.AddSingleton<ITelegramBotClient>(sp =>
    {
        var options = sp.GetRequiredService<IOptions<AppOptions>>().Value;
        if (string.IsNullOrWhiteSpace(options.Telegram.BotToken))
            throw new InvalidOperationException("Telegram bot token is missing.");

        return new TelegramBotClient(options.Telegram.BotToken);
    });
}

static void ConfigureSerilog(IConfiguration configuration)
{
    // Log file path can be relative in config; convert to absolute path near app binaries.
    var logPath = configuration["Logging:LogPath"] ?? "logs/trc-bot-.log";
    if (!Path.IsPathRooted(logPath))
        logPath = Path.Combine(AppContext.BaseDirectory, logPath);

    var logDir = Path.GetDirectoryName(logPath);
    if (!string.IsNullOrWhiteSpace(logDir))
        Directory.CreateDirectory(logDir);

    Log.Logger = new LoggerConfiguration()
        .ReadFrom.Configuration(configuration)
        .MinimumLevel.Information()
        .WriteTo.Console()
        .WriteTo.File(logPath, rollingInterval: RollingInterval.Day)
        .CreateLogger();
}

static void ConfigureListenFromRedirectUri(WebApplication app)
{
    var options = app.Services.GetRequiredService<IOptions<AppOptions>>().Value;
    if (!Uri.TryCreate(options.Strava.RedirectUri, UriKind.Absolute, out var uri))
        return;

    // Reuse host/port from configured redirect URI so local OAuth callback can be received.
    var listenUrl = $"{uri.Scheme}://{uri.Host}:{uri.Port}";
    if (!app.Urls.Contains(listenUrl, StringComparer.OrdinalIgnoreCase))
        app.Urls.Add(listenUrl);
}



