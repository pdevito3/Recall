namespace Recall.UnitTests.Domain.Sessions.Content;

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Recall.Server.Domain.Sessions.Content;
using Shouldly;

/// <summary>The rule of the converter: each word of the Markdown is in the document.</summary>
public partial class MarkdownWordsTests
{
    private const int RandomSeed = 20261107;
    private const int RandomInputs = 3_000;

    private static List<string> WordsIn(string json)
    {
        var words = new List<string>();
        Collect(JsonNode.Parse(json)!.AsObject(), words);
        return words;
    }

    private static void Collect(JsonObject node, List<string> words)
    {
        var text = node["text"]?.GetValue<string>() ?? string.Empty;
        var hrefs = (node["marks"] as JsonArray)?.OfType<JsonObject>().Select(mark => mark["attrs"]?["href"]?.GetValue<string>() ?? string.Empty) ?? [];
        var language = node["attrs"]?["language"]?.GetValue<string>() ?? string.Empty;
        foreach (var value in hrefs.Append(text).Append(language))
            words.AddRange(Word().Matches(value).Select(match => match.Value));
        foreach (var child in (node["content"] as JsonArray ?? []).OfType<JsonObject>())
            Collect(child, words);
    }

    private static IEnumerable<string> TypesIn(JsonObject node)
    {
        yield return node["type"]!.GetValue<string>();
        foreach (var type in (node["content"] as JsonArray ?? []).OfType<JsonObject>().SelectMany(TypesIn))
            yield return type;
    }

