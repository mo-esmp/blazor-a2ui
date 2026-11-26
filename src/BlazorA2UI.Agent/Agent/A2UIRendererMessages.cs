using BlazorA2UI.Evaluation;
using BlazorA2UI.Unicode;

namespace BlazorA2UI.Agent;

/// <summary>
/// A user action the renderer reports (<c>action</c>).
/// </summary>
/// <param name="Name">The action's event name, from the component's <c>action.event.name</c>.</param>
/// <param name="SurfaceId">The surface the action came from.</param>
/// <param name="SourceComponentId">The component the user acted on.</param>
/// <param name="Timestamp">When the action happened, as the renderer reports it.</param>
/// <param name="Context">The event's context with its bindings resolved. The record owns this copy.</param>
/// <param name="UserMessage">The resolved <c>userMessage</c>, or null.</param>
/// <param name="Metadata">The renderer's metadata for the action, or null. The record owns this copy.</param>
public sealed record A2UIRendererAction(
    string Name,
    string SurfaceId,
    string SourceComponentId,
    DateTimeOffset Timestamp,
    JsonObject Context,
    string? UserMessage,
    JsonObject? Metadata);

/// <summary>
/// An error the renderer reports (<c>error</c>).
/// </summary>
/// <param name="Code">The error code, such as <see cref="A2UIErrorCodes.ValidationFailed"/>.</param>
/// <param name="Message">The renderer's description of the error. It comes from the renderer, so treat it as text.</param>
/// <param name="SurfaceId">The surface the error belongs to, or null for an error about a function call.</param>
/// <param name="FunctionCallId">The function call the error belongs to, or null for an error about a surface.</param>
/// <param name="Path">The JSON Pointer to the field that failed validation, or null.</param>
public sealed record A2UIRendererError(string Code, string Message, string? SurfaceId, string? FunctionCallId, string? Path);

/// <summary>
/// The answer to a function call (<c>rendererFunctionResponse</c> or <c>agentFunctionResponse</c>): a value or an error.
/// </summary>
/// <param name="FunctionCallId">The id of the call it answers.</param>
/// <param name="Value">The function's value when it succeeded, or null. The record owns this copy.</param>
/// <param name="Error">The error when the function failed, or null when it succeeded.</param>
public sealed record A2UIFunctionResponse(string FunctionCallId, JsonNode? Value, A2UIFunctionError? Error)
{
    /// <summary>
    /// Gets whether the function failed.
    /// </summary>
    public bool IsError => Error is not null;
}

/// <summary>
/// Why a function call failed.
/// </summary>
/// <param name="Code">The error code, such as <see cref="A2UIErrorCodes.ExecutionError"/>.</param>
/// <param name="Message">The description of the failure.</param>
public sealed record A2UIFunctionError(string Code, string Message);

/// <summary>
/// Reads renderer-to-agent messages and checks them against <c>renderer_to_agent.json</c>.
/// </summary>
internal static class RendererMessageParser
{
    /// <summary>
    /// The <c>action</c> message.
    /// </summary>
    internal const string Action = "action";

    /// <summary>
    /// The <c>callAgentFunction</c> message.
    /// </summary>
    internal const string CallAgentFunction = "callAgentFunction";

    /// <summary>
    /// The <c>rendererFunctionResponse</c> message.
    /// </summary>
    internal const string RendererFunctionResponse = "rendererFunctionResponse";

    /// <summary>
    /// The <c>error</c> message.
    /// </summary>
    internal const string Error = "error";

