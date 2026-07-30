using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using MistChess.Api.Application;
using MistChess.Api.Contracts;

namespace MistChess.Api.Security;

public sealed class AdminOptions
{
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(PasswordHash);
}

public static class AdminAuthenticationDefaults
{
    public const string Scheme = "MistChessAdmin";
    public const string AuthorizationPolicy = "admin";
    public const string CookieName = "__Host-MistChessAdmin";
    public const string DevelopmentCookieName = "MistChessAdmin";
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(8);

    public static string GetCookieName(IHostEnvironment environment) =>
        environment.IsDevelopment() ? DevelopmentCookieName : CookieName;
}

public static class MistChessClaims
{
    public const string PrincipalKind = "mistchess:principal-kind";
    public const string GuestPrincipal = "guest";
    public const string AdminPrincipal = "admin";
    public const string Banned = "mistchess:banned";
    public const string BanReason = "mistchess:ban-reason";
}

public enum AdminLoginAttemptResult
{
    Succeeded,
    Failed,
    Blocked
}

public sealed class AdminLoginFailureLimiter(TimeProvider timeProvider)
{
    private const int FailureLimit = 5;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    private readonly object gate = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> failures = new(StringComparer.Ordinal);
    private int attemptsSinceCleanup;

    public AdminLoginAttemptResult Evaluate(string source, Func<bool> verify)
    {
        lock (gate)
        {
            var now = timeProvider.GetUtcNow();
            CleanupExpiredSources(now);
            if (!failures.TryGetValue(source, out var sourceFailures))
            {
                sourceFailures = new Queue<DateTimeOffset>();
                failures.Add(source, sourceFailures);
            }

            RemoveExpired(sourceFailures, now);
            if (sourceFailures.Count >= FailureLimit)
            {
                return AdminLoginAttemptResult.Blocked;
            }

            if (verify())
            {
                failures.Remove(source);
                return AdminLoginAttemptResult.Succeeded;
            }

            sourceFailures.Enqueue(now);
            return AdminLoginAttemptResult.Failed;
        }
    }

    private void CleanupExpiredSources(DateTimeOffset now)
    {
        attemptsSinceCleanup++;
        if (attemptsSinceCleanup < 64)
        {
            return;
        }

        attemptsSinceCleanup = 0;
        foreach (var source in failures.Keys.ToArray())
        {
            var sourceFailures = failures[source];
            RemoveExpired(sourceFailures, now);
            if (sourceFailures.Count == 0)
            {
                failures.Remove(source);
            }
        }
    }

    private static void RemoveExpired(Queue<DateTimeOffset> sourceFailures, DateTimeOffset now)
    {
        while (sourceFailures.TryPeek(out var failedAt) && now - failedAt >= Window)
        {
            sourceFailures.Dequeue();
        }
    }
}

public sealed class AdminCredentialService(
    IOptions<AdminOptions> options,
    IPasswordHasher<AdminOptions> passwordHasher,
    AdminLoginFailureLimiter failureLimiter,
    TimeProvider timeProvider,
    ILogger<AdminCredentialService> logger)
{
    public AdminSessionView Verify(AdminLoginRequest request, string source)
    {
        var configured = options.Value;
        var now = timeProvider.GetUtcNow();
        if (!configured.IsConfigured)
        {
            throw new ApiException(
                StatusCodes.Status503ServiceUnavailable,
                "ADMIN_LOGIN_DISABLED",
                "Administrator login is not configured.");
        }

        AdminLoginAttemptResult attemptResult;
        try
        {
            attemptResult = failureLimiter.Evaluate(source, () =>
            {
                var passwordResult = passwordHasher.VerifyHashedPassword(
                    configured,
                    configured.PasswordHash,
                    request.Password);
                var validUsername = string.Equals(
                    configured.Username,
                    request.Username.Trim(),
                    StringComparison.Ordinal);
                return validUsername && passwordResult != PasswordVerificationResult.Failed;
            });
        }
        catch (FormatException exception)
        {
            logger.LogError(exception, "The configured administrator password hash is invalid.");
            throw new ApiException(
                StatusCodes.Status503ServiceUnavailable,
                "ADMIN_LOGIN_DISABLED",
                "Administrator login is not configured.");
        }

        if (attemptResult == AdminLoginAttemptResult.Blocked)
        {
            logger.LogWarning(
                "Admin login rate limited adminName={AdminName} sourceIp={SourceIp} actionTime={ActionTime}",
                configured.Username,
                source,
                now);
            throw new ApiException(
                StatusCodes.Status429TooManyRequests,
                "RATE_LIMITED",
                "Too many administrator login failures.");
        }

        if (attemptResult == AdminLoginAttemptResult.Failed)
        {
            logger.LogWarning(
                "Admin login failed adminName={AdminName} sourceIp={SourceIp} actionTime={ActionTime}",
                configured.Username,
                source,
                now);
            throw new ApiException(
                StatusCodes.Status401Unauthorized,
                "INVALID_ADMIN_CREDENTIALS",
                "The administrator username or password is invalid.");
        }
        logger.LogInformation(
            "Admin login succeeded adminName={AdminName} sourceIp={SourceIp} actionTime={ActionTime}",
            configured.Username,
            source,
            now);
        return new AdminSessionView(configured.Username, now.Add(AdminAuthenticationDefaults.Lifetime));
    }
}

public static class CurrentAdmin
{
    public static string GetName(ClaimsPrincipal principal)
    {
        if (!principal.HasClaim(MistChessClaims.PrincipalKind, MistChessClaims.AdminPrincipal))
        {
            throw ApiException.Unauthorized();
        }

        return principal.FindFirstValue(ClaimTypes.Name)
            ?? throw ApiException.Unauthorized();
    }
}