    public static IEnumerable<Func<(string Markdown, string[] Words, string NodeType)>> EachForm()
    {
        (string, string[], string)[] forms =
        [
            ("plain w1 words w2", ["w1", "w2"], "paragraph"),
            ("# w1\n\n###### w2", ["w1", "w2"], "heading"),
            ("w1\n===\n\nw2\n---", ["w1", "w2"], "heading"),
            ("> w1\n>\n> > w2", ["w1", "w2"], "blockquote"),
            ("- w1\n- w2\n  - w3", ["w1", "w2", "w3"], "bulletList"),
            ("1. w1\n2. w2", ["w1", "w2"], "orderedList"),
            ("3. w1\n4. w2", ["3", "4", "w1", "w2"], "paragraph"),
            ("2003. w1", ["2003", "w1"], "paragraph"),
            ("1999. w1\n1999. w2", ["1999", "w1", "w2"], "paragraph"),
            ("1. [ ] w1\n2. [x] w2", ["1", "2", "w1", "w2"], "paragraph"),
            ("2003. [ ] w1", ["2003", "w1"], "paragraph"),
            ("1. w1\n1. w2\n1. w3", ["w1", "w2", "w3"], "orderedList"),
            ("1999. w1\n2003. w2\n2011. w3", ["1999", "2003", "2011", "w1", "w2", "w3"], "paragraph"),
            ("- [ ] w1\n- [x] w2", ["w1", "w2"], "taskList"),
            ("- [ ] w1\n- w2", ["w1", "w2"], "paragraph"),
            ("+ 5 degrees\n+ 6", ["5", "6"], "paragraph"),
            ("Multiply 2*3*4 to get 24, and 5*3=15", ["2", "3", "4", "5", "15"], "paragraph"),
            ("10**2**3", ["10", "2", "3"], "paragraph"),
            ("a~~b~~c", ["a", "b", "c"], "paragraph"),
            ("x++y++z", ["x", "y", "z"], "paragraph"),
            ("p==q==r", ["p", "q", "r"], "paragraph"),
            ("2*3 + 4*5", ["2", "3", "4", "5"], "paragraph"),
            ("w1**w2**", ["w1", "w2"], "paragraph"),
            ("[w1](https://example.com)w2", ["w1", "w2"], "paragraph"),
            ("| w1 | | w3 |\n|---|---|---|\n| w4 | w5 | w6 |", ["w1", "w3", "w6"], "paragraph"),
            ("|w1|w2|\n|-|-|\n|w3|", ["w1", "w2", "w3"], "paragraph"),
            ("**w1**'s w2, *w3*.", ["w1", "s", "w2", "w3"], "paragraph"),
            ("\u8FD9\u662F**\u7C97\u4F53**\u6587\u672C", ["\u8FD9\u662F", "\u7C97\u4F53", "\u6587\u672C"], "paragraph"),
            ("```js\nw1\n```", ["js", "w1"], "codeBlock"),
            ("~~~\nw1\n~~~", ["w1"], "codeBlock"),
            ("    w1 indented", ["w1", "indented"], "codeBlock"),
            ("w1\n\n---\n\nw2", ["w1", "w2"], "horizontalRule"),
            ("<div>w1</div>\n\nw2", ["div", "w1", "w2"], "paragraph"),
            ("<!-- w1 -->\n\nw2", ["w1", "w2"], "paragraph"),
            ("| w1 | w2 |\n|---|---|\n| w3 | [w4](https://example.com/w5) |", ["w1", "w2", "w3", "w4", "example", "w5"], "paragraph"),
            ("+----+----+\n| w1 | w2 |\n+----+----+\n| w3 | - w4 |\n+----+----+", ["w1", "w2", "w3", "w4"], "paragraph"),
            ("*w1* **w2** ~~w3~~ ==w4== ++w5++ `w6`", ["w1", "w2", "w3", "w4", "w5", "w6"], "paragraph"),
            ("w1  \nw2\\\nw3\nw4", ["w1", "w2", "w3", "w4"], "paragraph"),
            ("[w1](https://example.com/w2)", ["w1", "example", "w2"], "paragraph"),
            ("[w1](https://example.com/w2 \"w3\")", ["w1", "w2", "w3"], "paragraph"),
            ("[](https://example.com/w1) w2", ["example", "w1", "w2"], "paragraph"),
            ("![w1](https://example.com/w2.png \"w3\")", ["w1", "w2", "png", "w3"], "paragraph"),
            ("![](https://example.com/w1.png)", ["w1", "png"], "paragraph"),
            ("see <https://example.com/w1> and w2", ["https", "example", "w1", "w2"], "paragraph"),
            ("write to <w1@example.com> and w2", ["w1", "example", "w2"], "paragraph"),
            ("https://example.com/w1 bare", ["https", "w1", "bare"], "paragraph"),
            ("keep w1\n\n[w2]: https://example.com/w3 \"w4\"", ["w1", "w2", "w3", "w4"], "paragraph"),
            ("[w1][w2]\n\n[w2]: https://example.com/w3", ["w1", "w2", "w3"], "paragraph"),
            ("[w1]\n\n[w1]: https://example.com/w2", ["w1", "w2"], "paragraph"),
            ("she said [maybe w1 and w2", ["maybe", "w1", "w2"], "paragraph"),
            ("maybe] w1 ![ w2", ["maybe", "w1", "w2"], "paragraph"),
            ("[laughs] w1 [pause] w2", ["laughs", "w1", "pause", "w2"], "paragraph"),
            ("AT&T &amp; w1 &copy; x < y", ["AT", "T", "w1", "x", "y"], "paragraph"),
            ("w1 *w2 _w3 `w4 ~~w5", ["w1", "w2", "w3", "w4", "w5"], "paragraph"),
            ("a | b | w1", ["a", "b", "w1"], "paragraph"),
            ("[w1](javascript:w2)", ["w1", "javascript", "w2"], "paragraph"),
            ("[w1](data:text/html,w2)", ["w1", "data", "w2"], "paragraph"),
            ("![w1](javascript:w2)", ["w1", "javascript", "w2"], "paragraph"),
            ("<javascript:w1>", ["javascript", "w1"], "paragraph"),
            ("[^1] w1\n\n[^1]: w2", ["w1", "w2"], "paragraph"),
            ("*[w1]: w2\n\nw3", ["w1", "w2", "w3"], "paragraph"),
            ("w1\n: w2", ["w1", "w2"], "paragraph"),
            (":::note\nw1\n:::", ["note", "w1"], "paragraph"),
            ("# w1 {#w2 .w3}", ["w1", "w2", "w3"], "heading"),
            ("# R&amp;D don&#39;t w1", ["R", "D", "don", "t", "w1"], "heading"),
            ("-\n\n>\n\nw1", ["w1"], "paragraph"),
        ];
        return forms.Select<(string, string[], string), Func<(string, string[], string)>>(form => () => form);
    }

