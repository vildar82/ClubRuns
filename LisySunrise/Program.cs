using LisySunrise.Config;
using LisySunrise.Data;
using LisySunrise.Services;
using LisySunrise.Security;
using LisySunrise.Web;
using Microsoft.Extensions.Options;
using Serilog;
using Telegram.Bot;

var mode = args.FirstOrDefault()?.Trim().ToLowerInvariant() ?? "bot";

if (mode == "job")
{
    // One-shot mode: run attendance collection once and exit.
    // Useful for manual execution or Windows Task Scheduler.
    var hostBuilder = Host.CreateApplicationBuilder(args);
    ConfigureConfiguration(hostBuilder.Configuration);
    ConfigureSerilog(hostBuilder.Configuration);
    hostBuilder.Services.AddLogging(logging =>
    {
        logging.ClearProviders();
        logging.AddSerilog();
    });

    RegisterCoreServices(hostBuilder.Services, hostBuilder.Configuration);
    hostBuilder.Services.AddTransient<AttendanceJobService>();

    using var host = hostBuilder.Build();
    var repo = host.Services.GetRequiredService<SqliteRepository>();
    await repo.InitializeAsync();

    var job = host.Services.GetRequiredService<AttendanceJobService>();
    var result = await job.RunAsync();

    Console.WriteLine($"Run date: {result.RunDate}");
    Console.WriteLine($"Found: {result.Found.Count}, Not found: {result.NotFound.Count}, Errors: {result.Errors.Count}");
    return;
}

var builder = WebApplication.CreateBuilder(args);
// Long-running mode: Telegram polling + OAuth callback endpoint + Friday scheduler.
ConfigureConfiguration(builder.Configuration);
ConfigureSerilog(builder.Configuration);

builder.Host.UseSerilog();
RegisterCoreServices(builder.Services, builder.Configuration);
builder.Services.AddHostedService<TelegramBotHostedService>();
builder.Services.AddHostedService<FridaySchedulerService>();
builder.Services.AddTransient<AttendanceJobService>();

var app = builder.Build();

// Ensure SQLite schema exists before receiving commands or OAuth callbacks.
var repository = app.Services.GetRequiredService<SqliteRepository>();
await repository.InitializeAsync();

ConfigureListenFromRedirectUri(app);
app.MapGet("/", () => Results.Text("Lisi Sunrise bot is running."));
app.MapStravaEndpoints();

await app.RunAsync();

static void ConfigureConfiguration(ConfigurationManager configuration)
{
    // appsettings.json is the base source, env vars can override everything for deployment.
    configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
    configuration.AddEnvironmentVariables();
}

static void RegisterCoreServices(IServiceCollection services, IConfiguration configuration)
{
    services.Configure<AppOptions>(configuration);
    services.AddSingleton<ITokenProtector>(_ =>
    {
        // Current implementation uses Windows DPAPI, so non-Windows runtime is blocked explicitly.
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows DPAPI token protection requires Windows runtime.");
        }

        return new WindowsDpapiTokenProtector();
    });

    services.AddSingleton(sp =>
    {
        var options = sp.GetRequiredService<IOptions<AppOptions>>().Value;
        var path = options.Database.Path;
        if (!Path.IsPathRooted(path))
        {
            path = Path.Combine(AppContext.BaseDirectory, path);
        }

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
        {
            throw new InvalidOperationException("Telegram bot token is missing.");
        }

        return new TelegramBotClient(options.Telegram.BotToken);
    });
}

static void ConfigureSerilog(IConfiguration configuration)
{
    // Log file path can be relative in config; convert to absolute path near app binaries.
    var logPath = configuration["Logging:LogPath"] ?? "logs/lisi-sunrise-.log";
    if (!Path.IsPathRooted(logPath))
    {
        logPath = Path.Combine(AppContext.BaseDirectory, logPath);
    }

    var logDir = Path.GetDirectoryName(logPath);
    if (!string.IsNullOrWhiteSpace(logDir))
    {
        Directory.CreateDirectory(logDir);
    }

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
    {
        return;
    }

    // Reuse host/port from configured redirect URI so local OAuth callback can be received.
    var listenUrl = $"{uri.Scheme}://{uri.Host}:{uri.Port}";
    if (!app.Urls.Contains(listenUrl, StringComparer.OrdinalIgnoreCase))
    {
        app.Urls.Add(listenUrl);
    }
}
