namespace BlazorA2UI.Agent;

/// <summary>
/// A property value: a literal, a data binding (<c>{"@path": ...}</c>) or a function call (<c>{"@call": ...}</c>).
/// Strings, numbers and booleans convert to literals implicitly, and an <see cref="A2UICall"/> converts to a call.
/// </summary>
public sealed class A2UIValue
{
    /// <summary>
    /// The value as it goes on the wire. Every caller gets a copy.
    /// </summary>
    private readonly JsonNode? _node;

    /// <summary>
    /// Creates a value from its wire form.
    /// </summary>
    /// <param name="node">The wire form, owned by this value.</param>
    /// <param name="kind">What the value is.</param>
    private A2UIValue(JsonNode? node, A2UIValueKind kind)
    {
        _node = node;
        Kind = kind;
    }

    /// <summary>
    /// Gets what the value is.
    /// </summary>
    public A2UIValueKind Kind { get; }

    /// <summary>
    /// Creates a literal value. No object in the literal, at any depth, may use a key that starts with a single
    /// <c>@</c>, because the renderer would read it as a binding, a call or another reserved key. Escape a literal
    /// <c>@</c> key by doubling it (<c>@@handle</c>).
    /// </summary>
    /// <exception cref="ArgumentException">An object in the literal has a reserved key, or the literal is nested too deeply.</exception>
    /// <param name="value">The literal; it is copied.</param>
    public static A2UIValue Literal(JsonNode? value)
    {
        JsonNode? copy = value?.DeepClone();
        CheckLiteral(copy, 0);
        return new A2UIValue(copy, A2UIValueKind.Literal);
    }

    /// <summary>
    /// Creates a data binding. An absolute path (<c>/user/name</c>) starts at the data model's root; a relative path
    /// (<c>name</c>) resolves against the enclosing scope, such as the current item of a list template.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <param name="path">The JSON Pointer path, kept exactly as written.</param>
    public static A2UIValue Path(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return new A2UIValue(new JsonObject { ["@path"] = path }, A2UIValueKind.Path);
    }

    /// <summary>
    /// Creates a function call value.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="call"/> is null.</exception>
    /// <param name="call">The call.</param>
    public static A2UIValue Call(A2UICall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        return new A2UIValue(call.ToJson(), A2UIValueKind.Call);
    }

    /// <summary>
    /// Converts a string to a literal.
    /// </summary>
    /// <param name="value">The string.</param>
    public static implicit operator A2UIValue(string value) => new(JsonValue.Create(value), A2UIValueKind.Literal);

    /// <summary>
    /// Converts a boolean to a literal.
    /// </summary>
    /// <param name="value">The boolean.</param>
    public static implicit operator A2UIValue(bool value) => new(JsonValue.Create(value), A2UIValueKind.Literal);

    /// <summary>
    /// Converts an integer to a literal.
    /// </summary>
    /// <param name="value">The integer.</param>
    public static implicit operator A2UIValue(int value) => new(JsonValue.Create(value), A2UIValueKind.Literal);

    /// <summary>
    /// Converts an integer to a literal.
    /// </summary>
    /// <param name="value">The integer.</param>
    public static implicit operator A2UIValue(long value) => new(JsonValue.Create(value), A2UIValueKind.Literal);

    /// <summary>
    /// Converts a number to a literal.
    /// </summary>
    /// <exception cref="ArgumentException">The number is NaN or infinite, which JSON cannot carry.</exception>
    /// <param name="value">The number.</param>
    public static implicit operator A2UIValue(double value) =>
        double.IsFinite(value)
            ? new(JsonValue.Create(value), A2UIValueKind.Literal)
            : throw new ArgumentException("A number value must be finite.", nameof(value));

    /// <summary>
    /// Converts a function call to a call value.
    /// </summary>
    /// <param name="call">The call.</param>
    public static implicit operator A2UIValue(A2UICall call) => Call(call);

    /// <summary>
    /// Returns the value's wire form. Each call returns a new copy.
    /// </summary>
    public JsonNode? ToJson() => _node?.DeepClone();

    /// <inheritdoc />
    public override string ToString() => _node?.ToJsonString() ?? "null";

    /// <summary>
    /// Creates a literal from a wire form the library built, such as an array of bindings. It is not checked or copied.
    /// </summary>
    /// <param name="node">The wire form, owned by the new value.</param>
    internal static A2UIValue FromWire(JsonNode node) => new(node, A2UIValueKind.Literal);

    /// <summary>
    /// The deepest literal accepted. It matches the renderer's evaluation limit.
    /// </summary>
    internal const int MaxLiteralDepth = 64;

    /// <summary>
    /// Rejects a reserved key anywhere in a literal.
    /// </summary>
    /// <exception cref="ArgumentException">An object has a reserved key, or the literal is nested too deeply.</exception>
    /// <param name="node">The part of the literal to check.</param>
    /// <param name="depth">The nesting depth of <paramref name="node"/>.</param>
    private static void CheckLiteral(JsonNode? node, int depth)
    {
        if (depth > MaxLiteralDepth)
        {
            throw new ArgumentException($"A literal cannot be nested more than {MaxLiteralDepth} levels deep.", "value");
        }

        switch (node)
        {
            case JsonObject obj:
                foreach ((string key, JsonNode? child) in obj)
                {
                    if (IsReservedKey(key))
                    {
                        throw new ArgumentException($"A literal object cannot use the reserved key '{key}'.", "value");
                    }

                    CheckLiteral(child, depth + 1);
                }

                break;
            case JsonArray array:
                foreach (JsonNode? item in array)
                {
                    CheckLiteral(item, depth + 1);
                }

                break;
        }
    }

    /// <summary>
    /// Whether a key is reserved: it starts with <c>@</c> and is not escaped as <c>@@</c>.
    /// </summary>
    /// <param name="key">The object key.</param>
    internal static bool IsReservedKey(string key) => key.Length > 0 && key[0] == '@' && (key.Length == 1 || key[1] != '@');
}

/// <summary>
/// What an <see cref="A2UIValue"/> is.
/// </summary>
public enum A2UIValueKind
{
    /// <summary>
    /// A literal JSON value.
    /// </summary>
    Literal,

    /// <summary>
    /// A data binding, <c>{"@path": ...}</c>.
    /// </summary>
    Path,

    /// <summary>
    /// A function call, <c>{"@call": ...}</c>.
    /// </summary>
    Call,
}