    [Test]
    [MethodDataSource(nameof(EachForm))]
    public void each_form_of_markdown_keeps_its_words_in_its_node((string Markdown, string[] Words, string NodeType) form)
    {
        // Act
        var json = MarkdownToProseMirror.ConvertToJson(form.Markdown);

        // Assert
        var words = WordsIn(json);
        foreach (var word in form.Words)
            words.ShouldContain(word, form.Markdown);
        TypesIn(JsonNode.Parse(json)!.AsObject()).ShouldContain(form.NodeType, form.Markdown);
    }

    [Test]
    [Arguments("Multiply 2*3*4 to get 24, and 5*3=15")]
    [Arguments("10**2**3")]
    [Arguments("a~~b~~c")]
    [Arguments("x++y++z")]
    [Arguments("p==q==r")]
    [Arguments("[w1](https://example.com)w2")]
    [Arguments("- [ ] w1\n- w2")]
    [Arguments("+ 5 degrees")]
    [Arguments("| w1 | | w3 |\n|---|---|---|\n| w4 | w5 | w6 |")]
    [Arguments("\uAC00**\uB098**\uB2E4")]
    public void a_form_that_would_change_what_is_shown_stays_as_the_source(string markdown)
    {
        // Act
        var plain = ProseMirrorToPlainText.Render(MarkdownToProseMirror.ConvertToJson(markdown));

        // Assert
        plain.Trim().ShouldBe(markdown);
    }

    [Test]
    [Arguments("Costs 5% of $10, say 2/3.")]
    [Arguments("caf&eacute; don&#39;t")]
    [Arguments("# Title\n\n**Bold** and *it* and ~~gone~~ with [a](https://example.com) here")]
    [Arguments("\u8FD9\u662F**\u7C97\u4F53**\u6587\u672C")]
    [Arguments("\u1780**\u1781**\u1782")]
    [Arguments("\u1000**\u1001**\u1002")]
    public void a_form_with_only_clear_marks_stays_in_its_nodes(string markdown)
    {
        // Act
        var plain = ProseMirrorToPlainText.Render(MarkdownToProseMirror.ConvertToJson(markdown));

        // Assert
        plain.ShouldNotContain("**");
        plain.ShouldNotContain("&#");
        plain.ShouldNotContain("&eacute");
    }

    [Test]
    public void a_sign_that_the_document_loses_sends_it_to_the_source()
    {
        // Act
        var plain = ProseMirrorToPlainText.Render(MarkdownToProseMirror.ConvertToJson("[w1](https://example.com/a 'w2')"));

        // Assert
        plain.ShouldContain("'w2'");
    }

    [Test]
    public void a_link_that_no_page_may_open_is_no_link_in_the_document()
    {
        // Act
        var json = MarkdownToProseMirror.ConvertToJson("[a](javascript:alert(1)) [b](JaVa\tScRiPt:x) ![c](data:text/html,x) <vbscript:d>");

        // Assert
        LinkAddress.AnyRefusedIn(json).ShouldBeFalse();
        json.ShouldNotContain("\"link\"");
        ProseMirrorToPlainText.Render(json).ShouldContain("javascript:alert(1)");
    }

    [Test]
    public void a_form_that_the_conversion_cannot_keep_stays_as_the_source()
    {
        // Act
        var json = MarkdownToProseMirror.ConvertToJson("# Heading\n\n```js kept-in-the-info w1\ncode\n```\n\nafter");

        // Assert
        var plain = ProseMirrorToPlainText.Render(json);
        plain.ShouldContain("kept-in-the-info w1");
        plain.ShouldContain("# Heading");
        TypesIn(JsonNode.Parse(json)!.AsObject()).ShouldNotContain("heading");
    }

    [Test]
    [Arguments("- foo [x] bar <http://x>", "[x]")]
    [Arguments("- foo [x] bar <x@example.com>", "[x]")]
    [Arguments("- foo [x] bar [**a** b](http://x)", "[x]")]
    [Arguments("- foo [x] bar [](http://x)", "[x]")]
    [Arguments("- foo [x] bar [a]\n\n[a]: http://x", "[x]")]
    [Arguments("- foo [x] bar [a]\n\n[a]: http://b \"x\"", "[x]")]
    [Arguments("- foo [x] bar &lt;http://x&gt;", "[x]")]
    [Arguments("```js lost\ncode\n```\n\n<http://lost>", "js lost")]
    [Arguments("```js &lost;\ncode\n```", "&lost;")]
    public void a_word_that_the_document_holds_twice_cannot_hide_a_lost_word(string markdown, string kept)
    {
        // Act
        var plain = ProseMirrorToPlainText.Render(MarkdownToProseMirror.ConvertToJson(markdown));

        // Assert
        plain.ShouldContain(kept);
    }

