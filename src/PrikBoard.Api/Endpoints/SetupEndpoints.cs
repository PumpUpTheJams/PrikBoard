using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PrikBoard.Api.Auth;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Endpoints;

/// <summary>
/// Setting up a new PrikBoard in the browser: unlock with the setup key from the server's log (<see cref="SetupKey"/>),
/// add sign-in providers, sign in as the first admin, check how the server runs and download the recovery kit. The same
/// endpoints serve admins later (Admin → Instance). Browser sessions only, so there are no MCP tools for them.
/// </summary>
public static partial class SetupEndpoints
{
    /// <summary>Rate-limiter policy for entering the setup key, which anyone can try.</summary>
    public const string UnlockLimit = "setup";

    /// <summary>The HttpClient that fetches OpenID Connect discovery documents.</summary>
    public const string DiscoveryClient = "openid-discovery";

    private const string GitHubAppStatePurpose = "PrikBoard.GitHubAppState";

    public static RouteGroupBuilder MapSetupEndpoints(this RouteGroupBuilder api)
    {
        var setup = api.MapGroup("/setup").WithTags("Setup").NoBoardScope();

        setup.MapGet("", GetSetupStatus)
            .AllowAnonymous()
            .WithName("getSetupStatus")
            .WithSummary("Whether PrikBoard needs setting up")
            .WithDescription("Public. needed: nobody has an account yet, so the web app shows the setup instead of signing in. " +
                             "recovery: the server runs in recovery mode. unlocked: this browser entered the current setup key.");

        setup.MapPost("/unlock", UnlockSetup)
            .AllowAnonymous()
            .RequireRateLimiting(UnlockLimit)
            .WithName("unlockSetup")
            .WithSummary("Enter the setup key")
            .WithDescription("Public. Checks the setup key that the server printed in its log and, when it matches, sets a setup " +
                             "cookie for an hour. With it this browser can manage sign-in providers and run the checks, and the first " +
                             "sign-in in it becomes the admin. 403 for a wrong key, 409 when no key is open (PrikBoard is set up).")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status429TooManyRequests);

        setup.MapGet("/providers", ListSignInProviders)
            .RequireSetupAccess()
            .WithName("listSignInProviders")
            .WithSummary("List sign-in providers with their settings")
            .WithDescription("Setup or admins. Every sign-in provider, the default first: those from the server's configuration " +
                             "(read-only) and those added here. Client secrets are never returned.");

        setup.MapPost("/providers", CreateSignInProvider)
            .RequireSetupAccess()
            .WithName("createSignInProvider")
            .WithSummary("Add a sign-in provider")
            .WithDescription("Setup or admins. Adds an OpenID Connect provider (Google, Auth0, Keycloak and others), a GitHub " +
                             "OAuth App or a Discord application by its client id and secret, or Steam (no client id; an optional " +
                             "Web API key as the secret). It works right away; register {this server}/api/auth/callback/{id} as its " +
                             "callback URL (Steam needs none). 409 when the id is taken.")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        setup.MapPatch("/providers/{id}", UpdateSignInProvider)
            .RequireSetupAccess()
            .WithName("updateSignInProvider")
            .WithSummary("Change a sign-in provider")
            .WithDescription("Setup or admins. Changes a provider added here; fields left out stay as they are. The id can't change. " +
                             "409 for a provider from the server's configuration.")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        setup.MapDelete("/providers/{id}", DeleteSignInProvider)
            .RequireSetupAccess()
            .WithName("deleteSignInProvider")
            .WithSummary("Remove a sign-in provider")
            .WithDescription("Setup or admins. People whose only sign-in uses it can't sign in until a provider with the same id is " +
                             "added again, which finds their accounts again. 409 for a provider from the configuration, and for the " +
                             "last provider once people have accounts.")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        // A browser navigation (it ends at the provider), like login.
        setup.MapGet("/providers/{id}/test", TestSignInProvider)
            .RequireSetupAccess()
            .ExcludeFromDescription()
            .WithName("testSignInProvider");

