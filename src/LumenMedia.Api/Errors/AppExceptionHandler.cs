using LumenMedia.Application.Common;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace LumenMedia.Api.Errors;

/// <summary>Maps <see cref="AppException"/>s (and unexpected errors) to RFC 9457 Problem Details.</summary>
public sealed class AppExceptionHandler(ILogger<AppExceptionHandler> logger) : IExceptionHandler
{
    private const string TypeBase = "https://lumenmedia/errors/";

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ProblemDetails problem;

        switch (exception)
        {
            case ValidationException validation:
                logger.LogWarning(
                    "Request validation failed at {Path}: {Detail}",
                    httpContext.Request.Path,
                    validation.Message);
                problem = new ValidationProblemDetails(
                    validation.Errors.ToDictionary(kv => kv.Key, kv => kv.Value))
                {
                    Type = TypeBase + validation.ErrorType,
                    Title = "Validation failed",
                    Status = validation.StatusCode,
                    Detail = validation.Message,
                };
                break;

            case AppException app:
                logger.LogWarning(
                    "Request {ErrorType} ({Status}) at {Path}: {Detail}",
                    app.ErrorType,
                    app.StatusCode,
                    httpContext.Request.Path,
                    app.Message);
                problem = new ProblemDetails
                {
                    Type = TypeBase + app.ErrorType,
                    Title = app.ErrorType.Replace('-', ' '),
                    Status = app.StatusCode,
                    Detail = app.Message,
                };
                break;

            default:
                logger.LogError(exception, "Unhandled exception");
                problem = new ProblemDetails
                {
                    Type = TypeBase + "internal",
                    Title = "Internal Server Error",
                    Status = StatusCodes.Status500InternalServerError,
                    Detail = "An unexpected error occurred.",
                };
                break;
        }

        problem.Instance = httpContext.Request.Path;
        problem.Extensions["traceId"] = httpContext.TraceIdentifier;

        httpContext.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
        httpContext.Response.ContentType = "application/problem+json";
        await httpContext.Response.WriteAsJsonAsync(problem, problem.GetType(), options: null, contentType: "application/problem+json", cancellationToken);
        return true;
    }
}
