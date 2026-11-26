namespace BlazorA2UI;

/// <summary>
/// Error codes used on the wire (renderer-to-agent <c>error</c> messages and
/// <c>rendererFunctionResponse.error</c> payloads), as defined by A2UI v1.0.
/// </summary>
public static class A2UIErrorCodes
{
    /// <summary>
    /// A message or component failed validation.
    /// </summary>
    public const string ValidationFailed = "VALIDATION_FAILED";
    /// <summary>
    /// A component was placed under a parent its catalog does not allow.
    /// </summary>
    public const string UnallowedParent = "UNALLOWED_PARENT";
    /// <summary>
    /// A component holds a child its catalog does not allow.
    /// </summary>
    public const string UnallowedChild = "UNALLOWED_CHILD";
    /// <summary>
    /// The component tree is inconsistent: a duplicate surface, a cycle, a dangling reference or an orphan.
    /// </summary>
    public const string IntegrityError = "INTEGRITY_ERROR";
    /// <summary>
    /// A function call is malformed, or a caller may not make it.
    /// </summary>
    public const string InvalidFunctionCall = "INVALID_FUNCTION_CALL";
    /// <summary>
    /// A function call names a function that no catalog defines.
    /// </summary>
    public const string UnknownFunction = "UNKNOWN_FUNCTION";
    /// <summary>
    /// An expression or function argument could not be parsed or evaluated.
    /// </summary>
    public const string ExpressionError = "EXPRESSION_ERROR";
    /// <summary>
    /// A function failed while it ran.
    /// </summary>
    public const string ExecutionError = "EXECUTION_ERROR";
    /// <summary>
    /// A reserved <c>@</c> key was used where it is not allowed.
    /// </summary>
    public const string InvalidReservedKey = "INVALID_RESERVED_KEY";
    /// <summary>
    /// A catalog was not found, or a catalog document is malformed.
    /// </summary>
    public const string CatalogError = "CATALOG_NOT_FOUND";
    /// <summary>
    /// A message, component tree or expression is nested past its limit.
    /// </summary>
    public const string RecursionLimitExceeded = "RECURSION_LIMIT_EXCEEDED";
    /// <summary>
    /// A data-model path is invalid, or a write cannot be applied.
    /// </summary>
    public const string DataError = "DATA_ERROR";
    /// <summary>
    /// A pending agent call was cancelled. Never sent to the agent.
    /// </summary>
    public const string Cancelled = "CANCELLED";
    /// <summary>
    /// A user action has an invalid event context or message.
    /// </summary>
    public const string InvalidAction = "INVALID_ACTION";
    /// <summary>
    /// An agent call did not answer in time.
    /// </summary>
    public const string Timeout = "TIMEOUT";

    /// <summary>
    /// An agent call could not be sent because the renderer has no outbound connection. Never sent to the agent.
    /// </summary>
    public const string NoListener = "NO_LISTENER";

    /// <summary>
    /// An agent call reused the id of a call that is still waiting. Never sent to the agent.
    /// </summary>
    public const string Duplicate = "DUPLICATE";

    /// <summary>
    /// An agent call was made after the processor was disposed. Never sent to the agent.
    /// </summary>
    public const string Disposed = "DISPOSED";

    /// <summary>
    /// A call could not be sent because the most calls allowed are already waiting for an answer. Never sent to the
    /// other side.
    /// </summary>
    public const string TooManyPending = "TOO_MANY_PENDING";

    /// <summary>
    /// A call failed for a reason no other code describes, such as a malformed answer.
    /// </summary>
    public const string UnknownError = "UNKNOWN_ERROR";
}

/// <summary>
/// Base class for every error the A2UI core raises.
/// </summary>
public class A2UIException : Exception
{
    /// <summary>
    /// Creates an error with a wire code.
    /// </summary>
    /// <param name="code">The wire error code, usually one of <see cref="A2UIErrorCodes"/>.</param>
    /// <param name="message">The human-readable message sent with the code.</param>
    /// <param name="inner">The exception that caused this one, if any.</param>
    public A2UIException(string code, string message, Exception? inner = null) : base(message, inner)
    {
        Code = code;
    }

    /// <summary>
    /// The machine-readable wire error code.
    /// </summary>
    public string Code { get; }

    /// <summary>
    /// The surface the error belongs to, when known.
    /// </summary>
    public string? SurfaceId { get; init; }

    /// <summary>
    /// The function call the error belongs to, when known.
    /// </summary>
    public string? FunctionCallId { get; init; }

    /// <summary>
    /// JSON Pointer into the offending payload, for validation errors.
    /// </summary>
    public string? Path { get; init; }
}

/// <summary>
/// A data binding or function expression could not be evaluated.
/// </summary>
public sealed class A2UIExpressionException : A2UIException
{
    /// <summary>
    /// Creates an expression error.
    /// </summary>
    /// <param name="message">The human-readable message.</param>
    /// <param name="inner">The exception that caused this one, if any.</param>
    public A2UIExpressionException(string message, Exception? inner = null)
        : base(A2UIErrorCodes.ExpressionError, message, inner) { }
}

/// <summary>
/// A read or write against the data model was invalid.
/// </summary>
public sealed class A2UIDataException : A2UIException
{
    /// <summary>
    /// Creates a data error with <paramref name="message"/>.
    /// </summary>
    public A2UIDataException(string message) : base(A2UIErrorCodes.DataError, message) { }
}

/// <summary>
/// A catalog was malformed, or a component or function could not be resolved to a catalog.
/// </summary>
public sealed class A2UICatalogException : A2UIException
{
    /// <summary>
    /// Creates a catalog error with <paramref name="message"/>.
    /// </summary>
    public A2UICatalogException(string message) : base(A2UIErrorCodes.CatalogError, message) { }
}

/// <summary>
/// A message or component failed validation.
/// </summary>
public sealed class A2UIValidationException : A2UIException
{
    /// <summary>
    /// Creates a validation error.
    /// </summary>
    /// <param name="message">The human-readable message.</param>
    /// <param name="code">The wire code; validation failures that the spec names more precisely use their own code.</param>
    public A2UIValidationException(string message, string code = A2UIErrorCodes.ValidationFailed)
        : base(code, message) { }
}
