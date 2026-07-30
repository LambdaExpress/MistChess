using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
using MistChess.Api.Application;
using MistChess.Api.Contracts;
using MistChess.Api.Health;
using MistChess.Api.Hubs;
using MistChess.Api.Middleware;
using MistChess.Api.Security;
using MistChess.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);
var configuredWebSocketOrigins = builder.Configuration
    .GetSection("WebSockets:AllowedOrigins")
    .Get<string[]>() ?? [];
var webSocketOrigins = configuredWebSocketOrigins
    .Where(value => !string.IsNullOrWhiteSpace(value))
    .Select(value => value.Trim().TrimEnd('/'))
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToArray();
if (webSocketOrigins.Length == 0)
{
    webSocketOrigins = builder.Environment.IsDevelopment()
        ? ["http://127.0.0.1:5173", "http://localhost:5173"]
        : builder.Environment.IsEnvironment("Testing")
            ? ["https://localhost"]
            : throw new InvalidOperationException(
                "WebSockets:AllowedOrigins must contain the public frontend origin in production.");
}

foreach (var origin in webSocketOrigins)
{
    if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
        (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
        uri.AbsolutePath != "/" ||
        !string.IsNullOrEmpty(uri.Query) ||
        !string.IsNullOrEmpty(uri.Fragment) ||
        !string.IsNullOrEmpty(uri.UserInfo))
    {
        throw new InvalidOperationException(
            $"WebSockets:AllowedOrigins contains invalid origin '{origin}'.");
    }
}
var connectionString = builder.Configuration.GetConnectionString("MistChess")
    ?? throw new InvalidOperationException("ConnectionStrings:MistChess is required.");
builder.Services.ConfigureHttpJsonOptions(options => ConfigureJson(options.SerializerOptions));
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    options.RequireHeaderSymmetry = true;
    foreach (var proxyEntry in builder.Configuration.GetSection("ReverseProxy:KnownProxies").GetChildren())
    {
        if (!IPAddress.TryParse(proxyEntry.Value, out var proxyAddress))
        {
            throw new InvalidOperationException(
                $"ReverseProxy:KnownProxies:{proxyEntry.Key} must be an IP address.");
        }

        options.KnownProxies.Add(proxyAddress);
    }
});
builder.Services.Configure<WebSocketOptions>(options =>
{
    foreach (var origin in webSocketOrigins)
    {
        options.AllowedOrigins.Add(origin);
    }
});


builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddDbContextFactory<MistChessDbContext>(options =>
    options.UseNpgsql(
        connectionString,
        npgsql => npgsql.MigrationsAssembly(typeof(MistChessDbContext).Assembly.GetName().Name!)));
builder.Services.AddSingleton<IGameStateSerializer, GameStateJsonSerializer>();
builder.Services.AddSingleton<GameFactory>();
builder.Services.AddSingleton<GameViewProjector>();
builder.Services.AddSingleton<MistChessMetrics>();
builder.Services.AddSingleton<RatingService>();
builder.Services.AddSingleton<GameCompletionService>();
builder.Services.AddScoped<GuestSessionService>();
builder.Services.AddScoped<GuestPresenceService>();
builder.Services.AddScoped<AdminUserService>();
builder.Services.AddScoped<RoomService>();
builder.Services.AddScoped<MatchmakingService>();
builder.Services.AddScoped<GameService>();
builder.Services.AddScoped<HistoryService>();
builder.Services.AddSingleton<GameConnectionTracker>();
builder.Services.AddSingleton<ILobbyNotifier, SignalRLobbyNotifier>();
builder.Services.AddSingleton<IGameNotifier, SignalRGameNotifier>();
builder.Services.AddSingleton<IAccountNotifier, SignalRAccountNotifier>();
builder.Services.AddSingleton<MatchmakingCoordinator>();
builder.Services.AddHostedService<MatchmakingWorker>();
builder.Services.AddHostedService<GameClockWorker>();
builder.Services.AddSingleton<PreAuthenticationRateLimitMiddleware>();
builder.Services.Configure<AdminOptions>(builder.Configuration.GetSection("Admin"));
builder.Services.AddSingleton<IPasswordHasher<AdminOptions>, PasswordHasher<AdminOptions>>();
builder.Services.AddSingleton<AdminLoginFailureLimiter>();
builder.Services.AddScoped<AdminCredentialService>();

