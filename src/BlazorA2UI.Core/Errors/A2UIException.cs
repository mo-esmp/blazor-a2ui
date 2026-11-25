namespace BlazorA2UI;

/// <summary>
/// Error codes used on the wire (renderer-to-agent <c>error</c> messages and
/// <c>rendererFunctionResponse.error</c> payloads), as defined by A2UI v1.0.
/// </summary>
public static class A2UIErrorCodes
{
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string UnallowedParent = "UNALLOWED_PARENT";
    public const string UnallowedChild = "UNALLOWED_CHILD";
    public const string IntegrityError = "INTEGRITY_ERROR";
    public const string InvalidFunctionCall = "INVALID_FUNCTION_CALL";
    public const string UnknownFunction = "UNKNOWN_FUNCTION";
    public const string ExpressionError = "EXPRESSION_ERROR";
    public const string ExecutionError = "EXECUTION_ERROR";
    public const string InvalidReservedKey = "INVALID_RESERVED_KEY";
    public const string CatalogError = "CATALOG_NOT_FOUND";
    public const string RecursionLimitExceeded = "RECURSION_LIMIT_EXCEEDED";
    public const string DataError = "DATA_ERROR";
    public const string Cancelled = "CANCELLED";
    public const string InvalidAction = "INVALID_ACTION";
    public const string Timeout = "TIMEOUT";
}

/// <summary>Base class for every error the A2UI core raises.</summary>
public class A2UIException : Exception
{
    public A2UIException(string code, string message, Exception? inner = null) : base(message, inner)
    {
        Code = code;
    }

    /// <summary>The machine-readable wire error code.</summary>
    public string Code { get; }

    /// <summary>The surface the error belongs to, when known.</summary>
    public string? SurfaceId { get; init; }

    /// <summary>The function call the error belongs to, when known.</summary>
    public string? FunctionCallId { get; init; }

    /// <summary>JSON Pointer into the offending payload, for validation errors.</summary>
    public string? Path { get; init; }
}

/// <summary>A data binding or function expression could not be evaluated.</summary>
public sealed class A2UIExpressionException : A2UIException
{
    public A2UIExpressionException(string message, Exception? inner = null)
        : base(A2UIErrorCodes.ExpressionError, message, inner) { }
}

/// <summary>A read or write against the data model was invalid.</summary>
public sealed class A2UIDataException : A2UIException
{
    public A2UIDataException(string message) : base(A2UIErrorCodes.DataError, message) { }
}

/// <summary>A catalog was malformed, or a component or function could not be resolved to a catalog.</summary>
public sealed class A2UICatalogException : A2UIException
{
    public A2UICatalogException(string message) : base(A2UIErrorCodes.CatalogError, message) { }
}

/// <summary>A message or component failed validation.</summary>
public sealed class A2UIValidationException : A2UIException
{
    public A2UIValidationException(string message, string code = A2UIErrorCodes.ValidationFailed)
        : base(code, message) { }
}
