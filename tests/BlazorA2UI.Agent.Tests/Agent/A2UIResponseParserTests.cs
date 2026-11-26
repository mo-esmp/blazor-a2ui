using System.Text;
using BlazorA2UI.Catalogs;
using BlazorA2UI.Processing;

namespace BlazorA2UI.Tests.Agent;

/// <summary>
/// Tests for <see cref="A2UIResponseParser"/> and <see cref="A2UIResponseStream"/>.
/// </summary>
public sealed class A2UIResponseParserTests
{
    /// <summary>
    /// A createSurface message for the basic catalog.
    /// </summary>
    private const string Create = """{"version":"1.0","createSurface":{"surfaceId":"main","catalogId":"https://a2ui.org/specification/v1_0/catalogs/basic/catalog.json"}}""";

    /// <summary>
    /// An updateComponents message that draws a text on the surface of <see cref="Create"/>.
    /// </summary>
    private const string Update = """{"version":"1.0","updateComponents":{"surfaceId":"main","components":[{"id":"root","component":"Text","text":"Hi"}]}}""";

    /// <summary>
    /// Answers that every way of chunking must read the same, for <see cref="Append_AnyChunking_ReadsLikeWholeAnswer"/>.
    /// </summary>
    public static TheoryData<string> Answers => new()
    {
        $"Here you go:\n<a2ui-json>\n[{Create},{Update}]\n</a2ui-json>\nAnything else?",
        $"Intro\n```json\n<a2ui-json>[{Create}]</a2ui-json>\n```\nBye",
        $"<A2UI-JSON type=\"x\">\n```json\n{Create}\n```\n</a2ui-json >",
        $"a < b and <b>bold</b> <a2ui-json>[{Create}]</a2ui-json> 2 < 3",
        """<a2ui-json>[{"version":"1.0","updateDataModel":{"surfaceId":"main","path":"/t","value":"</a2ui-json> \"quoted\""}}]</a2ui-json>done""",
        $"<a2ui-json>[{Create}, 42]</a2ui-json>after",
        $"<a2ui-json>[{Create},]</a2ui-json><a2ui-json>[{Update}",
        "Text with ``` inline fence and `code` and <a2ui and <a2ui-jsonx>.",
        "<a2ui-json>/* a \"} ** */[// first }\n{\"deleteSurface\": /* \"{ */ {\"surfaceId\":\"m\"}}, /**/]</a2ui-json>end",
    };

    /// <summary>
    /// Proves that text and blocks are returned in order, with the whitespace around blocks dropped.
    /// </summary>
    [Fact]
    public void Parse_TextAndBlock_ReturnsPartsInOrder()
    {
        // Arrange
        string answer = $"Here you go:\n<a2ui-json>\n[{Create},{Update}]\n</a2ui-json>\nAnything else?";

        // Act
        IReadOnlyList<A2UIResponsePart> parts = A2UIResponseParser.Parse(answer);

        // Assert
        Describe(parts).ShouldBe(["T:Here you go:", $"M:{Create}", $"M:{Update}", "T:Anything else?"]);
    }

    /// <summary>
    /// Proves that an answer with no block is one text part, and an empty answer has no part.
    /// </summary>
    [Fact]
    public void Parse_TextOnly_ReturnsOneTextPart()
    {
        // Arrange
        string answer = "  Just text, with a < sign and <b>markup</b>.  ";

        // Act
        IReadOnlyList<A2UIResponsePart> parts = A2UIResponseParser.Parse(answer);

        // Assert
        Describe(parts).ShouldBe(["T:Just text, with a < sign and <b>markup</b>."]);
        A2UIResponseParser.Parse(string.Empty).ShouldBeEmpty();
    }

    /// <summary>
    /// Proves that Markdown fences inside and around a block are dropped.
    /// </summary>
    [Fact]
    public void Parse_Fences_AreDropped()
    {
        // Arrange
        string answer = $"Intro\n```json\n<a2ui-json>\n```json\n[{Create}]\n```\n</a2ui-json>\n```\nBye";

        // Act
        IReadOnlyList<A2UIResponsePart> parts = A2UIResponseParser.Parse(answer);

        // Assert
        Describe(parts).ShouldBe(["T:Intro", $"M:{Create}", "T:Bye"]);
    }