builder.Services
    .AddAuthentication(GuestAuthenticationDefaults.Scheme)
    .AddScheme<AuthenticationSchemeOptions, GuestAuthenticationHandler>(
        GuestAuthenticationDefaults.Scheme,
        _ => { })
    .AddCookie(AdminAuthenticationDefaults.Scheme, options =>
    {
        options.Cookie.Name = AdminAuthenticationDefaults.GetCookieName(builder.Environment);
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.Path = "/";
        options.Cookie.IsEssential = true;
        options.ExpireTimeSpan = AdminAuthenticationDefaults.Lifetime;
        options.SlidingExpiration = false;
        options.Events = new CookieAuthenticationEvents
        {
            OnRedirectToLogin = context => WriteAuthenticationErrorAsync(
                context.Response,
                StatusCodes.Status401Unauthorized,
                new ErrorResponse("UNAUTHORIZED", "Administrator authentication is required.")),
            OnRedirectToAccessDenied = context => WriteAuthenticationErrorAsync(
                context.Response,
                StatusCodes.Status403Forbidden,
                new ErrorResponse("FORBIDDEN", "Administrator access is required."))
        };
    });
builder.Services.AddAuthorization(options =>
{
    options.DefaultPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .RequireClaim(MistChessClaims.PrincipalKind, MistChessClaims.GuestPrincipal)
        .RequireAssertion(context => !context.User.HasClaim(
            MistChessClaims.Banned,
            bool.TrueString))
        .Build();
    options.AddPolicy(
        AdminAuthenticationDefaults.AuthorizationPolicy,
        new AuthorizationPolicyBuilder(AdminAuthenticationDefaults.Scheme)
            .RequireAuthenticatedUser()
            .RequireClaim(MistChessClaims.PrincipalKind, MistChessClaims.AdminPrincipal)
            .Build());
});
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = builder.Environment.IsDevelopment()
        ? "MistChess-XSRF"
        : "__Host-MistChess-XSRF";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.SameAsRequest
        : CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.Path = "/";
});
builder.Services.AddControllersWithViews()
    .AddJsonOptions(options => ConfigureJson(options.JsonSerializerOptions))
    .ConfigureApiBehaviorOptions(options =>
    {
        options.InvalidModelStateResponseFactory = _ => new BadRequestObjectResult(
            new ErrorResponse("INVALID_REQUEST", "The request body is invalid."));
    });
builder.Services.AddSignalR()
    .AddJsonProtocol(options => ConfigureJson(options.PayloadSerializerOptions));
builder.Services.AddResponseCompression(options => options.EnableForHttps = true);
builder.Services.AddOpenApi(options =>
{
    options.AddSchemaTransformer((schema, context, _) =>
    {
        var type = Nullable.GetUnderlyingType(context.JsonTypeInfo.Type) ?? context.JsonTypeInfo.Type;
        if (type == typeof(MistChess.Api.Contracts.Position))
        {
            schema.Required ??= new HashSet<string>(StringComparer.Ordinal);
            schema.Required.Add("file");
            schema.Required.Add("rank");
        }

        if (!type.IsEnum)
        {
            return Task.CompletedTask;
        }

        schema.Type = JsonSchemaType.String;
        schema.Format = null;
        schema.Enum =
        [
            .. Enum.GetNames(type)
                .Select(name => (JsonNode)JsonValue.Create(JsonNamingPolicy.CamelCase.ConvertName(name))!)
        ];
        return Task.CompletedTask;
    });
});
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("postgresql", tags: ["ready"]);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
    {
        if (context.HttpContext.Request.Path.StartsWithSegments("/api/replay-shares"))
        {
            context.HttpContext.RequestServices
                .GetRequiredService<MistChessMetrics>()
                .RecordShareOperation("rate_limited");
        }

        context.HttpContext.Response.ContentType = "application/json";
        await context.HttpContext.Response.WriteAsJsonAsync(
            new ErrorResponse("RATE_LIMITED", "Too many requests."),
            cancellationToken);
    };
    options.AddPolicy("session", context => FixedWindow(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        12,
        TimeSpan.FromMinutes(1)));
    options.AddPolicy("resource", context => FixedWindow(PlayerKey(context), 20, TimeSpan.FromMinutes(1)));
    options.AddPolicy("matchmaking", context => FixedWindow(PlayerKey(context), 12, TimeSpan.FromMinutes(1)));
    options.AddPolicy("command", context => FixedWindow(PlayerKey(context), 30, TimeSpan.FromMinutes(1)));
    options.AddPolicy("history-read", context => FixedWindow(PlayerKey(context), 60, TimeSpan.FromMinutes(1)));
    options.AddPolicy("share-change", context => FixedWindow(PlayerKey(context), 10, TimeSpan.FromMinutes(1)));
    options.AddPolicy("share-read", context => FixedWindow(ShareReadKey(context), 60, TimeSpan.FromMinutes(1)));
    options.AddPolicy("presence", context => FixedWindow(PlayerKey(context), 12, TimeSpan.FromMinutes(1)));
    options.AddPolicy("admin-users", context => FixedWindow(AdminKey(context), 60, TimeSpan.FromMinutes(1)));
    options.AddPolicy("admin-history", context => FixedWindow(AdminKey(context), 60, TimeSpan.FromMinutes(1)));
    options.AddPolicy("move", context => FixedWindow(
        PlayerKey(context),
        20,
        TimeSpan.FromSeconds(10)));
    options.AddPolicy("hub", context => FixedWindow(PlayerKey(context), 20, TimeSpan.FromMinutes(1)));
});