    /// <summary>
    /// An RFC 3339 date-time, as JSON Schema's <c>date-time</c> format requires: a date, a time and an offset.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex s_dateTime = new(
        @"^\d{4}-\d{2}-\d{2}[Tt]\d{2}:\d{2}:\d{2}(\.\d+)?([Zz]|[+-]\d{2}:\d{2})$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// Returns which renderer-to-agent message a JSON value is, and its body.
    /// </summary>
    /// <exception cref="A2UIValidationException">
    /// The value is not an object with <c>"version": "1.0"</c> and exactly one known message, or the body is not an object.
    /// </exception>
    /// <param name="message">The message.</param>
    public static (string Kind, JsonObject Body) ReadEnvelope(JsonNode? message)
    {
        if (message is not JsonObject envelope)
        {
            throw new A2UIValidationException("A renderer-to-agent message must be a JSON object.");
        }

        if (A2UIValues.AsString(envelope["version"]) != A2UIProtocol.Version)
        {
            throw new A2UIValidationException($"A renderer-to-agent message must have \"version\": \"{A2UIProtocol.Version}\".") { Path = "/version" };
        }

        if (envelope.Count != 2)
        {
            throw new A2UIValidationException(
                "A renderer-to-agent message must have exactly one of 'action', 'callAgentFunction', 'rendererFunctionResponse' or 'error'.");
        }

        foreach ((string key, JsonNode? body) in envelope)
        {
            if (key == "version")
            {
                continue;
            }

            if (key is not (Action or CallAgentFunction or RendererFunctionResponse or Error))
            {
                throw new A2UIValidationException($"Unknown renderer-to-agent message '{key}'.");
            }

            return body is JsonObject obj
                ? (key, obj)
                : throw new A2UIValidationException($"'{key}' must be an object.") { Path = "/" + key };
        }

        // The envelope has two keys and only one of them can be "version".
        throw new A2UIValidationException("A renderer-to-agent message must have a message besides 'version'.");
    }

    /// <summary>
    /// Reads an <c>action</c> body.
    /// </summary>
    /// <exception cref="A2UIValidationException">A required field is missing or has the wrong type.</exception>
    /// <param name="body">The body.</param>
    public static A2UIRendererAction ReadAction(JsonObject body)
    {
        string name = RequiredString(body, "name", Action);
        string surfaceId = RequiredString(body, "surfaceId", Action);
        string sourceComponentId = RequiredString(body, "sourceComponentId", Action);
        string timestamp = RequiredString(body, "timestamp", Action);
        if (!s_dateTime.IsMatch(timestamp)
            || !DateTimeOffset.TryParse(timestamp, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out DateTimeOffset when))
        {
            throw Invalid(Action, "timestamp", "must be an RFC 3339 date-time with an offset");
        }

        if (body["context"] is not JsonObject context)
        {
            throw Invalid(Action, "context", "must be an object");
        }

        string? userMessage = OptionalString(body, "userMessage", Action);
        JsonObject? metadata = null;
        if (body.ContainsKey("metadata"))
        {
            if (body["metadata"] is not JsonObject meta)
            {
                throw Invalid(Action, "metadata", "must be an object");
            }

            foreach ((string key, JsonNode? value) in meta)
            {
                if (key != "extensions" || value is not JsonObject extensions)
                {
                    throw Invalid(Action, "metadata", "may only hold an 'extensions' object");
                }

                foreach ((string extensionKey, JsonNode? _) in extensions)
                {
                    if (!Uax31.IsIdentifier(extensionKey))
                    {
                        throw Invalid(Action, "metadata", $"has the extension key '{extensionKey}', which is not a UAX #31 identifier");
                    }
                }
            }

            metadata = (JsonObject)meta.DeepClone();
        }

        return new A2UIRendererAction(name, surfaceId, sourceComponentId, when, (JsonObject)context.DeepClone(), userMessage, metadata);
    }