    /// <summary>
    /// Proves that tags in another case, with attributes or with whitespace before the <c>&gt;</c> are read.
    /// </summary>
    [Fact]
    public void Parse_TagVariants_AreRead()
    {
        // Arrange
        string answer = $"<A2UI-JSON lang=\"json\">[{Create}]</A2UI-Json \n>";

        // Act
        IReadOnlyList<A2UIResponsePart> parts = A2UIResponseParser.Parse(answer);

        // Assert
        Describe(parts).ShouldBe([$"M:{Create}"]);
    }

    /// <summary>
    /// Proves that a tag whose name only starts like the open tag is text.
    /// </summary>
    [Fact]
    public void Parse_LongerTagName_IsText()
    {
        // Arrange
        string answer = "<a2ui-jsonx>[]</a2ui-jsonx>";

        // Act
        IReadOnlyList<A2UIResponsePart> parts = A2UIResponseParser.Parse(answer);

        // Assert
        Describe(parts).ShouldBe(["T:<a2ui-jsonx>[]</a2ui-jsonx>"]);
    }

    /// <summary>
    /// Proves that a block may hold a single message instead of an array.
    /// </summary>
    [Fact]
    public void Parse_SingleMessage_IsRead()
    {
        // Arrange
        string answer = $"<a2ui-json>{Create}</a2ui-json>";

        // Act
        IReadOnlyList<A2UIResponsePart> parts = A2UIResponseParser.Parse(answer);

        // Assert
        Describe(parts).ShouldBe([$"M:{Create}"]);
    }

    /// <summary>
    /// Proves that a missing version is added first, as the renderer expects.
    /// </summary>
    [Fact]
    public void Parse_MissingVersion_AddsVersionFirst()
    {
        // Arrange
        string answer = """<a2ui-json>[{"deleteSurface":{"surfaceId":"main"}}]</a2ui-json>""";

        // Act
        IReadOnlyList<A2UIResponsePart> parts = A2UIResponseParser.Parse(answer);

        // Assert
        Describe(parts).ShouldBe(["""M:{"version":"1.0","deleteSurface":{"surfaceId":"main"}}"""]);
    }

    /// <summary>
    /// Proves that a close tag inside a JSON string does not end the block.
    /// </summary>
    [Fact]
    public void Parse_CloseTagInsideString_DoesNotEndBlock()
    {
        // Arrange
        string answer = """<a2ui-json>[{"version":"1.0","updateDataModel":{"surfaceId":"main","path":"/t","value":"a </a2ui-json> \"b\""}}]</a2ui-json>done""";

        // Act
        IReadOnlyList<A2UIResponsePart> parts = A2UIResponseParser.Parse(answer);

        // Assert
        parts.Count.ShouldBe(2);
        parts[0].ShouldBeOfType<A2UIMessagePart>().Message["updateDataModel"]!["value"]!.GetValue<string>().ShouldBe("a </a2ui-json> \"b\"");
        parts[1].ShouldBe(new A2UITextPart("done"));
    }

    /// <summary>
    /// Proves that trailing commas and comments are forgiven.
    /// </summary>
    [Fact]
    public void Parse_TrailingCommasAndComments_AreForgiven()
    {
        // Arrange
        string answer = """
            <a2ui-json>
            [
              // Remove the surface.
              {"version":"1.0","deleteSurface":{"surfaceId":"main",},},
            ]
            </a2ui-json>
            """;

        // Act
        IReadOnlyList<A2UIResponsePart> parts = A2UIResponseParser.Parse(answer);

        // Assert
        Describe(parts).ShouldBe(["""M:{"version":"1.0","deleteSurface":{"surfaceId":"main"}}"""]);
    }

