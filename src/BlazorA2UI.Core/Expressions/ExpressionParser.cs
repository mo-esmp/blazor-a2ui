// Ported from the A2UI TypeScript web core (typescript/core/web_core/src/expressions/expression_parser.ts),
// Copyright 2024 Google LLC, licensed under the Apache License, Version 2.0.
// See THIRD-PARTY-NOTICES.md.

using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using BlazorA2UI.Unicode;

namespace BlazorA2UI.Expressions;

/// <summary>
/// Parses <c>formatString</c> templates: literal text with <c>${...}</c> interpolations
/// that hold data paths, literals and named-argument function calls.
/// </summary>
/// <remarks>
/// Parsed parts are returned as JSON: a string for literal text, <c>{"@path": ...}</c>
/// for a data binding, and <c>{"@call": ..., "args": {...}}</c> for a function call.
/// </remarks>
internal static class ExpressionParser
{
    /// <summary>
    /// The deepest nesting of calls and interpolations a template may have.
    /// </summary>
    public const int MaxDepth = 100;
    /// <summary>
    /// The longest template accepted, in UTF-16 code units.
    /// </summary>
    public const int MaxTemplateLength = 10_000;
    /// <summary>
    /// The most parts (literal runs and expressions) a template may produce.
    /// </summary>
    public const int MaxParts = 1_000;

    /// <summary>
    /// Parses a template string into its parts.
    /// </summary>
    public static List<JsonNode?> Parse(string input, int depth = 0)
    {
        if (depth > MaxDepth)
        {
            throw new A2UIExpressionException("Max recursion depth reached in parse");
        }

        if (input.Length > MaxTemplateLength)
        {
            throw new A2UIExpressionException(
                $"Expression template length ({input.Length}) exceeds maximum limit ({MaxTemplateLength})");
        }

        List<JsonNode?> parts = new();
        if (input.Length == 0)
        {
            return parts;
        }

        if (!input.Contains("${", StringComparison.Ordinal))
        {
            parts.Add(input);
            return parts;
        }

        Scanner scanner = new(input);
        StringBuilder literal = new();
        while (!scanner.IsAtEnd)
        {
            if (parts.Count >= MaxParts)
            {
                throw new A2UIExpressionException($"Expression parts count exceeds maximum limit ({MaxParts})");
            }

            if (scanner.Matches("${"))
            {
                scanner.Advance(2);
                string content = ExtractInterpolationContent(scanner);
                JsonNode? parsed = ParseExpression(content, depth + 1);
                if (parsed is JsonValue v && v.TryGetValue<string>(out string? s))
                {
                    // Literal results (including the empty result of null) merge into the surrounding text.
                    literal.Append(s);
                }
                else
                {
                    Flush(parts, literal);
                    parts.Add(parsed);
                }
            }
            else if (scanner.Matches("\\${"))
            {
                scanner.Advance(3);
                literal.Append("${");
            }
            else
            {
                literal.Append(scanner.Advance());
            }
        }

        Flush(parts, literal);
        return parts;
    }

    /// <summary>
    /// Moves the pending literal text into <paramref name="parts"/>, if there is any.
    /// </summary>
    private static void Flush(List<JsonNode?> parts, StringBuilder literal)
    {
        if (literal.Length > 0)
        {
            parts.Add(literal.ToString());
            literal.Clear();
        }
    }

    /// <summary>
    /// Reads the text of a <c>${...}</c> interpolation up to its matching closing brace, skipping braces inside string literals.
    /// </summary>
    /// <exception cref="A2UIExpressionException">The interpolation is not closed.</exception>
    private static string ExtractInterpolationContent(Scanner scanner)
    {
        int start = scanner.Pos;
        int balance = 1;
        while (!scanner.IsAtEnd && balance > 0)
        {
            string c = scanner.Advance();
            if (c == "{")
            {
                balance++;
            }
            else if (c == "}")
            {
                balance--;
            }
            else if (c is "'" or "\"")
            {
                while (!scanner.IsAtEnd)
                {
                    string d = scanner.Advance();
                    if (d == "\\")
                    {
                        scanner.Advance();
                    }
                    else if (d == c)
                    {
                        break;
                    }
                }
            }
        }

        if (balance > 0)
        {
            throw new A2UIExpressionException("Unclosed interpolation: missing '}'");
        }

        return scanner.Input.Substring(start, scanner.Pos - 1 - start);
    }

    /// <summary>
    /// Parses the content of one <c>${...}</c> block.
    /// </summary>
    public static JsonNode? ParseExpression(string expr, int depth = 0)
    {
        expr = expr.Trim();
        if (expr.Length == 0)
        {
            return "";
        }

        Scanner scanner = new(expr);
        JsonNode? result = ParseExpressionInternal(scanner, depth);
        if (!scanner.IsAtEnd)
        {
            throw new A2UIExpressionException(
                $"Unexpected characters at end of expression: '{scanner.Input[scanner.Pos..]}'");
        }

        return result;
    }