    /// <summary>
    /// Reads an <c>error</c> body. A validation error (<c>VALIDATION_FAILED</c>, <c>UNALLOWED_PARENT</c>,
    /// <c>UNALLOWED_CHILD</c>) needs a surface and a path; any other error belongs to exactly one of a surface and a
    /// function call.
    /// </summary>
    /// <exception cref="A2UIValidationException">The body does not match either form.</exception>
    /// <param name="body">The body.</param>
    public static A2UIRendererError ReadError(JsonObject body)
    {
        string code = RequiredString(body, "code", Error);
        string message = RequiredString(body, "message", Error);
        if (code is A2UIErrorCodes.ValidationFailed or A2UIErrorCodes.UnallowedParent or A2UIErrorCodes.UnallowedChild)
        {
            foreach ((string key, JsonNode? _) in body)
            {
                if (key is not ("code" or "message" or "surfaceId" or "path"))
                {
                    throw Invalid(Error, key, "is not allowed in a validation error");
                }
            }

            return new A2UIRendererError(code, message, RequiredString(body, "surfaceId", Error), null, RequiredString(body, "path", Error));
        }

        string? surfaceId = OptionalString(body, "surfaceId", Error);
        string? functionCallId = OptionalString(body, "functionCallId", Error);
        if ((surfaceId is null) == (functionCallId is null))
        {
            throw new A2UIValidationException("An error must have exactly one of 'surfaceId' and 'functionCallId'.") { Path = "/error" };
        }

        // Other keys are allowed in a generic error; a string path is kept.
        return new A2UIRendererError(code, message, surfaceId, functionCallId, A2UIValues.AsString(body["path"]));
    }

    /// <summary>
    /// Reads a function response body: a <c>functionCallId</c> and exactly one of <c>value</c> and an <c>error</c>
    /// with a string <c>code</c> and <c>message</c>, and nothing else.
    /// </summary>
    /// <exception cref="A2UIValidationException">The body does not have that shape.</exception>
    /// <param name="body">The body.</param>
    /// <param name="kind">The message name, for error messages.</param>
    public static A2UIFunctionResponse ReadFunctionResponse(JsonObject body, string kind)
    {
        string functionCallId = RequiredString(body, "functionCallId", kind);
        foreach ((string key, JsonNode? _) in body)
        {
            if (key is not ("functionCallId" or "value" or "error"))
            {
                throw Invalid(kind, key, "is not allowed");
            }
        }

        if (body.ContainsKey("value") == body.ContainsKey("error"))
        {
            throw new A2UIValidationException($"{kind} must have exactly one of 'value' and 'error'.") { FunctionCallId = functionCallId };
        }

        if (!body.ContainsKey("error"))
        {
            return new A2UIFunctionResponse(functionCallId, body["value"]?.DeepClone(), null);
        }

        if (body["error"] is not JsonObject error
            || error.Count != 2
            || A2UIValues.AsString(error["code"]) is not { } code
            || A2UIValues.AsString(error["message"]) is not { } message)
        {
            throw new A2UIValidationException($"{kind} 'error' must be an object with only a string 'code' and 'message'.")
            {
                FunctionCallId = functionCallId,
                Path = "/error",
            };
        }

        return new A2UIFunctionResponse(functionCallId, null, new A2UIFunctionError(code, message));
    }

    /// <summary>
    /// Returns a required string field.
    /// </summary>
    /// <exception cref="A2UIValidationException">The field is missing or not a string.</exception>
    /// <param name="body">The message body.</param>
    /// <param name="key">The field.</param>
    /// <param name="kind">The message name, for the error.</param>
    private static string RequiredString(JsonObject body, string key, string kind) =>
        A2UIValues.AsString(body[key]) ?? throw Invalid(kind, key, "must be a string");

    /// <summary>
    /// Returns an optional string field, or null when it is missing.
    /// </summary>
    /// <exception cref="A2UIValidationException">The field is present but not a string.</exception>
    /// <param name="body">The message body.</param>
    /// <param name="key">The field.</param>
    /// <param name="kind">The message name, for the error.</param>
    private static string? OptionalString(JsonObject body, string key, string kind) =>
        !body.ContainsKey(key) ? null : A2UIValues.AsString(body[key]) ?? throw Invalid(kind, key, "must be a string");

    /// <summary>
    /// Creates the error for a field that fails its schema.
    /// </summary>
    /// <param name="kind">The message name.</param>
    /// <param name="key">The field.</param>
    /// <param name="problem">What is wrong with it.</param>
    private static A2UIValidationException Invalid(string kind, string key, string problem) =>
        new($"{kind} '{key}' {problem}.") { Path = "/" + key };
}
