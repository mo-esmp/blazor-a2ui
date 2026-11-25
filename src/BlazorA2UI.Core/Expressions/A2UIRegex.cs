// The dialect parser and static safety check are ported from the A2UI TypeScript web core
// (typescript/core/web_core/src/common/regex_subset.ts), Copyright 2024 Google LLC,
// licensed under the Apache License, Version 2.0. See THIRD-PARTY-NOTICES.md.

using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace BlazorA2UI.Expressions;

/// <summary>
/// The A2UI regex dialect: a portable subset of ECMAScript patterns with a static
/// safety check that rejects patterns prone to catastrophic backtracking. Accepted
/// patterns are compiled to an equivalent .NET regex with a match timeout.
/// </summary>
public static class A2UIRegex
{
    public const int MaxPatternCodePoints = 256;
    public const int MaxValueCodePoints = 4096;
    private const int MaxQuantifierBound = 65535;
    private const int MaxCodePoint = 0x10FFFF;
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly ConcurrentDictionary<string, Regex> Cache = new(StringComparer.Ordinal);

    /// <summary>
    /// Tests <paramref name="value"/> against <paramref name="pattern"/> with search semantics.
    /// </summary>
    /// <exception cref="A2UIExpressionException">The pattern is not in the dialect or fails the safety check.</exception>
    public static bool IsMatch(string pattern, string value)
    {
        var regex = Compile(pattern);
        if (CountCodePoints(value) > MaxValueCodePoints)
        {
            return false;
        }

        try
        {
            return regex.IsMatch(value);
        }
        catch (RegexMatchTimeoutException ex)
        {
            throw new A2UIExpressionException("Regex evaluation timed out.", ex);
        }
    }

    /// <summary>Validates <paramref name="pattern"/> and returns the compiled .NET regex.</summary>
    public static Regex Compile(string pattern)
    {
        if (Cache.TryGetValue(pattern, out var cached))
        {
            return cached;
        }

        var tree = Analyze(pattern);
        var regex = new Regex(Emit(tree), RegexOptions.CultureInvariant, MatchTimeout);
        if (Cache.Count > 512)
        {
            Cache.Clear();
        }

        Cache[pattern] = regex;
        return regex;
    }

    /// <summary>Parses and safety-checks a pattern, throwing an expression error when it is rejected.</summary>
    internal static Node Analyze(string pattern)
    {
        if (CountCodePoints(pattern) > MaxPatternCodePoints)
        {
            throw new A2UIExpressionException($"Unsafe regex pattern (PATTERN_TOO_LONG): exceeds the maximum length of {MaxPatternCodePoints} code points");
        }

        Node tree;
        try
        {
            tree = new Parser(ToCodePoints(pattern)).ParsePattern();
        }
        catch (RegexSyntaxException ex)
        {
            throw new A2UIExpressionException($"Unsupported regex pattern (UNSUPPORTED_SYNTAX): {ex.Message}");
        }

        string? rejection = null;
        if (HasNestedQuantifier(tree))
        {
            rejection = "NESTED_QUANTIFIER";
        }
        else if (HasOverlappingAlternation(tree))
        {
            rejection = "OVERLAPPING_ALTERNATION";
        }
        else if (HasAdjacentOverlap(tree))
        {
            rejection = "ADJACENT_OVERLAP";
        }

        if (rejection is not null)
        {
            throw new A2UIExpressionException($"Unsafe regex pattern ({rejection})");
        }

        return tree;
    }

    internal static int CountCodePoints(string input)
    {
        var count = 0;
        for (var i = 0; i < input.Length; i++)
        {
            if (char.IsHighSurrogate(input[i]) && i + 1 < input.Length && char.IsLowSurrogate(input[i + 1]))
            {
                i++;
            }

            count++;
        }

        return count;
    }

    // ---------------------------------------------------------------- AST

    internal readonly record struct Range(int Start, int End);

    internal enum Anchor { Start, End, WordBoundary, NotWordBoundary }

    internal abstract record Node;

    internal sealed record EmptyNode : Node;