var app = builder.Build();
var adminOptions = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AdminOptions>>().Value;
if (!adminOptions.IsConfigured)
{
    app.Logger.LogWarning(
        "Administrator login is disabled because Admin:Username or Admin:PasswordHash is missing.");
}
var hasStaticWebApp = Directory.Exists(app.Environment.WebRootPath)
    && File.Exists(Path.Combine(app.Environment.WebRootPath, "index.html"));
app.UseForwardedHeaders();
app.UseWebSockets();
app.UseMiddleware<CompressedReplayResponseSizeMiddleware>();
app.UseResponseCompression();
app.UseMiddleware<UncompressedReplayResponseSizeMiddleware>();
app.Use(async (context, next) =>
{
    if ((context.Request.Path.StartsWithSegments("/hubs/lobby") ||
         context.Request.Path.StartsWithSegments("/hubs/game")) &&
        context.WebSockets.IsWebSocketRequest)
    {
        var origins = context.Request.Headers["Origin"];
        var origin = origins.Count == 1 ? origins[0] : null;
        if (origin is null || !webSocketOrigins.Contains(origin.TrimEnd('/'), StringComparer.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
    }

    if (context.Request.Path.StartsWithSegments("/shared/replay"))
    {
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
    }

    await next(context);
});
app.UseMiddleware<ApiExceptionMiddleware>();
app.UseMiddleware<SafeRequestLoggingMiddleware>();
app.UseHttpsRedirection();
if (hasStaticWebApp)
{
    app.UseDefaultFiles();
    app.UseStaticFiles();
}
app.UseRouting();
app.UseMiddleware<PreAuthenticationRateLimitMiddleware>();
app.UseAuthentication();
app.UseMiddleware<GuestPresenceMiddleware>();
app.UseRateLimiter();
app.UseAuthorization();

app.MapOpenApi();
app.MapControllers();
app.MapHub<LobbyHub>("/hubs/lobby").RequireRateLimiting("hub");
app.MapHub<GameHub>("/hubs/game").RequireRateLimiting("hub");
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready")
});
if (hasStaticWebApp)
{
    app.MapFallback(async context =>
    {
        var path = context.Request.Path;
        var isFrontendRoute = (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
            && !System.IO.Path.HasExtension(path.Value)
            && !path.StartsWithSegments("/api")
            && !path.StartsWithSegments("/hubs")
            && !path.StartsWithSegments("/health")
            && !path.StartsWithSegments("/openapi");
        if (!isFrontendRoute)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.SendFileAsync(Path.Combine(app.Environment.WebRootPath, "index.html"));
    });
}

app.Run();

static void ConfigureJson(JsonSerializerOptions options)
{
    options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase;
    options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    options.RespectRequiredConstructorParameters = true;
    options.NumberHandling = JsonNumberHandling.Strict;
}

static RateLimitPartition<string> FixedWindow(string key, int permitLimit, TimeSpan window) =>
    RateLimitPartition.GetFixedWindowLimiter(
        key,
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = window,
            QueueLimit = 0,
            AutoReplenishment = true
        });

static string AdminKey(HttpContext context) =>
    context.User.FindFirst(MistChessClaims.PrincipalKind)?.Value == MistChessClaims.AdminPrincipal
        ? context.User.Identity?.Name ?? "admin"
        : context.Connection.RemoteIpAddress?.ToString() ?? "anonymous";

static async Task WriteAuthenticationErrorAsync(
    HttpResponse response,
    int statusCode,
    ErrorResponse error)
{
    response.StatusCode = statusCode;
    response.ContentType = "application/json";
    await response.WriteAsJsonAsync(error);
}

static string PlayerKey(HttpContext context) =>
    CurrentPlayer.TryGetId(context.User)?.ToString("N")
    ?? context.Connection.RemoteIpAddress?.ToString()
    ?? "anonymous";

static string ShareReadKey(HttpContext context)
{
    var remoteAddress = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var token = context.Request.RouteValues.TryGetValue("shareToken", out var value)
        ? value?.ToString() ?? string.Empty
        : string.Empty;
    var digest = Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(token))).AsSpan(0, 16).ToString();
    return $"{remoteAddress}:{digest}";
}

public partial class Program;
