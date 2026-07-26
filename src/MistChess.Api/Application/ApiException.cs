using MistChess.Api.Contracts;

namespace MistChess.Api.Application;

public sealed class ApiException : Exception
{
    public ApiException(int statusCode, string code, string title, string? detail = null, Guid? gameId = null)
        : base(title)
    {
        StatusCode = statusCode;
        Code = code;
        Title = title;
        Detail = detail;
        GameId = gameId;
    }

    public int StatusCode { get; }
    public string Code { get; }
    public string Title { get; }
    public string? Detail { get; }
    public Guid? GameId { get; }

    public ErrorResponse ToResponse() => new(Code, Title, Detail, GameId);

    public static ApiException Unauthorized() => new(
        StatusCodes.Status401Unauthorized,
        "UNAUTHORIZED",
        "A valid guest session is required.");

    public static ApiException NotFound() => new(
        StatusCodes.Status404NotFound,
        "NOT_FOUND",
        "The requested resource was not found.");

    public static ApiException Conflict(string code, string title, Guid? gameId = null) => new(
        StatusCodes.Status409Conflict,
        code,
        title,
        gameId: gameId);

    public static ApiException Unprocessable(string code, string title) => new(
        StatusCodes.Status422UnprocessableEntity,
        code,
        title);
}
