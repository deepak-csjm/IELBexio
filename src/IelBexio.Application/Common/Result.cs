namespace IelBexio.Application.Common;

/// <summary>
/// Outcome of an operation that can fail for expected, non-exceptional reasons. Expected failures
/// (validation, preflight, duplicate) are returned, not thrown: they are normal business outcomes and
/// throwing would make them easy to swallow.
/// </summary>
public readonly record struct Result
{
    private Result(bool succeeded, string? errorCode, string? errorMessage)
    {
        Succeeded = succeeded;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
    }

    public bool Succeeded { get; }
    public bool Failed => !Succeeded;
    public string? ErrorCode { get; }
    public string? ErrorMessage { get; }

    public static Result Success() => new(true, null, null);
    public static Result Failure(string code, string message) => new(false, code, message);
}

public readonly record struct Result<T>
{
    private Result(bool succeeded, T? value, string? errorCode, string? errorMessage)
    {
        Succeeded = succeeded;
        Value = value;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
    }

    public bool Succeeded { get; }
    public bool Failed => !Succeeded;
    public T? Value { get; }
    public string? ErrorCode { get; }
    public string? ErrorMessage { get; }

    public static Result<T> Success(T value) => new(true, value, null, null);
    public static Result<T> Failure(string code, string message) => new(false, default, code, message);

    public T ValueOrThrow() => Succeeded
        ? Value!
        : throw new InvalidOperationException($"{ErrorCode}: {ErrorMessage}");
}
