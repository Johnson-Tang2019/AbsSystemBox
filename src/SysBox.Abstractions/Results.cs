// SPDX-License-Identifier: GPL-3.0-or-later
namespace SysBox.Abstractions;

public static class ErrorCodes
{
    public const string NotImplemented = "NOT_IMPLEMENTED";
    public const string InvalidArgument = "INVALID_ARGUMENT";
    public const string AccessDenied = "ACCESS_DENIED";
    public const string Unavailable = "UNAVAILABLE";
}

public sealed record SysBoxError
{
    public string Code { get; }
    public string Message { get; }
    public string? Details { get; }
    public bool Retryable { get; }

    public SysBoxError(string code, string message, string? details = null, bool retryable = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Code = code;
        Message = message;
        Details = details;
        Retryable = retryable;
    }
}

public sealed class Result<T> where T : notnull
{
    private readonly T? value;
    public bool IsSuccess => Error is null;
    public SysBoxError? Error { get; }
    public T Value => IsSuccess ? value! : throw new InvalidOperationException("A failed result has no value.");

    private Result(T value) => this.value = value;
    private Result(SysBoxError error) => Error = error;

    public static Result<T> Success(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(value);
    }

    public static Result<T> Failure(SysBoxError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new(error);
    }
}

public sealed class SysBoxOperationException(SysBoxError error) : Exception(error.Message)
{
    public SysBoxError Error { get; } = error;
}
