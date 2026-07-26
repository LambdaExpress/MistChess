using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MistChess.Api.Application;

namespace MistChess.Api.Tests.Infrastructure;

public sealed class MistChessWebApplicationFactory(
    string connectionString,
    bool runBackgroundWorkers = false,
    bool useTestAuthentication = false) : WebApplicationFactory<Program>
{
    public static MistChessWebApplicationFactory WithoutDatabase(bool authenticated = false) => new(
        "Host=127.0.0.1;Port=1;Database=mistchess_unavailable;Username=mistchess_unavailable;Password=unavailable;Timeout=1;Command Timeout=1",
        useTestAuthentication: authenticated);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:MistChess", connectionString);
        builder.UseEnvironment("Testing");
        if (!runBackgroundWorkers)
        {
            builder.ConfigureServices(services =>
            {
                var workers = services
                    .Where(descriptor => descriptor.ServiceType == typeof(IHostedService) &&
                        descriptor.ImplementationType is { } implementationType &&
                        (implementationType == typeof(MatchmakingWorker) || implementationType == typeof(GameClockWorker)))
                    .ToArray();
                foreach (var worker in workers)
                {
                    services.Remove(worker);
                }
            });
        }
        if (useTestAuthentication)
        {
            builder.ConfigureServices(services =>
            {
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = TestAuthenticationHandler.AuthenticationSchemeName;
                        options.DefaultChallengeScheme = TestAuthenticationHandler.AuthenticationSchemeName;
                    })
                    .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                        TestAuthenticationHandler.AuthenticationSchemeName,
                        _ => { });
            });
        }
    }

    public HttpClient CreateHttpsClient() => CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false,
        HandleCookies = true
    });
}

public sealed class TestAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string AuthenticationSchemeName = "ApiTests";
    public static readonly Guid PlayerId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, PlayerId.ToString("D"))],
            AuthenticationSchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), AuthenticationSchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