    /// <summary>
    /// Parses one expression: a literal, a path, a keyword or a function call.
    /// </summary>
    /// <exception cref="A2UIExpressionException">The expression is malformed or nested deeper than <see cref="MaxDepth"/>.</exception>
    private static JsonNode? ParseExpressionInternal(Scanner scanner, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new A2UIExpressionException("Max recursion depth reached in parse");
        }

        scanner.SkipWhitespace();
        if (scanner.IsAtEnd)
        {
            return "";
        }

        if (scanner.Matches("${"))
        {
            scanner.Advance(2);
            string content = ExtractInterpolationContent(scanner);
            return ParseExpression(content, depth + 1);
        }

        if (scanner.Peek() is "'" or "\"")
        {
            return ParseStringLiteral(scanner);
        }

        if (IsNumberStart(scanner))
        {
            return ParseNumberLiteral(scanner);
        }

        if (scanner.MatchesKeyword("true"))
        {
            return true;
        }

        if (scanner.MatchesKeyword("false"))
        {
            return false;
        }

        if (scanner.MatchesKeyword("null"))
        {
            return "";
        }

        string token = ScanPathOrIdentifier(scanner);
        scanner.SkipWhitespace();
        if (scanner.Peek() == "(")
        {
            return ParseFunctionCall(token, scanner, depth);
        }

        if (token.StartsWith('@'))
        {
            throw new A2UIExpressionException($"Expected '(' after function name '{token}'");
        }

        if (token.Length == 0)
        {
            return "";
        }