    /// <summary>
    /// Proves that comments holding quotes and braces do not confuse the reader, inside or between messages.
    /// </summary>
    [Fact]
    public void Parse_CommentsWithJsonCharacters_AreSkipped()
    {
        // Arrange
        string answer = "<a2ui-json>/* a \"} ** */[// first }\n{\"deleteSurface\": /* \"{ */ {\"surfaceId\":\"m\"}}, /**/]</a2ui-json>end";

        // Act
        IReadOnlyList<A2UIResponsePart> parts = A2UIResponseParser.Parse(answer);

        // Assert
        Describe(parts).ShouldBe(["""M:{"version":"1.0","deleteSurface":{"surfaceId":"m"}}""", "T:end"]);
    }

    /// <summary>
    /// Proves that raw line breaks and tabs inside strings are read as escaped.
    /// </summary>
    [Fact]
    public void Parse_RawControlCharactersInString_AreEscaped()
    {
        // Arrange
        string answer = "<a2ui-json>[{\"version\":\"1.0\",\"updateDataModel\":{\"surfaceId\":\"main\",\"path\":\"/t\",\"value\":\"line 1\nline\t2\"}}]</a2ui-json>";

        // Act
        IReadOnlyList<A2UIResponsePart> parts = A2UIResponseParser.Parse(answer);

        // Assert
        parts.Single().ShouldBeOfType<A2UIMessagePart>().Message["updateDataModel"]!["value"]!.GetValue<string>().ShouldBe("line 1\nline\t2");
    }

    /// <summary>
    /// Proves that curly double quotes used as JSON quotes are straightened.
    /// </summary>
    [Fact]
    public void Parse_CurlyQuotes_AreStraightened()
    {
        // Arrange
        string answer = "<a2ui-json>[{\u201Cversion\u201D:\u201C1.0\u201D,\u201CdeleteSurface\u201D:{\u201CsurfaceId\u201D:\u201Cmain\u201D}}]</a2ui-json>";

        // Act
        IReadOnlyList<A2UIResponsePart> parts = A2UIResponseParser.Parse(answer);

        // Assert
        Describe(parts).ShouldBe(["""M:{"version":"1.0","deleteSurface":{"surfaceId":"main"}}"""]);
    }

    /// <summary>
    /// Proves that a message that is not allowed is an error and the rest of its block is skipped, but earlier
    /// messages and later text are kept.
    /// </summary>
    [Fact]
    public void Parse_DisallowedMessage_ReportsAndSkipsRestOfBlock()
    {
        // Arrange
        string call = """{"version":"1.0","callRendererFunction":{"functionCallId":"1","surfaceId":"main","call":{"@call":"openUrl"}}}""";
        string answer = $"<a2ui-json>[{Create},{call},{Update}]</a2ui-json>after";

        // Act
        IReadOnlyList<A2UIResponsePart> parts = A2UIResponseParser.Parse(answer);

        // Assert
        Describe(parts).ShouldBe([$"M:{Create}", "E:A2UI message 2 is a 'callRendererFunction' message, which is not allowed here.", "T:after"]);
    }

    /// <summary>
    /// Proves that a message is read when the options allow it.
    /// </summary>
    [Fact]
    public void Parse_AllowedMessage_IsRead()
    {
        // Arrange
        string response = """{"version":"1.0","agentFunctionResponse":{"functionCallId":"1","value":2}}""";
        A2UIResponseParserOptions options = new() { AllowedMessages = A2UIAgentMessages.All };

        // Act
        IReadOnlyList<A2UIResponsePart> parts = A2UIResponseParser.Parse($"<a2ui-json>[{response}]</a2ui-json>", options);

        // Assert
        Describe(parts).ShouldBe([$"M:{response}"]);
    }