    internal sealed record CharSetNode(IReadOnlyList<Range> Ranges) : Node;

    internal sealed record GroupNode(bool Capturing, Node Child) : Node;

    internal sealed record LookaheadNode(bool Negated, Node Child) : Node;

    internal sealed record AnchorNode(Anchor Anchor) : Node;

    internal sealed record ConcatNode(IReadOnlyList<Node> Items) : Node;

    internal sealed record AltNode(IReadOnlyList<Node> Branches) : Node;

    internal sealed record RepeatNode(Node Child, int Min, int? Max, bool Lazy) : Node;

    private sealed class RegexSyntaxException(string message) : Exception(message);

    // ------------------------------------------------------------ ranges

    private static readonly Range Surrogates = new(0xD800, 0xDFFF);
    private static readonly Range[] DigitRanges = [new(0x30, 0x39)];
    private static readonly Range[] WordRanges = [new(0x30, 0x39), new(0x41, 0x5A), new(0x5F, 0x5F), new(0x61, 0x7A)];

    private static readonly Range[] SpaceRanges =
    [
        new(0x09, 0x0D), new(0x20, 0x20), new(0xA0, 0xA0), new(0x1680, 0x1680), new(0x2000, 0x200A),
        new(0x2028, 0x2029), new(0x202F, 0x202F), new(0x205F, 0x205F), new(0x3000, 0x3000), new(0xFEFF, 0xFEFF),
    ];

    private static readonly Range[] LineTerminators = [new(0x0A, 0x0A), new(0x0D, 0x0D), new(0x2028, 0x2029)];
    private static readonly IReadOnlyList<Range> DotRanges = Complement(LineTerminators);

    private static IEnumerable<Range> ExcludeSurrogates(Range r)
    {
        if (r.Start > r.End)
        {
            yield break;
        }

        if (r.End < Surrogates.Start || r.Start > Surrogates.End)
        {
            yield return r;
            yield break;
        }

        if (r.Start < Surrogates.Start)
        {
            yield return new Range(r.Start, Surrogates.Start - 1);
        }

        if (r.End > Surrogates.End)
        {
            yield return new Range(Surrogates.End + 1, r.End);
        }
    }

    internal static IReadOnlyList<Range> Normalize(IEnumerable<Range> ranges)
    {
        var split = ranges.SelectMany(ExcludeSurrogates).OrderBy(r => r.Start).ThenBy(r => r.End).ToList();
        if (split.Count <= 1)
        {
            return split;
        }

        var merged = new List<Range> { split[0] };
        for (var i = 1; i < split.Count; i++)
        {
            var prev = merged[^1];
            var curr = split[i];
            if (curr.Start <= prev.End + 1)
            {
                merged[^1] = new Range(prev.Start, Math.Max(prev.End, curr.End));
            }
            else
            {
                merged.Add(curr);
            }
        }

        return merged;
    }

    internal static IReadOnlyList<Range> Complement(IEnumerable<Range> ranges)
    {
        var excluded = Normalize(ranges.Append(Surrogates).SelectMany(r => new[] { r }));

        // Normalize drops the surrogate block itself; add it back to the exclusion list.
        var all = excluded.Append(Surrogates).OrderBy(r => r.Start).ToList();
        var result = new List<Range>();
        var cursor = 0;
        foreach (var r in all)
        {
            if (r.Start > cursor)
            {
                result.Add(new Range(cursor, r.Start - 1));
            }

            cursor = Math.Max(cursor, r.End + 1);
        }

        if (cursor <= MaxCodePoint)
        {
            result.Add(new Range(cursor, MaxCodePoint));
        }

        return result.SelectMany(ExcludeSurrogates).ToList();
    }

    private static bool Overlap(IReadOnlyList<Range> a, IReadOnlyList<Range> b)
    {
        int i = 0, j = 0;
        while (i < a.Count && j < b.Count)
        {
            if (a[i].End < b[j].Start)
            {
                i++;
            }
            else if (b[j].End < a[i].Start)
            {
                j++;
            }
            else
            {
                return true;
            }
        }

        return false;
    }

