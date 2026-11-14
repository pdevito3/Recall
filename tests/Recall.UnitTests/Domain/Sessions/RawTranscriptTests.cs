namespace Recall.UnitTests.Domain.Sessions;

using Recall.Server.Domain.Sessions.Content;
using Shouldly;

public class RawTranscriptTests
{
    private const string TypedParagraph = """{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"typed by hand"}]}]}""";
    private const string Heading = """{"type":"doc","content":[{"type":"heading","attrs":{"level":2},"content":[{"type":"text","text":"Title"}]}]}""";
    private const string AfterMention = """{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"met "},{"type":"mention","attrs":{"personId":"per_1","label":"Ann"}}]}]}""";

    public static IEnumerable<Func<(string Raw, string Words, string Before)>> AppendsBeforeNewParagraph()
    {
        yield return () => ("", "first words", """{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"first words"}]}]}""");
        yield return () => ("   ", "words", """{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"words"}]}]}""");
        yield return () => ("""{"type":"doc","content":[{"type":"paragraph"}]}""", "words", """{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"words"}]}]}""");
        yield return () => (TypedParagraph, "heard words, don't — käsi “quoted” <b>& 😀 сказал",
            """{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"typed by hand"}]},{"type":"paragraph","content":[{"type":"text","text":"heard words, don't — käsi “quoted” <b>& \uD83D\uDE00 сказал"}]}]}""");
        yield return () => (Heading, "next words",
            """{"type":"doc","content":[{"type":"heading","attrs":{"level":2},"content":[{"type":"text","text":"Title"}]},{"type":"paragraph","content":[{"type":"text","text":"next words"}]}]}""");
        yield return () => (AfterMention, "after words",
            """{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"met "},{"type":"mention","attrs":{"personId":"per_1","label":"Ann"}}]},{"type":"paragraph","content":[{"type":"text","text":"after words"}]}]}""");
        yield return () => ("""{"type":"doc"}""", "words", """{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"words"}]}]}""");
        yield return () => ("""{"type":"doc","content":null}""", "words", """{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"words"}]}]}""");
        yield return () => ("typed by hand", "heard words", "typed by hand\n\nheard words");
    }

    [Test]
    [Property("Rule", "REC-27")]
    [MethodDataSource(nameof(AppendsBeforeNewParagraph))]
    public void a_chunk_of_its_own_gives_raw_as_before_byte_for_byte(string raw, string words, string before)
    {
        // Act
        var text = RawTranscript.Append(raw, ChunkParagraphs.Own(words));

        // Assert
        text.ShouldBe(before);
    }

    public static IEnumerable<Func<(string Case, string Raw, string After)>> Joins()
    {
        yield return () => ("into the text at the end of a paragraph", TypedParagraph,
            """{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"typed by hand more words"}]},{"type":"paragraph","content":[{"type":"text","text":"new thought"}]}]}""");
        yield return () => ("after a space the person typed", """{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"typed "}]}]}""",
            """{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"typed more words"}]},{"type":"paragraph","content":[{"type":"text","text":"new thought"}]}]}""");
        yield return () => ("after a mention", AfterMention,
            """{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"met "},{"type":"mention","attrs":{"personId":"per_1","label":"Ann"}},{"type":"text","text":" more words"}]},{"type":"paragraph","content":[{"type":"text","text":"new thought"}]}]}""");
        yield return () => ("after marked text", """{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"bold","marks":[{"type":"bold"}]}]}]}""",
            """{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"bold","marks":[{"type":"bold"}]},{"type":"text","text":" more words"}]},{"type":"paragraph","content":[{"type":"text","text":"new thought"}]}]}""");
        yield return () => ("not into a heading", Heading,
            """{"type":"doc","content":[{"type":"heading","attrs":{"level":2},"content":[{"type":"text","text":"Title"}]},{"type":"paragraph","content":[{"type":"text","text":"more words"}]},{"type":"paragraph","content":[{"type":"text","text":"new thought"}]}]}""");
        yield return () => ("into the one empty paragraph", """{"type":"doc","content":[{"type":"paragraph"}]}""",
            """{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"more words"}]},{"type":"paragraph","content":[{"type":"text","text":"new thought"}]}]}""");
        yield return () => ("into an empty Raw", "",
            """{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"more words"}]},{"type":"paragraph","content":[{"type":"text","text":"new thought"}]}]}""");
        yield return () => ("into plain Raw", "typed by hand", "typed by hand more words\n\nnew thought");
    }

    [Test]
    [Property("Rule", "REC-27")]
    [MethodDataSource(nameof(Joins))]
    public void a_chunk_that_joins_goes_on_in_the_last_paragraph_after_a_space(string @case, string raw, string after)
    {
        // Arrange
        var chunk = new ChunkParagraphs(true, ["more words", "new thought"]);

        // Act
        var text = RawTranscript.Append(raw, chunk);

        // Assert
        text.ShouldBe(after);
    }

    [Test]
    [Property("Rule", "REC-27")]
    public void a_chunk_that_does_not_join_starts_a_paragraph_for_each_of_its_paragraphs()
    {
        // Arrange
        var chunk = new ChunkParagraphs(false, ["more words", "new thought"]);

        // Act
        var document = RawTranscript.Append(TypedParagraph, chunk);
        var plain = RawTranscript.Append("typed by hand", chunk);

        // Assert
        ProseMirrorToPlainText.Render(document).ShouldBe("typed by hand\nmore words\nnew thought");
        plain.ShouldBe("typed by hand\n\nmore words\n\nnew thought");
    }
}