        setup.MapPost("/openid-check", CheckOpenIdAuthority)
            .RequireSetupAccess()
            .WithName("checkOpenIdAuthority")
            .WithSummary("Try an OpenID Connect issuer URL")
            .WithDescription("Setup or admins. Fetches the issuer's discovery document (/.well-known/openid-configuration) and " +
                             "returns the issuer it names, or a 400 keyed on authority saying what went wrong.")
            .ProducesValidationProblem();

        setup.MapPost("/github-app", BeginGitHubApp)
            .RequireSetupAccess()
            .WithName("beginGitHubApp")
            .WithSummary("Start creating a GitHub App for sign-in")
            .WithDescription("Setup or admins. Returns a manifest and GitHub's address to post it to (a browser form with one " +
                             "field, manifest). The person confirms on GitHub, which sends them back to PrikBoard; the new app " +
                             "becomes the sign-in provider with the given id, and the browser returns to returnUrl. 409 when the id is taken.")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        // GitHub sends the browser here after the app was created.
        setup.MapGet("/github-app/callback", CompleteGitHubApp)
            .AllowAnonymous()
            .ExcludeFromDescription()
            .WithName("completeGitHubApp");

        setup.MapGet("/checks", GetSystemChecks)
            .RequireSetupAccess()
            .WithName("getSystemChecks")
            .WithSummary("Check how PrikBoard runs")
            .WithDescription("Setup or admins. The database and its migrations, the data folder, the sign-in keys and the hash key, " +
                             "HTTPS, the sign-in providers and the version, each Ok, Warning or Failed.");

        setup.MapGet("/recovery-kit", GetRecoveryKit)
            .RequireAdmin()
            .WithName("getRecoveryKit")
            .WithSummary("Download the recovery kit")
            .WithDescription("Admins only. A text file with the hash key (Auth:HashKey) and how to restore PrikBoard from a backup. " +
                             "Without the key nobody can sign in to their account again, so keep the file offline and safe.")
            .Produces(StatusCodes.Status200OK, contentType: "text/plain");