    /// <summary>
    /// Proves that each kind of wrong envelope is reported.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="error">The end of the error expected.</param>
    [Theory]
    [InlineData("""{"version":"0.9","deleteSurface":{"surfaceId":"m"}}""", "must have \"version\": \"1.0\".")]
    [InlineData("""{"version":"1.0"}""", "must hold a message, such as 'createSurface' or 'updateComponents'.")]
    [InlineData("""{"deleteSurface":{"surfaceId":"m"},"updateDataModel":{"surfaceId":"m"}}""", "must hold exactly one message, such as 'createSurface' or 'updateComponents'.")]
    [InlineData("""{"showChart":{}}""", "has the unknown message 'showChart'.")]
    [InlineData("""{"deleteSurface":{"surfaceId":"m"},"deleteSurface":{"surfaceId":"n"}}""", "is not valid JSON:")]
    public void Parse_WrongEnvelope_ReportsError(string message, string error)
    {
        // Arrange
        string answer = $"<a2ui-json>[{message}]</a2ui-json>";

        // Act
        IReadOnlyList<A2UIResponsePart> parts = A2UIResponseParser.Parse(answer);

        // Assert
        string text = parts.Single().ShouldBeOfType<A2UIErrorPart>().Error;
        text.ShouldStartWith("A2UI message 1 ");
        text.ShouldContain(error);
    }

    /// <summary>
    /// Proves that broken blocks are reported and the text after them is kept.
    /// </summary>
    /// <param name="block">The content of the block.</param>
    /// <param name="error">The start of the error expected.</param>
    [Theory]
    [InlineData("", "An A2UI JSON block is empty.")]
    [InlineData("  \n ", "An A2UI JSON block is empty.")]
    [InlineData("""[{"deleteSurface":{"surfaceId":"m"}""", "An A2UI JSON block closed before its JSON was complete.")]
    [InlineData("""Sure! [{"deleteSurface":{"surfaceId":"m"}}]""", "An A2UI JSON block must hold a JSON array of messages.")]
    [InlineData("""[{"deleteSurface":{"surfaceId":"m"}}] and more""", "The A2UI JSON block has text after its JSON.")]
    [InlineData("""["deleteSurface"]""", "Each item of an A2UI JSON block must be a message object.")]
    [InlineData("""[[{"deleteSurface":{"surfaceId":"m"}}]]""", "Each item of an A2UI JSON block must be a message object.")]
    [InlineData("""[{"deleteSurface":{"surfaceId":m}}]""", "A2UI message 1 is not valid JSON:")]
    public void Parse_BrokenBlock_ReportsErrorAndKeepsText(string block, string error)
    {
        // Arrange
        string answer = $"<a2ui-json>{block}</a2ui-json>after";

        // Act
        IReadOnlyList<A2UIResponsePart> parts = A2UIResponseParser.Parse(answer);

        // Assert
        parts.Last().ShouldBe(new A2UITextPart("after"));
        parts.OfType<A2UIErrorPart>().Single().Error.ShouldStartWith(error);
    }

    /// <summary>
    /// Proves that an answer that ends inside a block reports it, after the messages that were complete.
    /// </summary>
    [Fact]
    public void Parse_UnclosedBlock_ReportsAfterCompleteMessages()
    {
        // Arrange
        string answer = $"<a2ui-json>[{Create},{{\"deleteSurface\":";

        // Act
        IReadOnlyList<A2UIResponsePart> parts = A2UIResponseParser.Parse(answer);

        // Assert
        parts.Count.ShouldBe(2);
        parts[0].ShouldBeOfType<A2UIMessagePart>();
        parts[1].ShouldBeOfType<A2UIErrorPart>().Error.ShouldContain(A2UIResponseParser.CloseTag);
    }

    /// <summary>
    /// Proves that an answer whose JSON closed but whose close tag never came keeps its messages without an error.
    /// </summary>
    [Fact]
    public void Parse_MissingCloseTagAfterCompleteJson_KeepsMessages()
    {
        // Arrange
        string answer = $"<a2ui-json>[{Create}]";

        // Act
        IReadOnlyList<A2UIResponsePart> parts = A2UIResponseParser.Parse(answer);

        // Assert
        Describe(parts).ShouldBe([$"M:{Create}"]);
    }

