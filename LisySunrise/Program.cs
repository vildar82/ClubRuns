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
    // In console job mode we always print report to stdout first.
    // Telegram sending is optional and chosen interactively after execution.
    var result = await job.RunAsync(targetLocalDate: null, publishToDefaultTarget: false);

    Console.WriteLine(job.BuildReportText(result));
    Console.WriteLine();
    Console.Write("Send report to Telegram (enter @username or chat id, empty to skip): ");
    var target = Console.ReadLine()?.Trim();
    if (!string.IsNullOrWhiteSpace(target))
    {
        var chatId = await ResolveChatIdAsync(target, repo);
        if (chatId.HasValue)
        {
            await job.PublishReportToChatAsync(result, chatId.Value, CancellationToken.None);
            Console.WriteLine($"Report sent to chat {chatId.Value}.");
        }
        else
        {
            Console.WriteLine($"Unable to resolve Telegram target: {target}");
        }
    }

    return;
}

var builder = WebApplication.CreateBuilder(args);
// Long-running mode: Telegram polling + OAuth callback endpoint + Friday scheduler.
// Scheduler invokes the same AttendanceJobService as job mode; only trigger style differs.
ConfigureConfiguration(builder.Configuration);
ConfigureSerilog(builder.Configuration);

builder.Host.UseSerilog();
RegisterCoreServices(builder.Services, builder.Configuration);
builder.Services.AddHostedService<TelegramBotHostedService>();
builder.Services.AddHostedService<SchedulerService>();
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
    var logPath = configuration["Logging:LogPath"] ?? "logs/lisi-sunrise-.log";
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

static async Task<long?> ResolveChatIdAsync(string target, SqliteRepository repo)
{
    if (long.TryParse(target, out var numericChatId))
        return numericChatId;

    var username = target.TrimStart('@');
    if (string.IsNullOrWhiteSpace(username))
        return null;

    var user = await repo.GetUserByTelegramUsernameAsync(username);
    return user?.TelegramUserId;
}