        return api;
    }

    /// <summary>
    /// Lets through browsers that entered the open setup key, and admins: 401 for anyone else signed out, 403 for other
    /// people and for every token.
    /// </summary>
    private static RouteHandlerBuilder RequireSetupAccess(this RouteHandlerBuilder builder) =>
        builder
            .AllowAnonymous()
            .AddEndpointFilter(async (context, next) =>
            {
                var http = context.HttpContext;
                if (http.User.IsToken()) return AdminEndpoints.SessionsOnly();
                if (await HasSetupAccessAsync(http)) return await next(context);
                return http.User.Identity?.IsAuthenticated == true
                    ? AdminEndpoints.AdminsOnly()
                    : TypedResults.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Setup key needed",
                        detail: "Enter the setup key from the server's log, or sign in as an admin.");
            })
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

    private static async Task<bool> HasSetupAccessAsync(HttpContext http) =>
        !http.User.IsToken()
        && (await http.RequestServices.GetRequiredService<SetupKey>().GrantedAsync(http) || await AdminEndpoints.IsAdminAsync(http));

    private static async Task<Ok<SetupStatus>> GetSetupStatus(HttpContext http, SetupKey key, PrikBoardDbContext db, CancellationToken ct) =>
        TypedResults.Ok(new SetupStatus(!await db.Users.AnyAsync(ct), key.Recovery && key.IsOpen, await key.GrantedAsync(http)));

    private static async Task<Results<NoContent, ProblemHttpResult>> UnlockSetup(UnlockSetupRequest request, HttpContext http, SetupKey key)
    {
        if (!key.IsOpen)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Set up already",
                detail: "PrikBoard is set up. Admins change it on the admin page; to recover, restart it with Setup__Recovery=true.");
        }
        if (key.Unlock(request.Key) is not { } principal)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Wrong setup key",
                detail: "That isn't the setup key. It's in the server's log; a restart prints a new one.");
        }
        await http.SignInAsync(SetupKey.Scheme, principal);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<List<SignInProviderDetail>>> ListSignInProviders(
        PrikBoardDbContext db, SignInProviders providers, CancellationToken ct) =>
        TypedResults.Ok(await DetailsAsync(db, providers, ct));

    private static async Task<List<SignInProviderDetail>> DetailsAsync(PrikBoardDbContext db, SignInProviders providers, CancellationToken ct)
    {
        var signIns = await db.UserIdentities.GroupBy(i => i.Provider).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, StringComparer.OrdinalIgnoreCase, ct);
        var defaultId = providers.Default?.Id;
        var configured = providers.All.Where(p => p.Row is null).Select(p => new SignInProviderDetail(
            p.Id, p.Options.Type, p.Name, p.Options.Authority, p.Options.ClientId ?? "", !string.IsNullOrEmpty(p.Options.ClientSecret),
            [.. p.Options.Scopes], p.Options.EmailsAreVerified, p.Id == defaultId, ProviderSource.Configuration, null,
            signIns.GetValueOrDefault(p.Id)));
        // Every stored row, also one that is off because its secret can't be decrypted, so it can be fixed.
        var rows = await db.SignInProviders.AsNoTracking().OrderBy(p => p.CreatedAt).ToListAsync(ct);
        var stored = rows.Where(r => !providers.IsConfigured(r.Id)).Select(r => new SignInProviderDetail(
            r.Id, r.Type, r.DisplayName ?? r.Id, r.Authority, r.ClientId, r.ProtectedClientSecret is not null, r.Scopes,
            r.EmailsAreVerified, r.Id == defaultId, ProviderSource.Setup, r.GitHubAppUrl, signIns.GetValueOrDefault(r.Id)));
        return [.. configured.Concat(stored).OrderByDescending(p => p.IsDefault)];
    }

    private static async Task<Results<Created<SignInProviderDetail>, ValidationProblem, ProblemHttpResult>> CreateSignInProvider(
        CreateSignInProviderRequest request, PrikBoardDbContext db, SignInProviders providers, CancellationToken ct)
    {
        if (!AuthSetup.IsValidId(request.Id)) return Problems.Invalid("id", "Use lowercase letters, digits and dashes.");
        if (await IdTakenAsync(request.Id, db, providers, ct) is { } taken) return taken;

        var provider = new SignInProvider
        {
            Id = request.Id,
            Type = request.Type,
            DisplayName = Blank(request.DisplayName),
            Authority = Blank(request.Authority),
            ClientId = request.ClientId?.Trim() ?? "",
            ProtectedClientSecret = Blank(request.ClientSecret) is { } secret ? providers.ProtectSecret(secret) : null,
            Scopes = request.Scopes ?? [],
            EmailsAreVerified = request.EmailsAreVerified,
        };
        if (Validate(provider) is { } invalid) return invalid;

        db.SignInProviders.Add(provider);
        if (request.IsDefault) await MakeDefaultAsync(provider, db, ct);
        await db.SaveChangesAsync(ct);
        await providers.LoadAsync(db, ct);
        var detail = (await DetailsAsync(db, providers, ct)).Single(p => p.Id == provider.Id);
        return TypedResults.Created($"/api/setup/providers/{provider.Id}", detail);
    }

    private static async Task<Results<Ok<SignInProviderDetail>, ValidationProblem, ProblemHttpResult>> UpdateSignInProvider(
        string id, UpdateSignInProviderRequest request, PrikBoardDbContext db, SignInProviders providers, CancellationToken ct)
    {
        if (providers.IsConfigured(id)) return InConfiguration();
        var provider = await db.SignInProviders.FindAsync([id], ct);
        if (provider is null) return NotFound();

        if (request.DisplayName is not null) provider.DisplayName = Blank(request.DisplayName);
        if (request.Authority is not null) provider.Authority = Blank(request.Authority);
        if (request.ClientId is not null) provider.ClientId = request.ClientId.Trim();
        if (request.ClientSecret is not null)
        {
            provider.ProtectedClientSecret = Blank(request.ClientSecret) is { } secret ? providers.ProtectSecret(secret) : null;
        }
        if (request.Scopes is not null) provider.Scopes = request.Scopes;
        if (request.EmailsAreVerified is { } verified) provider.EmailsAreVerified = verified;
        if (request.IsDefault is true) await MakeDefaultAsync(provider, db, ct);
        else if (request.IsDefault is false) provider.IsDefault = false;
        if (Validate(provider) is { } invalid) return invalid;

        await db.SaveChangesAsync(ct);
        await providers.LoadAsync(db, ct);
        return TypedResults.Ok((await DetailsAsync(db, providers, ct)).Single(p => p.Id == provider.Id));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteSignInProvider(
        string id, PrikBoardDbContext db, SignInProviders providers, CancellationToken ct)
    {
        if (providers.IsConfigured(id)) return InConfiguration();
        var provider = await db.SignInProviders.FindAsync([id], ct);
        if (provider is null) return NotFound();
        if (providers.All.All(p => p.Id == provider.Id) && await db.Users.AnyAsync(ct))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Last sign-in provider",
                detail: "Nobody could sign in any more. Add another provider first.");
        }

        // The sign-ins that use it stay: a provider added again with this id finds those accounts again.
        db.SignInProviders.Remove(provider);
        await db.SaveChangesAsync(ct);
        await providers.LoadAsync(db, ct);
        return TypedResults.NoContent();
    }

    /// <summary>
    /// A round trip through the provider that signs nobody in: it ends on <paramref name="returnUrl"/> with
    /// setupTest=ok or failed (see <see cref="UserSignIn.TestItem"/>).
    /// </summary>
    private static Results<ChallengeHttpResult, ProblemHttpResult> TestSignInProvider(string id, string? returnUrl, SignInProviders providers)
    {
        if (providers.Find(id) is null) return NotFound();
        var properties = new AuthenticationProperties { RedirectUri = AuthEndpoints.IsLocalUrl(returnUrl) ? returnUrl : "/#/setup" };
        properties.Items[UserSignIn.TestItem] = "1";
        return TypedResults.Challenge(properties, [id]);
    }

    private static async Task<Results<Ok<OpenIdAuthority>, ValidationProblem>> CheckOpenIdAuthority(
        CheckOpenIdAuthorityRequest request, IHttpClientFactory clients, CancellationToken ct)
    {
        if (AuthorityProblem(request.Authority) is { } problem) return Problems.Invalid("authority", problem);
        var discovery = $"{request.Authority.Trim().TrimEnd('/')}/.well-known/openid-configuration";
        try
        {
            using var response = await clients.CreateClient(DiscoveryClient).GetAsync(discovery, ct);
            if (!response.IsSuccessStatusCode)
            {
                return Problems.Invalid("authority", $"{discovery} answered {(int)response.StatusCode} {response.ReasonPhrase}.");
            }
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            return json.RootElement.TryGetProperty("issuer", out var issuer) && issuer.GetString() is { Length: > 0 } name
                ? TypedResults.Ok(new OpenIdAuthority(name))
                : Problems.Invalid("authority", $"{discovery} names no issuer.");
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException)
        {
            return Problems.Invalid("authority", $"{discovery} could not be read: {error.Message}");
        }
    }

    private static async Task<Results<Ok<GitHubAppManifest>, ValidationProblem, ProblemHttpResult>> BeginGitHubApp(
        BeginGitHubAppRequest request, HttpRequest http, PrikBoardDbContext db, SignInProviders providers,
        IDataProtectionProvider protection, CancellationToken ct)
    {
        if (!AuthSetup.IsValidId(request.Id)) return Problems.Invalid("id", "Use lowercase letters, digits and dashes.");
        if (await IdTakenAsync(request.Id, db, providers, ct) is { } taken) return taken;
        if (request.Organization is { Length: > 0 } org && !GitHubName().IsMatch(org))
        {
            return Problems.Invalid("organization", "That isn't a GitHub organization name.");
        }

        var origin = $"{http.Scheme}://{http.Host}";
        var manifest = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["name"] = request.Name.Trim(),
            ["url"] = origin,
            ["description"] = $"Sign-in for PrikBoard at {origin}.",
            ["callback_urls"] = new[] { $"{origin}{AuthSetup.CallbackPath(request.Id)}" },
            ["redirect_url"] = $"{origin}/api/setup/github-app/callback",
            // Public, or only its owner could sign in with it. It can't do anything but read the email addresses of
            // whoever signs in with it.
            ["public"] = true,
            ["default_permissions"] = new Dictionary<string, string> { ["email_addresses"] = "read" },
            // GitHub wants a webhook URL even for a webhook that is off.
            ["hook_attributes"] = new Dictionary<string, object> { ["url"] = $"{origin}/", ["active"] = false },
            ["request_oauth_on_install"] = false,
        });
        var state = protection.CreateProtector(GitHubAppStatePurpose).ToTimeLimitedDataProtector().Protect(
            JsonSerializer.Serialize(new GitHubAppState(request.Id, AuthEndpoints.IsLocalUrl(request.ReturnUrl) ? request.ReturnUrl! : "/#/setup")),
            TimeSpan.FromHours(1));
        var page = request.Organization is { Length: > 0 } owner
            ? $"https://github.com/organizations/{owner}/settings/apps/new"
            : "https://github.com/settings/apps/new";
        return TypedResults.Ok(new GitHubAppManifest($"{page}?state={Uri.EscapeDataString(state)}", manifest));
    }

    private sealed record GitHubAppState(string Id, string ReturnUrl);

    /// <summary>
    /// GitHub's redirect after the person created the app: trades the code for the app's credentials and saves the provider,
    /// then goes back to the page that started it with setupGitHub=ok or failed.
    /// </summary>
    private static async Task<RedirectHttpResult> CompleteGitHubApp(
        string? code, string? state, HttpContext http, PrikBoardDbContext db, SignInProviders providers, GitHubApps gitHub,
        IDataProtectionProvider protection, ILogger<GitHubApps> logger, CancellationToken ct)
    {
        GitHubAppState? started;
        try
        {
            started = JsonSerializer.Deserialize<GitHubAppState>(
                protection.CreateProtector(GitHubAppStatePurpose).ToTimeLimitedDataProtector().Unprotect(state ?? ""));
        }
        catch (Exception error) when (error is CryptographicException or JsonException)
        {
            started = null;
        }
        if (started is null) return Failed("/#/setup", "The answer from GitHub doesn't belong to a setup started here, or came too late.");
        if (!await HasSetupAccessAsync(http)) return Failed(started.ReturnUrl, "The setup key timed out. Enter it again and start over.");
        if (await IdTakenAsync(started.Id, db, providers, ct) is not null) return Failed(started.ReturnUrl, $"There already is a provider {started.Id}.");
        if (string.IsNullOrEmpty(code) || await gitHub.ConvertAsync(code, ct) is not { } app)
        {
            logger.LogWarning("GitHub refused the code for the new GitHub App of provider {Id}", started.Id);
            return Failed(started.ReturnUrl, "GitHub didn't hand over the new app. Try again.");
        }

        db.SignInProviders.Add(new SignInProvider
        {
            Id = started.Id,
            Type = ProviderType.GitHub,
            DisplayName = "GitHub",
            ClientId = app.ClientId,
            ProtectedClientSecret = providers.ProtectSecret(app.ClientSecret),
            GitHubAppUrl = app.HtmlUrl,
        });
        await db.SaveChangesAsync(ct);
        await providers.LoadAsync(db, ct);
        return TypedResults.Redirect(ResultUrl(started.ReturnUrl, $"setupGitHub=ok&setupProvider={Uri.EscapeDataString(started.Id)}"));

        static RedirectHttpResult Failed(string returnUrl, string reason) =>
            TypedResults.Redirect(ResultUrl(returnUrl, $"setupGitHub=failed&setupReason={Uri.EscapeDataString(reason)}"));
    }

    /// <summary>A local return URL such as /#/setup, with <paramref name="query"/> put before its hash.</summary>
    private static string ResultUrl(string returnUrl, string query)
    {
        var hash = returnUrl.IndexOf('#') is >= 0 and var at ? returnUrl[at..] : "";
        return $"/?{query}{hash}";
    }

    private static async Task<Ok<List<SystemCheck>>> GetSystemChecks(
        HttpRequest http, PrikBoardDbContext db, FileStore files, KeyedHasher hasher, SignInProviders providers,
        IOptions<KeyManagementOptions> keyManagement, CancellationToken ct)
    {
        var checks = new List<SystemCheck>();

        try
        {
            var version = await db.Database.SqlQuery<string>($"SELECT current_setting('server_version') AS \"Value\"").SingleAsync(ct);
            // "17.11 (Debian 17.11-1.pgdg13+2)": the number is enough.
            checks.Add(new("database", "Database", CheckStatus.Ok, $"PostgreSQL {version.Split(' ')[0]}"));
            var pending = (await db.Database.GetPendingMigrationsAsync(ct)).Count();
            checks.Add(pending == 0
                ? new("migrations", "Database schema", CheckStatus.Ok, "Up to date")
                : new("migrations", "Database schema", CheckStatus.Failed, $"{pending} migrations pending"));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            checks.Add(new("database", "Database", CheckStatus.Failed, error.Message));
        }

        checks.Add(files.WriteProblem() is { } problem
            ? new("storage", "Data folder", CheckStatus.Failed, $"{files.Root} is not writable: {problem}")
            : new("storage", "Data folder", CheckStatus.Ok, files.Root));

        checks.Add(keyManagement.Value.XmlRepository is FileSystemXmlRepository keys
            ? new("keys", "Data Protection keys", CheckStatus.Ok, keys.Directory.FullName)
            : new("keys", "Data Protection keys", CheckStatus.Warning, "Not stored on disk; lost on restart"));

        checks.Add(hasher.Source switch
        {
            HashKeySource.Configuration => new("hashKey", "Hash key", CheckStatus.Ok, KeyedHasher.ConfigKey),
            HashKeySource.File => new("hashKey", "Hash key", CheckStatus.Ok, Path.Combine(files.Root, KeyedHasher.KeyFileName)),
            _ => new("hashKey", "Hash key", CheckStatus.Warning, "Development key"),
        });

        checks.Add(http.IsHttps || IsLoopback(http.Host.Host)
            ? new("https", "HTTPS", CheckStatus.Ok, http.IsHttps ? "HTTPS" : "http on localhost")
            : new("https", "HTTPS", CheckStatus.Warning, "Plain http; sign-in providers require HTTPS"));

        checks.Add(providers.All.Count > 0
            ? new("providers", "Sign-in providers", CheckStatus.Ok, string.Join(", ", providers.All.Select(p => p.Name)))
            : new("providers", "Sign-in providers", CheckStatus.Failed, "None"));

        var commit = AppVersion.Current.IndexOf('+') is >= 0 and var plus ? AppVersion.Current[(plus + 1)..] : null;
        checks.Add(new("version", "Version", CheckStatus.Ok,
            commit is null ? AppVersion.Number : $"{AppVersion.Number} ({commit[..Math.Min(7, commit.Length)]})"));
        return TypedResults.Ok(checks);
    }

    private static FileContentHttpResult GetRecoveryKit(HttpContext http, KeyedHasher hasher, FileStore files)
    {
        var where = hasher.Source switch
        {
            HashKeySource.Configuration => $"It is set in {KeyedHasher.ConfigKey} (environment variable Auth__HashKey).",
            HashKeySource.File => $"PrikBoard generated it into {Path.Combine(files.Root, KeyedHasher.KeyFileName)}, in the data folder.",
            _ => "This is the public development key: this server is for trying PrikBoard out only.",
        };
        var text = $"""
            PrikBoard recovery kit
            ======================

            Server:  {http.Request.Scheme}://{http.Request.Host}
            Made:    {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC, by PrikBoard {AppVersion.Current}

            Hash key
            --------
            {Convert.ToBase64String(hasher.Key)}

            {where}

            PrikBoard stores no names, emails or provider account ids, only hashes made with this key. Without it nobody can
            sign in to their account again, and a backup of the database can't be used. Keep this file offline and safe: with
            it and the database, someone could check whether a given email address has an account.

            Restoring from a backup
            -----------------------
            1. Restore the database and the data folder ({files.Root}) from the same backup.
            2. If the data folder's hash-key file is missing, set the environment variable Auth__HashKey to the key above.
            3. The data folder's keys/ folder encrypts sign-in sessions and the sign-in providers' client secrets. Without it
               everyone signs in again, and providers added in the setup need their secrets entered again: start PrikBoard once
               with Setup__Recovery=true, open it and enter the setup key from its log.
            """;
        http.Response.Headers.CacheControl = "no-store";
        return TypedResults.File(Encoding.UTF8.GetBytes(text.ReplaceLineEndings("\n")), "text/plain; charset=utf-8", "prikboard-recovery-kit.txt");
    }

    /// <summary>The id is free: not in the configuration and not stored.</summary>
    private static async Task<ProblemHttpResult?> IdTakenAsync(string id, PrikBoardDbContext db, SignInProviders providers, CancellationToken ct) =>
        providers.IsConfigured(id) || await db.SignInProviders.AnyAsync(p => p.Id == id, ct)
            ? TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Provider exists",
                detail: $"There already is a sign-in provider {id}. Pick another id.")
            : null;

    private static ValidationProblem? Validate(SignInProvider provider)
    {
        if (provider.Type == ProviderType.OpenIdConnect)
        {
            if (AuthorityProblem(provider.Authority) is { } problem) return Problems.Invalid("authority", problem);
        }
        else if (provider.Authority is not null)
        {
            return Problems.Invalid("authority", $"{provider.Type} takes no issuer URL.");
        }
        if (provider.Type != ProviderType.Steam && provider.ClientId.Length == 0)
        {
            return Problems.Invalid("clientId", "Give the client id from the provider's app registration.");
        }
        if (provider.Type is ProviderType.GitHub or ProviderType.Discord && provider.ProtectedClientSecret is null)
        {
            return Problems.Invalid("clientSecret", $"{provider.Type} needs the client secret.");
        }
        if (provider.Scopes.Count > 20 || provider.Scopes.Any(s => s.Length is 0 or > 100 || s.Any(char.IsWhiteSpace)))
        {
            return Problems.Invalid("scopes", "Scopes are words without spaces, at most 20.");
        }
        return null;
    }

    /// <summary>Why an OpenID Connect issuer URL won't do; null when it is https (or http on this computer).</summary>
    private static string? AuthorityProblem(string? authority)
    {
        if (string.IsNullOrWhiteSpace(authority)) return "OpenID Connect needs the issuer URL.";
        if (!Uri.TryCreate(authority.Trim(), UriKind.Absolute, out var uri)
            || !(uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && IsLoopback(uri.Host))))
        {
            return "Give the issuer's https URL, such as https://accounts.google.com.";
        }
        return null;
    }

    private static bool IsLoopback(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || (IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));

    private static async Task MakeDefaultAsync(SignInProvider provider, PrikBoardDbContext db, CancellationToken ct)
    {
        foreach (var other in await db.SignInProviders.Where(p => p.IsDefault && p.Id != provider.Id).ToListAsync(ct)) other.IsDefault = false;
        provider.IsDefault = true;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static ProblemHttpResult NotFound() =>
        TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found", detail: "There is no such sign-in provider.");

    private static ProblemHttpResult InConfiguration() => TypedResults.Problem(statusCode: StatusCodes.Status409Conflict,
        title: "Set in the configuration", detail: "This provider comes from the server's configuration (Auth:Providers); change it there.");

    [GeneratedRegex("^[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?$")]
    private static partial Regex GitHubName();
}