    /// <summary>
    /// Proves that messages past the limit are refused.
    /// </summary>
    [Fact]
    public void Parse_TooManyMessages_ReportsError()
    {
        // Arrange
        A2UIResponseParserOptions options = new() { MaxMessages = 1 };
        string answer = $"<a2ui-json>[{Create}]</a2ui-json><a2ui-json>[{Update}]</a2ui-json>";

        // Act
        IReadOnlyList<A2UIResponsePart> parts = A2UIResponseParser.Parse(answer, options);

        // Assert
        Describe(parts).ShouldBe([$"M:{Create}", "E:The answer holds more than 1 A2UI messages."]);
    }

    /// <summary>
    /// Proves that a block past the length limit is refused and skipped up to its close tag.
    /// </summary>
    [Fact]
    public void Parse_BlockTooLong_ReportsErrorAndSkipsBlock()
    {
        // Arrange
        A2UIResponseParserOptions options = new() { MaxBlockLength = 100 };
        string answer = $"<a2ui-json>[{Create},{Update}]</a2ui-json>after";

        // Act
        IReadOnlyList<A2UIResponsePart> parts = A2UIResponseParser.Parse(answer, options);

        // Assert
        Describe(parts).ShouldBe(["E:The A2UI JSON block is longer than 100 characters.", "T:after"]);
    }

    /// <summary>
    /// Proves that JSON nested past the renderer's depth limit is refused without being parsed.
    /// </summary>
    [Fact]
    public void Parse_TooDeep_ReportsError()
    {
        // Arrange
        string deep = new string('[', 200) + new string(']', 200);
        string answer = $"<a2ui-json>[{{\"version\":\"1.0\",\"updateDataModel\":{{\"surfaceId\":\"m\",\"value\":{deep}}}}}]</a2ui-json>";

        // Act
        IReadOnlyList<A2UIResponsePart> parts = A2UIResponseParser.Parse(answer);

        // Assert
        parts.Single().ShouldBeOfType<A2UIErrorPart>().Error.ShouldContain("nested more than");
    }

    /// <summary>
    /// Proves that null input is refused.
    /// </summary>
    [Fact]
    public void Parse_Null_Throws()
    {
        // Arrange
        string response = null!; // Deliberately null to test the guard.

        // Act
        Action act = () => A2UIResponseParser.Parse(response);

        // Assert
        act.ShouldThrow<ArgumentNullException>();
    }

    /// <summary>
    /// Proves that messages from a basic catalog surface, read back from an answer, pass the renderer's validation.
    /// </summary>
    [Fact]
    public void Parse_BuiltMessages_PassValidation()
    {
        // Arrange
        Column root = new() { Children = [new Text { Content = "Name" }, new TextField { Label = "Name", Value = A2UIValue.Path("/name") }] };
        JsonArray block = [A2UIMessages.CreateSurface("main", root), A2UIMessages.UpdateDataModel("main", "/name", "Ada")];
        using A2UIMessageValidator validator = new([BasicCatalog.Create()]);

        // Act
        IReadOnlyList<A2UIResponsePart> parts = A2UIResponseParser.Parse($"Form:\n<a2ui-json>{block.ToJsonString()}</a2ui-json>");

        // Assert
        A2UIMessagePart[] messages = [.. parts.OfType<A2UIMessagePart>()];
        messages.Length.ShouldBe(2);
        foreach (A2UIMessagePart message in messages)
        {
            validator.Validate(message.Message).IsValid.ShouldBeTrue();
        }
    }

    /// <summary>
    /// Proves that untagged JSON is read as an array of messages or a single message.
    /// </summary>
    [Fact]
    public void ParseJson_ArrayOrObject_ReadsMessages()
    {
        // Arrange
        string array = $" [{Create},{Update}] ";

        // Act
        IReadOnlyList<A2UIResponsePart> fromArray = A2UIResponseParser.ParseJson(array);
        IReadOnlyList<A2UIResponsePart> fromObject = A2UIResponseParser.ParseJson(Create);

        // Assert
        Describe(fromArray).ShouldBe([$"M:{Create}", $"M:{Update}"]);
        Describe(fromObject).ShouldBe([$"M:{Create}"]);
    }

