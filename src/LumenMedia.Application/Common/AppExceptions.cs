namespace LumenMedia.Application.Common;

/// <summary>Base for expected application errors mapped to Problem Details by the API layer.</summary>
public abstract class AppException(string message) : Exception(message)
{
    public abstract int StatusCode { get; }
    public abstract string ErrorType { get; }
}

public sealed class NotFoundException(string message) : AppException(message)
{
    public override int StatusCode => 404;
    public override string ErrorType => "not-found";
}

public sealed class ConflictException(string message) : AppException(message)
{
    public override int StatusCode => 409;
    public override string ErrorType => "conflict";
}

public sealed class ForbiddenException(string message) : AppException(message)
{
    public override int StatusCode => 403;
    public override string ErrorType => "forbidden";
}

public sealed class UnauthorizedException(string message) : AppException(message)
{
    public override int StatusCode => 401;
    public override string ErrorType => "unauthorized";
}

public sealed class UnprocessableException(string message) : AppException(message)
{
    public override int StatusCode => 422;
    public override string ErrorType => "unprocessable";
}

public sealed class RateLimitException(string message) : AppException(message)
{
    public override int StatusCode => 429;
    public override string ErrorType => "rate-limit";
}

public sealed class ValidationException : AppException
{
    public ValidationException(IReadOnlyDictionary<string, string[]> errors)
        : base("One or more validation errors occurred.")
    {
        Errors = errors;
    }

    public ValidationException(string field, string error)
        : this(new Dictionary<string, string[]> { [field] = [error] })
    {
    }

    public IReadOnlyDictionary<string, string[]> Errors { get; }
    public override int StatusCode => 400;
    public override string ErrorType => "validation";
}