        return new JsonObject { ["@path"] = token };
    }

    /// <summary>
    /// Reads a data path or a function name at the scanner's position.
    /// </summary>
    private static string ScanPathOrIdentifier(Scanner scanner)
    {
        int start = scanner.Pos;
        if (scanner.Peek() == "@")
        {
            string next = scanner.Peek(1);
            if (next.Length != 1 || !(char.IsAsciiLetter(next[0]) || next[0] == '_'))
            {
                throw new A2UIExpressionException("Invalid identifier starting with '@' in expression");
            }

            scanner.Advance();
            while (!scanner.IsAtEnd)
            {
                string c = scanner.Peek();
                if (c.Length == 1 && (char.IsAsciiLetterOrDigit(c[0]) || c[0] == '_'))
                {
                    scanner.Advance();
                }
                else
                {
                    break;
                }
            }

            return scanner.Input[start..scanner.Pos];
        }

        while (!scanner.IsAtEnd)
        {
            string c = scanner.Peek();
            if (IsIdContinue(c) || c is "/" or "." or "-")
            {
                scanner.Advance();
            }
            else
            {
                break;
            }
        }

        return scanner.Input[start..scanner.Pos];
    }

    /// <summary>
    /// Parses the argument list of a call to <paramref name="name"/>.
    /// </summary>
    /// <exception cref="A2UIExpressionException">The argument list is malformed.</exception>
    private static JsonObject ParseFunctionCall(string name, Scanner scanner, int depth)
    {
        scanner.Advance(); // (
        scanner.SkipWhitespace();
        JsonObject args = new();
        while (!scanner.IsAtEnd && scanner.Peek() != ")")
        {
            int argStart = scanner.Pos;
            while (!scanner.IsAtEnd && IsIdContinue(scanner.Peek()))
            {
                scanner.Advance();
            }

            string argName = scanner.Input[argStart..scanner.Pos];
            scanner.SkipWhitespace();
            if (scanner.Peek() != ":")
            {
                throw new A2UIExpressionException($"Expected ':' after argument name '{argName}' in function '{name}'");
            }

            scanner.Advance();
            scanner.SkipWhitespace();
            args[argName] = ParseExpressionInternal(scanner, depth + 1);
            scanner.SkipWhitespace();
            if (scanner.Peek() == ",")
            {
                scanner.Advance();
                scanner.SkipWhitespace();
            }
        }

        if (scanner.Peek() != ")")
        {
            throw new A2UIExpressionException($"Expected ')' after function arguments for '{name}'");
        }

        scanner.Advance();
        return new JsonObject { ["@call"] = name, ["args"] = args };
    }

    /// <summary>
    /// Parses a quoted string literal, unescaping it.
    /// </summary>
    /// <exception cref="A2UIExpressionException">The literal is not closed.</exception>
    private static string ParseStringLiteral(Scanner scanner)
    {
        string quote = scanner.Advance();
        StringBuilder sb = new();
        while (!scanner.IsAtEnd)
        {
            string c = scanner.Advance();
            if (c == "\\")
            {
                if (scanner.IsAtEnd)
                {
                    break;
                }

                string next = scanner.Advance();
                sb.Append(next switch { "n" => "\n", "t" => "\t", "r" => "\r", _ => next });
            }
            else if (c == quote)
            {
                return sb.ToString();
            }
            else
            {
                sb.Append(c);
            }
        }

        throw new A2UIExpressionException($"Unclosed string literal: missing closing {quote}");
    }

    /// <summary>
    /// Whether a number literal starts at the scanner's position.
    /// </summary>
    private static bool IsNumberStart(Scanner scanner)
    {
        int offset = scanner.Peek() is "-" or "+" ? 1 : 0;
        if (IsDigit(scanner.Peek(offset)))
        {
            return true;
        }

        return scanner.Peek(offset) == "." && IsDigit(scanner.Peek(offset + 1));
    }

    /// <summary>
    /// The full syntax of a number literal.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex s_numberLiteral =
        new(@"^[+-]?(?:[0-9]+\.?[0-9]*|\.[0-9]+)(?:[eE][+-]?[0-9]+)?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Parses a number literal.
    /// </summary>
    /// <exception cref="A2UIExpressionException">The literal is malformed.</exception>
    private static JsonNode ParseNumberLiteral(Scanner scanner)
    {
        int start = scanner.Pos;
        if (scanner.Peek() is "-" or "+")
        {
            scanner.Advance();
        }

        while (!scanner.IsAtEnd && (IsDigit(scanner.Peek()) || scanner.Peek() == "."))
        {
            scanner.Advance();
        }

        if (!scanner.IsAtEnd && scanner.Peek() is "e" or "E")
        {
            scanner.Advance();
            if (!scanner.IsAtEnd && scanner.Peek() is "+" or "-")
            {
                scanner.Advance();
            }

            while (!scanner.IsAtEnd && IsDigit(scanner.Peek()))
            {
                scanner.Advance();
            }
        }

        string text = scanner.Input[start..scanner.Pos];
        if (!s_numberLiteral.IsMatch(text))
        {
            throw new A2UIExpressionException($"Invalid number literal: '{text}'");
        }

        double value = double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
        if (!double.IsFinite(value))
        {
            throw new A2UIExpressionException($"Number literal is out of range: '{text}'");
        }

        return JsonValue.Create(value == 0 ? 0d : value);
    }

    /// <summary>
    /// Whether a scanned code point is an ASCII digit.
    /// </summary>
    private static bool IsDigit(string c) => c.Length == 1 && char.IsAsciiDigit(c[0]);

    /// <summary>
    /// Whether a scanned code point can continue a UAX #31 identifier, so a keyword followed by one is not a keyword.
    /// </summary>
    private static bool IsIdContinue(string c) =>
        c.Length > 0 && Rune.TryGetRuneAt(c, 0, out Rune rune) && rune.Utf16SequenceLength == c.Length && Uax31.IsXidContinue(rune);

    /// <summary>
    /// Reads a template code point by code point.
    /// </summary>
    /// <param name="input">The template text.</param>
    private sealed class Scanner(string input)
    {
        /// <summary>
        /// Gets the template text.
        /// </summary>
        public string Input { get; } = input;

        /// <summary>
        /// Gets the current position, in UTF-16 code units.
        /// </summary>
        public int Pos { get; private set; }

        /// <summary>
        /// Gets whether the whole template has been read.
        /// </summary>
        public bool IsAtEnd => Pos >= Input.Length;

        /// <summary>
        /// Returns the code point at the offset (in code points, approximated as UTF-16 units past the first).
        /// </summary>
        public string Peek(int offset = 0)
        {
            int target = Pos + offset;
            if (target >= Input.Length)
            {
                return "\0";
            }

            return Rune.TryGetRuneAt(Input, target, out Rune rune) ? rune.ToString() : Input[target].ToString();
        }

        /// <summary>
        /// Consumes <paramref name="count"/> UTF-16 code units, or the next code point when no count is given, and returns them.
        /// </summary>
        public string Advance(int? count = null)
        {
            int step = count ?? (IsAtEnd ? 1 : Peek().Length);
            int end = Math.Min(Pos + step, Input.Length);
            string text = Input[Pos..end];
            Pos += step;
            return text;
        }

        /// <summary>
        /// Whether the text at the current position starts with <paramref name="expected"/>.
        /// </summary>
        public bool Matches(string expected) => Input.AsSpan(Math.Min(Pos, Input.Length)).StartsWith(expected, StringComparison.Ordinal);

        /// <summary>
        /// Consumes <paramref name="keyword"/> when it is at the current position as a whole word.
        /// </summary>
        /// <returns>False, consuming nothing, when the keyword is absent or continues into an identifier.</returns>
        public bool MatchesKeyword(string keyword)
        {
            if (!Matches(keyword))
            {
                return false;
            }

            string next = Peek(keyword.Length);
            if (next != "\0" && IsIdContinue(next))
            {
                return false;
            }

            Advance(keyword.Length);
            return true;
        }

        /// <summary>
        /// Skips white space.
        /// </summary>
        public void SkipWhitespace()
        {
            while (!IsAtEnd && char.IsWhiteSpace(Input[Pos]))
            {
                Pos++;
            }
        }
    }
}