    /// <summary>
    /// Proves that untagged JSON that is empty, incomplete or not JSON is reported.
    /// </summary>
    /// <param name="json">The JSON.</param>
    /// <param name="error">The start of the error expected.</param>
    [Theory]
    [InlineData("", "The JSON is empty.")]
    [InlineData("""[{"deleteSurface":{"surfaceId":"m"}}""", "The JSON ended before it was complete.")]
    [InlineData("hello", "An A2UI JSON block must hold a JSON array of messages.")]
    public void ParseJson_Broken_ReportsError(string json, string error)
    {
        // Arrange
        A2UIResponseParserOptions options = new();

        // Act
        IReadOnlyList<A2UIResponsePart> parts = A2UIResponseParser.ParseJson(json, options);

        // Assert
        parts.OfType<A2UIErrorPart>().Single().Error.ShouldStartWith(error);
    }

    /// <summary>
    /// Proves that untagged JSON keeps a tag-like string as data.
    /// </summary>
    [Fact]
    public void ParseJson_CloseTagText_IsData()
    {
        // Arrange
        string json = """{"updateDataModel":{"surfaceId":"m","path":"/t","value":"</a2ui-json>"}}""";

        // Act
        IReadOnlyList<A2UIResponsePart> parts = A2UIResponseParser.ParseJson(json);

        // Assert
        parts.Single().ShouldBeOfType<A2UIMessagePart>().Message["updateDataModel"]!["value"]!.GetValue<string>().ShouldBe("</a2ui-json>");
    }

    /// <summary>
    /// Proves that reading an answer one character at a time, or in random chunks, gives the same parts as reading it
    /// whole.
    /// </summary>
    /// <param name="answer">The answer.</param>
    [Theory]
    [MemberData(nameof(Answers))]
    public void Append_AnyChunking_ReadsLikeWholeAnswer(string answer)
    {
        // Arrange
        string[] expected = Describe(A2UIResponseParser.Parse(answer));
        Random random = new(answer.Length);

        // Act
        string[] perCharacter = Describe(Stream(answer.Select(c => c.ToString())));
        List<string[]> randomChunks = [];
        for (int run = 0; run < 20; run++)
        {
            randomChunks.Add(Describe(Stream(Split(answer, random))));
        }

        // Assert
        perCharacter.ShouldBe(expected);
        foreach (string[] parts in randomChunks)
        {
            parts.ShouldBe(expected);
        }
    }

    /// <summary>
    /// Proves that each message is returned as soon as its object closes, before the block ends.
    /// </summary>
    [Fact]
    public void Append_MessageClosed_ReturnsItAtOnce()
    {
        // Arrange
        A2UIResponseStream stream = new();

        // Act
        IReadOnlyList<A2UIResponsePart> first = stream.Append($"Hello <a2ui-json>[{Create},");
        IReadOnlyList<A2UIResponsePart> second = stream.Append(Update[..20]);
        IReadOnlyList<A2UIResponsePart> third = stream.Append(Update[20..] + "]</a2ui-json> bye");
        IReadOnlyList<A2UIResponsePart> last = stream.Complete();

        // Assert
        Describe(first).ShouldBe(["T:Hello", $"M:{Create}"]);
        second.ShouldBeEmpty();
        Describe(third).ShouldBe([$"M:{Update}", "T:bye"]);
        last.ShouldBeEmpty();
    }

    /// <summary>
    /// Proves that text that may start a tag is held back until it is known, and returned once it is not a tag.
    /// </summary>
    [Fact]
    public void Append_PossibleTag_IsHeldBack()
    {
        // Arrange
        A2UIResponseStream stream = new();

        // Act
        IReadOnlyList<A2UIResponsePart> first = stream.Append("Look: <a2ui");
        IReadOnlyList<A2UIResponsePart> second = stream.Append("-jsonic is a word");

        // Assert
        Describe(first).ShouldBe(["T:Look:"]);
        Describe(second).ShouldBe(["T: <a2ui-jsonic is a word"]);
    }