    private static int[] ToCodePoints(string pattern)
    {
        var cps = new List<int>(pattern.Length);
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= pattern.Length || !char.IsLowSurrogate(pattern[i + 1]))
                {
                    throw new RegexSyntaxException("Lone high surrogate in pattern");
                }

                cps.Add(char.ConvertToUtf32(c, pattern[i + 1]));
                i++;
            }
            else if (char.IsLowSurrogate(c))
            {
                throw new RegexSyntaxException("Lone low surrogate in pattern");
            }
            else
            {
                cps.Add(c);
            }
        }

        return cps.ToArray();
    }

    // ------------------------------------------------------------ parser

    private sealed class Parser(int[] cps)
    {
        private int _pos;

        private bool Eof => _pos >= cps.Length;

        private int? Peek(int offset = 0) => _pos + offset < cps.Length ? cps[_pos + offset] : null;

        private int Next() => !Eof ? cps[_pos++] : throw new RegexSyntaxException("Unexpected end of pattern");

        private bool Eat(int cp)
        {
            if (Peek() == cp)
            {
                _pos++;
                return true;
            }

            return false;
        }

        public Node ParsePattern()
        {
            var tree = ParseDisjunction();
            if (!Eof)
            {
                throw new RegexSyntaxException("Unmatched closing parenthesis");
            }

            return tree;
        }

        private Node ParseDisjunction()
        {
            var branches = new List<Node> { ParseAlternative() };
            while (Eat('|'))
            {
                branches.Add(ParseAlternative());
            }

            return branches.Count == 1 ? branches[0] : new AltNode(branches);
        }

        private Node ParseAlternative()
        {
            var items = new List<Node>();
            while (!Eof && Peek() != '|' && Peek() != ')')
            {
                items.Add(ParseTerm());
            }

            return items.Count switch
            {
                0 => new EmptyNode(),
                1 => items[0],
                _ => new ConcatNode(items),
            };
        }

        private Node ParseTerm()
        {
            var atom = ParseAtom();
            if (!IsQuantifierStart(Peek()))
            {
                return atom;
            }

            if (atom is AnchorNode or LookaheadNode)
            {
                throw new RegexSyntaxException("Quantifier on an assertion is not supported");
            }

            return ParseQuantifier(atom);
        }

        private static bool IsQuantifierStart(int? cp) => cp is '*' or '+' or '?' or '{';

        private Node ParseQuantifier(Node child)
        {
            var q = Next();
            int min;
            int? max;
            switch (q)
            {
                case '*':
                    (min, max) = (0, null);
                    break;
                case '+':
                    (min, max) = (1, null);
                    break;
                case '?':
                    (min, max) = (0, 1);
                    break;
                default:
                    (min, max) = ParseBracedBounds();
                    break;
            }

            var lazy = Eat('?');
            if (IsQuantifierStart(Peek()))
            {
                throw new RegexSyntaxException("Possessive or chained quantifiers are not supported");
            }

            return new RepeatNode(child, min, max, lazy);
        }

        private int? ParseDecimal()
        {
            if (Peek() is not (>= '0' and <= '9'))
            {
                return null;
            }

            long value = 0;
            while (Peek() is >= '0' and <= '9')
            {
                value = Math.Min(value * 10 + (Next() - '0'), int.MaxValue);
            }

            return (int)value;
        }

        private (int Min, int? Max) ParseBracedBounds()
        {
            var min = ParseDecimal() ?? throw new RegexSyntaxException("Malformed brace quantifier");
            if (min > MaxQuantifierBound)
            {
                throw new RegexSyntaxException($"Quantifier lower bound {min} exceeds {MaxQuantifierBound}");
            }

            if (Eat('}'))
            {
                return (min, min);
            }

            if (!Eat(','))
            {
                throw new RegexSyntaxException("Malformed brace quantifier");
            }

            if (Eat('}'))
            {
                return (min, null);
            }

            var max = ParseDecimal();
            if (max is null || !Eat('}'))
            {
                throw new RegexSyntaxException("Malformed brace quantifier");
            }

            if (max > MaxQuantifierBound)
            {
                throw new RegexSyntaxException($"Quantifier upper bound {max} exceeds {MaxQuantifierBound}");
            }

            if (max < min)
            {
                throw new RegexSyntaxException("Quantifier minimum exceeds maximum");
            }

            return (min, max);
        }

        private Node ParseAtom()
        {
            var cp = Next();
            switch (cp)
            {
                case '^':
                    return new AnchorNode(Anchor.Start);
                case '$':
                    return new AnchorNode(Anchor.End);
                case '.':
                    return new CharSetNode(DotRanges);
                case '[':
                    return ParseClass();
                case '(':
                    return ParseGroup();
                case '\\':
                {
                    var esc = ParseEscape(inClass: false);
                    return esc.Anchor is { } a ? new AnchorNode(a)
                        : esc.Ranges is { } r ? new CharSetNode(r)
                        : new CharSetNode([new Range(esc.Single!.Value, esc.Single.Value)]);
                }
                case '{' or '}' or ']':
                    throw new RegexSyntaxException("Unescaped brace or bracket is not supported");
                case '*' or '+' or '?':
                    throw new RegexSyntaxException("Nothing to repeat");
                default:
                    return new CharSetNode([new Range(cp, cp)]);
            }
        }

        private Node ParseGroup()
        {
            if (!Eat('?'))
            {
                var body = ParseDisjunction();
                if (!Eat(')'))
                {
                    throw new RegexSyntaxException("Unclosed group");
                }

                return new GroupNode(true, body);
            }

            var modifier = Peek();
            if (modifier is not (':' or '=' or '!'))
            {
                throw new RegexSyntaxException("Unsupported group extension");
            }

            Next();
            var inner = ParseDisjunction();
            if (!Eat(')'))
            {
                throw new RegexSyntaxException("Unclosed group");
            }

            return modifier == ':' ? new GroupNode(false, inner) : new LookaheadNode(modifier == '!', inner);
        }

        private Node ParseClass()
        {
            var negated = Eat('^');
            var ranges = new List<Range>();
            while (!Eof && Peek() != ']')
            {
                ranges.AddRange(ParseClassItem());
            }

            if (!Eat(']'))
            {
                throw new RegexSyntaxException("Unclosed character class");
            }

            if (ranges.Count == 0)
            {
                throw new RegexSyntaxException("Empty character class");
            }

            var normalized = Normalize(ranges);
            return new CharSetNode(negated ? Complement(normalized) : normalized);
        }

        private IReadOnlyList<Range> ParseClassItem()
        {
            var first = ParseClassAtom();
            if (Peek() != '-' || Peek(1) == ']')
            {
                return first.Ranges;
            }

            Next();
            if (Peek() == '-')
            {
                throw new RegexSyntaxException("Character class subtraction is not supported");
            }

            var second = ParseClassAtom();
            if (first.Single is null || second.Single is null)
            {
                throw new RegexSyntaxException("Character class escape cannot form a range");
            }

            if (first.Single > second.Single)
            {
                throw new RegexSyntaxException("Character class range out of order");
            }

            return [new Range(first.Single.Value, second.Single.Value)];
        }

        private (int? Single, IReadOnlyList<Range> Ranges) ParseClassAtom()
        {
            var cp = Peek();
            if (cp == '\\')
            {
                Next();
                var esc = ParseEscape(inClass: true);
                if (esc.Single is { } s)
                {
                    return (s, [new Range(s, s)]);
                }

                if (esc.Ranges is { } r)
                {
                    return (null, r);
                }

                throw new RegexSyntaxException("Assertions are not valid in a character class");
            }

            if (cp == '[')
            {
                throw new RegexSyntaxException("Nested character class is not supported");
            }

            if (cp == '&' && Peek(1) == '&')
            {
                throw new RegexSyntaxException("Character class intersection is not supported");
            }

            var lit = Next();
            return (lit, [new Range(lit, lit)]);
        }

        private (int? Single, IReadOnlyList<Range>? Ranges, Anchor? Anchor) ParseEscape(bool inClass)
        {
            if (Eof)
            {
                throw new RegexSyntaxException("Trailing backslash in pattern");
            }

            var cp = Next();
            switch (cp)
            {
                case 'b':
                    return inClass ? (0x08, null, null) : (null, null, Anchor.WordBoundary);
                case 'B':
                    if (inClass)
                    {
                        throw new RegexSyntaxException("\\B is not supported inside a character class");
                    }

                    return (null, null, Anchor.NotWordBoundary);
                case 'd':
                    return (null, DigitRanges, null);
                case 'D':
                    return (null, Complement(DigitRanges), null);
                case 'w':
                    return (null, WordRanges, null);
                case 'W':
                    return (null, Complement(WordRanges), null);
                case 's':
                    return (null, SpaceRanges, null);
                case 'S':
                    return (null, Complement(SpaceRanges), null);
                case 't':
                    return (0x09, null, null);
                case 'n':
                    return (0x0A, null, null);
                case 'v':
                    return (0x0B, null, null);
                case 'f':
                    return (0x0C, null, null);
                case 'r':
                    return (0x0D, null, null);
                case '0':
                    if (Peek() is >= '0' and <= '9')
                    {
                        throw new RegexSyntaxException("Octal escape is not supported");
                    }

                    return (0, null, null);
                case >= '1' and <= '9':
                    throw new RegexSyntaxException("Backreference or octal escape is not supported");
                case 'x':
                    return (ParseFixedHex(2), null, null);
                case 'u':
                    return (ParseUnicodeEscape(), null, null);
                case '^' or '$' or '\\' or '.' or '*' or '+' or '?' or '(' or ')' or '[' or ']' or '{' or '}' or '|' or '/':
                    return (cp, null, null);
                case '-' when inClass:
                    return (cp, null, null);
                default:
                    throw new RegexSyntaxException("Unsupported escape sequence");
            }
        }

        private int ParseFixedHex(int digits)
        {
            var value = 0;
            for (var i = 0; i < digits; i++)
            {
                var hv = HexValue(Peek());
                if (hv < 0)
                {
                    throw new RegexSyntaxException("Invalid hexadecimal escape");
                }

                Next();
                value = (value << 4) | hv;
            }

            return value;
        }

        private int ParseUnicodeEscape()
        {
            int value;
            if (!Eat('{'))
            {
                value = ParseFixedHex(4);
            }
            else
            {
                value = 0;
                var count = 0;
                while (!Eof && Peek() != '}')
                {
                    var hv = HexValue(Peek());
                    if (hv < 0)
                    {
                        throw new RegexSyntaxException("Invalid braced Unicode escape");
                    }

                    Next();
                    value = value * 16 + hv;
                    count++;
                    if (value > MaxCodePoint)
                    {
                        throw new RegexSyntaxException("Unicode escape exceeds U+10FFFF");
                    }
                }

                if (count == 0 || !Eat('}'))
                {
                    throw new RegexSyntaxException("Malformed braced Unicode escape");
                }
            }

            if (value is >= 0xD800 and <= 0xDFFF)
            {
                throw new RegexSyntaxException("Surrogate code point escape is not supported");
            }

            return value;
        }

        private static int HexValue(int? cp) => cp switch
        {
            >= '0' and <= '9' => cp.Value - '0',
            >= 'A' and <= 'F' => cp.Value - 'A' + 10,
            >= 'a' and <= 'f' => cp.Value - 'a' + 10,
            _ => -1,
        };
    }

    // ------------------------------------------------------- safety check

    private static Node Unwrap(Node node)
    {
        while (node is GroupNode g)
        {
            node = g.Child;
        }

        return node;
    }

    private static bool Nullable(Node node) => node switch
    {
        EmptyNode or AnchorNode or LookaheadNode => true,
        CharSetNode => false,
        GroupNode g => Nullable(g.Child),
        RepeatNode r => r.Min == 0 || Nullable(r.Child),
        ConcatNode c => c.Items.All(Nullable),
        AltNode a => a.Branches.Any(Nullable),
        _ => false,
    };

    private static IReadOnlyList<Range> First(Node node)
    {
        switch (node)
        {
            case CharSetNode cs:
                return cs.Ranges;
            case GroupNode g:
                return First(g.Child);
            case RepeatNode r:
                return r.Max == 0 ? [] : First(r.Child);
            case AltNode a:
                return Normalize(a.Branches.SelectMany(First));
            case ConcatNode c:
            {
                var acc = new List<Range>();
                foreach (var item in c.Items)
                {
                    acc.AddRange(First(item));
                    if (!Nullable(item))
                    {
                        break;
                    }
                }

                return Normalize(acc);
            }

            default:
                return [];
        }
    }

    private static IReadOnlyList<Range> Chars(Node node) => node switch
    {
        CharSetNode cs => cs.Ranges,
        GroupNode g => Chars(g.Child),
        RepeatNode r => r.Max == 0 ? [] : Chars(r.Child),
        ConcatNode c => Normalize(c.Items.SelectMany(Chars)),
        AltNode a => Normalize(a.Branches.SelectMany(Chars)),
        _ => [],
    };

    private static bool HasUnbounded(Node node) => node switch
    {
        GroupNode g => HasUnbounded(g.Child),
        LookaheadNode l => HasUnbounded(l.Child),
        RepeatNode r => r.Max is null || HasUnbounded(r.Child),
        ConcatNode c => c.Items.Any(HasUnbounded),
        AltNode a => a.Branches.Any(HasUnbounded),
        _ => false,
    };

    private static bool HasNestedQuantifier(Node node) => node switch
    {
        GroupNode g => HasNestedQuantifier(g.Child),
        LookaheadNode l => HasNestedQuantifier(l.Child),
        RepeatNode r => ((r.Max is null || r.Max > 1) && HasUnbounded(r.Child)) || HasNestedQuantifier(r.Child),
        ConcatNode c => c.Items.Any(HasNestedQuantifier),
        AltNode a => a.Branches.Any(HasNestedQuantifier),
        _ => false,
    };

    private static bool BranchesOverlap(IReadOnlyList<Node> branches)
    {
        var firsts = branches.Select(b => First(Unwrap(b))).ToList();
        for (var i = 0; i < firsts.Count; i++)
        {
            for (var j = i + 1; j < firsts.Count; j++)
            {
                if (Overlap(firsts[i], firsts[j]))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool HasOverlappingAlternation(Node node) => node switch
    {
        GroupNode g => HasOverlappingAlternation(g.Child),
        LookaheadNode l => HasOverlappingAlternation(l.Child),
        RepeatNode r => (r.Max is null && Unwrap(r.Child) is AltNode alt && BranchesOverlap(alt.Branches))
                        || HasOverlappingAlternation(r.Child),
        ConcatNode c => c.Items.Any(HasOverlappingAlternation),
        AltNode a => a.Branches.Any(HasOverlappingAlternation),
        _ => false,
    };

    private static bool ConcatHasAdjacentOverlap(IReadOnlyList<Node> items)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (Unwrap(items[i]) is not RepeatNode { Max: null } x)
            {
                continue;
            }

            var xChars = Chars(x.Child);
            for (var j = i + 1; j < items.Count; j++)
            {
                if (Unwrap(items[j]) is RepeatNode { Max: null } y && Overlap(xChars, Chars(y.Child)))
                {
                    return true;
                }

                if (!Nullable(items[j]))
                {
                    break;
                }
            }
        }

        return false;
    }

    private static bool HasAdjacentOverlap(Node node) => node switch
    {
        GroupNode g => HasAdjacentOverlap(g.Child),
        LookaheadNode l => HasAdjacentOverlap(l.Child),
        RepeatNode r => HasAdjacentOverlap(r.Child),
        ConcatNode c => ConcatHasAdjacentOverlap(c.Items) || c.Items.Any(HasAdjacentOverlap),
        AltNode a => a.Branches.Any(HasAdjacentOverlap),
        _ => false,
    };

    // ------------------------------------------------------------ emitter

    private const string AsciiWord = "[0-9A-Z_a-z]";

    internal static string Emit(Node node)
    {
        var sb = new StringBuilder();
        Emit(node, sb);
        return sb.ToString();
    }

    private static void Emit(Node node, StringBuilder sb)
    {
        switch (node)
        {
            case EmptyNode:
                break;
            case CharSetNode cs:
                EmitCharSet(cs.Ranges, sb);
                break;
            case GroupNode g:
                sb.Append("(?:");
                Emit(g.Child, sb);
                sb.Append(')');
                break;
            case LookaheadNode l:
                sb.Append(l.Negated ? "(?!" : "(?=");
                Emit(l.Child, sb);
                sb.Append(')');
                break;
            case AnchorNode a:
                sb.Append(a.Anchor switch
                {
                    Anchor.Start => @"\A",
                    Anchor.End => @"\z",
                    Anchor.WordBoundary => $"(?:(?<={AsciiWord})(?!{AsciiWord})|(?<!{AsciiWord})(?={AsciiWord}))",
                    _ => $"(?:(?<={AsciiWord})(?={AsciiWord})|(?<!{AsciiWord})(?!{AsciiWord}))",
                });
                break;
            case ConcatNode c:
                foreach (var item in c.Items)
                {
                    Emit(item, sb);
                }

                break;
            case AltNode alt:
                sb.Append("(?:");
                for (var i = 0; i < alt.Branches.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append('|');
                    }

                    Emit(alt.Branches[i], sb);
                }

                sb.Append(')');
                break;
            case RepeatNode r:
                sb.Append("(?:");
                Emit(r.Child, sb);
                sb.Append(')');
                sb.Append(CultureInfo.InvariantCulture, $"{{{r.Min},{(r.Max is { } max ? max.ToString(CultureInfo.InvariantCulture) : string.Empty)}}}");
                if (r.Lazy)
                {
                    sb.Append('?');
                }

                break;
        }
    }

    private static void EmitCharSet(IReadOnlyList<Range> ranges, StringBuilder sb)
    {
        var bmp = ranges.Where(r => r.Start <= 0xFFFF).Select(r => new Range(r.Start, Math.Min(r.End, 0xFFFF))).ToList();
        var astral = ranges.Where(r => r.End > 0xFFFF).Select(r => new Range(Math.Max(r.Start, 0x10000), r.End)).ToList();
        var alternatives = new List<string>();
        if (bmp.Count > 0)
        {
            var cls = new StringBuilder("[");
            foreach (var r in bmp)
            {
                cls.Append(CultureInfo.InvariantCulture, $"\\u{r.Start:X4}");
                if (r.End != r.Start)
                {
                    cls.Append(CultureInfo.InvariantCulture, $"-\\u{r.End:X4}");
                }
            }

            alternatives.Add(cls.Append(']').ToString());
        }

        foreach (var r in astral)
        {
            var (hs, ls) = Split(r.Start);
            var (he, le) = Split(r.End);
            if (hs == he)
            {
                alternatives.Add($"\\u{hs:X4}[\\u{ls:X4}-\\u{le:X4}]");
                continue;
            }

            alternatives.Add($"\\u{hs:X4}[\\u{ls:X4}-\\uDFFF]");
            if (he - hs > 1)
            {
                alternatives.Add($"[\\u{hs + 1:X4}-\\u{he - 1:X4}][\\uDC00-\\uDFFF]");
            }

            alternatives.Add($"\\u{he:X4}[\\uDC00-\\u{le:X4}]");
        }

        if (alternatives.Count == 0)
        {
            sb.Append("(?!)");
        }
        else if (alternatives.Count == 1)
        {
            sb.Append(alternatives[0]);
        }
        else
        {
            sb.Append("(?:").Append(string.Join('|', alternatives)).Append(')');
        }
    }

    private static (int High, int Low) Split(int cp)
    {
        var v = cp - 0x10000;
        return (0xD800 + (v >> 10), 0xDC00 + (v & 0x3FF));
    }
}
