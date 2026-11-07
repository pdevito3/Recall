namespace Recall.UnitTests.Domain.Sessions.Content;

using System.Text.Json.Nodes;
using Recall.Server.Domain.Sessions.Content;
using Shouldly;

public class MarkdownToProseMirrorTests
{
    [Test]
    [Arguments(5)]
    [Arguments(40)]
    [Arguments(100)]
    [Arguments(127)]
    [Arguments(200)]
    public void markdown_of_any_depth_keeps_its_words_and_reads_back(int levels)
    {
        // Arrange
        var quotes = string.Concat(Enumerable.Repeat("> ", levels)) + "deep words";
        var lists = string.Concat(Enumerable.Range(0, levels).Select(level => $"{new string(' ', level * 2)}- deep words\n"));

        // Act
        var quoted = MarkdownToProseMirror.ConvertToJson(quotes);
        var listed = MarkdownToProseMirror.ConvertToJson(lists);

        // Assert
        foreach (var json in new[] { quoted, listed })
        {
            JsonNode.Parse(json).ShouldNotBeNull();
            var plain = ProseMirrorToPlainText.Render(json);
            plain.ShouldContain("deep words");
            plain.ShouldNotContain("\"type\"");
        }
    }

    [Test]
    public void words_of_any_alphabet_take_no_more_room_than_they_need()
    {
        // Act
        var json = MarkdownToProseMirror.ConvertToJson("Обед");

        // Assert
        json.ShouldContain("Обед");
    }

    [Test]
    [Arguments("- [x] paid rent\n- [ ] call mum", "paid rent|call mum")]
    [Arguments("- [x] **paid** rent", "paid")]
    public void a_task_starts_with_its_words_and_not_with_the_space_after_its_box(string markdown, string firstWords)
    {
        // Act
        var json = MarkdownToProseMirror.ConvertToJson(markdown);

        // Assert
        var items = JsonNode.Parse(json)!["content"]![0]!["content"]!.AsArray();
        var firstTexts = items.Select(item => item!["content"]![0]!["content"]![0]!["text"]!.GetValue<string>());
        string.Join('|', firstTexts).ShouldBe(firstWords);
    }
}