    [Test]
    public void a_refused_address_of_a_link_leaves_the_rest_of_the_document_in_its_nodes()
    {
        // Act
        var json = MarkdownToProseMirror.ConvertToJson("# w1\n\n<javascript:w2> and [](data:w3)");

        // Assert
        TypesIn(JsonNode.Parse(json)!.AsObject()).ShouldContain("heading");
        json.ShouldNotContain("\"link\"");
        foreach (var word in new[] { "w1", "w2", "w3" })
            WordsIn(json).ShouldContain(word);
    }

    [Test]
    public void a_link_that_a_definition_gives_stays_a_link_and_its_words_stay()
    {
        // Act
        var json = MarkdownToProseMirror.ConvertToJson("# w1\n\n[w2] and [w3](https://example.com/w4) and <https://example.com/w5>\n\n[w2]: https://example.com/w6 \"w7\"");

        // Assert
        TypesIn(JsonNode.Parse(json)!.AsObject()).ShouldContain("heading");
        foreach (var word in new[] { "w1", "w2", "w3", "w4", "w5", "w6", "w7" })
            WordsIn(json).ShouldContain(word);
    }

    [Test]
    public void each_number_and_each_common_word_counts()
    {
        // Act
        var json = MarkdownToProseMirror.ConvertToJson("the cost was 12 the cost was 12\n\n[the]: 12");

        // Assert
        WordsIn(json).Count(word => word == "the").ShouldBeGreaterThanOrEqualTo(3);
        WordsIn(json).Count(word => word == "12").ShouldBeGreaterThanOrEqualTo(3);
    }

    /// <summary>The oracle knows the words, because it wrote them. It shares no code with the check in the server.</summary>
    [Test]
    public void random_markdown_keeps_each_word()
    {
        // Arrange
        Console.WriteLine($"Seed {RandomSeed}, {RandomInputs} inputs");
        var random = new Random(RandomSeed);
        string[] glue = ["[", "]", "(", ")", "<", ">", "*", "**", "_", "`", "~~", "==", "++", "|", "!", "#", "\\", "-", ":", "&", ";", "\"", "'", "\n", "\n\n", " ", "  \n", "> ", "- ", "1. ", "- [ ] ", "---", "```", "    ", "[^", "]:", "](", "<!--", "-->", "<div>", "https://", "mailto:", "javascript:", "&amp;", "@", "="];

        // Act and Assert
        for (var input = 0; input < RandomInputs; input++)
        {
            var written = new List<string>();
            var markdown = new System.Text.StringBuilder();
            for (var part = random.Next(1, 40); part > 0; part--)
            {
                markdown.Append(glue[random.Next(glue.Length)]);
                if (random.Next(2) == 0)
                    continue;
                var word = $"w{written.Count}x";
                written.Add(word);
                markdown.Append(word);
                markdown.Append(random.Next(3) == 0 ? " " : string.Empty);
            }

            var source = markdown.ToString();
            // A word can touch the letters of the next mark, as in "w5xjavascript:", and the form "wNx" holds no other.
            var json = MarkdownToProseMirror.ConvertToJson(source);
            var kept = string.Join(' ', WordsIn(json));
            foreach (var word in written)
                kept.ShouldContain(word, Case.Sensitive, $"Seed {RandomSeed}, input {input}: {source}");

            // A mark always stands between two written words, so no block of the document may show them as one.
            JoinedWord().IsMatch(ProseMirrorToPlainText.Render(json)).ShouldBeFalse($"Seed {RandomSeed}, input {input}: {source}");
        }
    }

    [GeneratedRegex(@"[\p{L}\p{Nd}]+")]
    private static partial Regex Word();

    [GeneratedRegex(@"w[0-9]+xw[0-9]+x")]
    private static partial Regex JoinedWord();
}
