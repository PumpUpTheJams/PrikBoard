using Microsoft.AspNetCore.DataProtection;
using PrikBoard.Discord;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.Configure<BotOptions>(builder.Configuration.GetSection("Discord"));
builder.Services.Configure<PrikBoardOptions>(builder.Configuration.GetSection("PrikBoard"));

var bot = builder.Configuration.GetSection("Discord").Get<BotOptions>() ?? new BotOptions();
var prikboard = builder.Configuration.GetSection("PrikBoard").Get<PrikBoardOptions>() ?? new PrikBoardOptions();
if (string.IsNullOrWhiteSpace(bot.BotToken)) throw new InvalidOperationException("Set Discord:BotToken (env Discord__BotToken).");
if (!Uri.TryCreate(prikboard.ApiUrl, UriKind.Absolute, out var apiUrl)) throw new InvalidOperationException("Set PrikBoard:ApiUrl to where the API is reachable.");
if (!Uri.TryCreate(prikboard.PublicUrl, UriKind.Absolute, out _)) throw new InvalidOperationException("Set PrikBoard:PublicUrl to the address people use PrikBoard at.");

// Tokens are kept under Storage:Path (a volume in production); Development defaults to a folder next to the project.
var storage = builder.Configuration["Storage:Path"] is { Length: > 0 } path
    ? path
    : builder.Environment.IsDevelopment()
        ? Path.Combine(builder.Environment.ContentRootPath, "data")
        : throw new InvalidOperationException("Set Storage:Path (env Storage__Path) to a volume for the servers' tokens.");
Directory.CreateDirectory(storage);
var keys = builder.Configuration["DataProtection:KeysPath"] is { Length: > 0 } keysPath ? keysPath : Path.Combine(storage, "keys");
builder.Services.AddDataProtection().SetApplicationName("PrikBoard.Discord").PersistKeysToFileSystem(new DirectoryInfo(keys));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(services => new GuildTokens(services.GetRequiredService<IDataProtectionProvider>(), storage));
builder.Services.AddSingleton<PendingConnects>();
builder.Services.AddHttpClient<PrikBoardApi>(http =>
{
    http.BaseAddress = apiUrl;
    http.Timeout = TimeSpan.FromSeconds(10);
    http.DefaultRequestHeaders.UserAgent.ParseAdd("PrikBoard-Discord/1.0");
});
builder.Services.AddHostedService<DiscordBot>();

builder.Build().Run();