    /// <summary>
    /// Proves that a long text never containing a tag is not held back.
    /// </summary>
    [Fact]
    public void Append_PlainText_IsReturnedAtOnce()
    {
        // Arrange
        A2UIResponseStream stream = new();

        // Act
        IReadOnlyList<A2UIResponsePart> parts = stream.Append("Plain text.");

        // Assert
        Describe(parts).ShouldBe(["T:Plain text."]);
    }

    /// <summary>
    /// Proves that a long block streamed in small chunks keeps reading correctly as read text is dropped.
    /// </summary>
    [Fact]
    public void Append_ManyMessagesInSmallChunks_ReadsAll()
    {
        // Arrange
        StringBuilder answer = new("<a2ui-json>[");
        for (int i = 0; i < 300; i++)
        {
            answer.Append($$$"""{"updateDataModel":{"surfaceId":"main","path":"/n{{{i}}}","value":"{{{new string('x', i % 17)}}}"}},""");
        }

        answer.Append("]</a2ui-json>");
        string text = answer.ToString();
        A2UIResponseStream stream = new();
        List<A2UIResponsePart> parts = [];

        // Act
        for (int i = 0; i < text.Length; i += 7)
        {
            parts.AddRange(stream.Append(text.Substring(i, Math.Min(7, text.Length - i))));
        }

        parts.AddRange(stream.Complete());

        // Assert
        parts.Count.ShouldBe(300);
        parts[299].ShouldBeOfType<A2UIMessagePart>().Message["updateDataModel"]!["path"]!.GetValue<string>().ShouldBe("/n299");
    }

    /// <summary>
    /// Proves that the stream refuses chunks after it is complete, and completing twice returns nothing.
    /// </summary>
    [Fact]
    public void Append_AfterComplete_Throws()
    {
        // Arrange
        A2UIResponseStream stream = new();
        stream.Append("Hi");
        stream.Complete();

        // Act
        Action act = () => stream.Append("more");

        // Assert
        act.ShouldThrow<InvalidOperationException>();
        stream.Complete().ShouldBeEmpty();
    }

    /// <summary>
    /// Reads an answer from chunks with a new stream.
    /// </summary>
    /// <param name="chunks">The chunks.</param>
    private static List<A2UIResponsePart> Stream(IEnumerable<string> chunks)
    {
        A2UIResponseStream stream = new();
        List<A2UIResponsePart> parts = [];
        foreach (string chunk in chunks)
        {
            parts.AddRange(stream.Append(chunk));
        }

        parts.AddRange(stream.Complete());
        return parts;
    }

    /// <summary>
    /// Splits text into chunks of random lengths from 1 to 12.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="random">The random source.</param>
    private static IEnumerable<string> Split(string text, Random random)
    {
        int i = 0;
        while (i < text.Length)
        {
            int length = Math.Min(random.Next(1, 13), text.Length - i);
            yield return text.Substring(i, length);
            i += length;
        }
    }

    /// <summary>
    /// Describes parts as strings for comparison, joining neighboring text parts as a reader would.
    /// </summary>
    /// <param name="parts">The parts.</param>
    private static string[] Describe(IEnumerable<A2UIResponsePart> parts)
    {
        List<string> result = [];
        StringBuilder? text = null;
        foreach (A2UIResponsePart part in parts)
        {
            if (part is A2UITextPart piece)
            {
                (text ??= new StringBuilder()).Append(piece.Text);
                continue;
            }

            if (text is not null)
            {
                result.Add("T:" + text);
                text = null;
            }

            result.Add(part switch
            {
                A2UIMessagePart message => "M:" + message.Message.ToJsonString(),
                A2UIErrorPart error => "E:" + error.Error,
                _ => throw new InvalidOperationException("Unknown part."),
            });
        }

        if (text is not null)
        {
            result.Add("T:" + text);
        }

        return [.. result];
    }
}
