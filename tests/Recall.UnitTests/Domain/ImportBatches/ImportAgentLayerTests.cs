namespace Recall.UnitTests.Domain.ImportBatches;

using System.Globalization;
using Recall.Server.Domain.ImportBatches;
using Recall.Server.Exceptions;
using Shouldly;

public class ImportAgentLayerTests
{
    private static ImportAgentLayer Layer(
        string date = "2024-03-31", int position = 0, string? time = null, string? timeZone = null, string raw = "Dear diary",
        IEnumerable<string?>? moods = null, IEnumerable<string?>? topics = null)
        => ImportAgentLayer.Of(date, position, time, timeZone, raw, moods, topics);

    public static IEnumerable<(Func<ImportAgentLayer> Write, string Field)> InvalidValues()
    {
        yield return (() => Layer(date: "31.03.2024"), "date");
        yield return (() => Layer(date: "2024-02-30"), "date");
        yield return (() => Layer(date: "0001-01-01"), "date");
        yield return (() => Layer(date: "0999-12-31"), "date");
        yield return (() => Layer(date: "9999-01-01"), "date");
        yield return (() => Layer(date: "9999-12-31"), "date");
        yield return (() => Layer(position: -1), "position");
        yield return (() => Layer(position: 720), "position");
        yield return (() => Layer(time: "9pm"), "time");
        yield return (() => Layer(time: "24:00"), "time");
        yield return (() => Layer(timeZone: "Factory"), "timeZone");
        yield return (() => Layer(timeZone: "Mars/Olympus"), "timeZone");
        yield return (() => Layer(raw: " \n "), "raw");
        yield return (() => Layer(raw: "---"), "raw");
        yield return (() => Layer(moods: Enumerable.Range(0, 51).Select(i => $"mood {i}")), "moods");
        yield return (() => Layer(topics: [new string('t', 101)]), "topics");
    }

    [Test]
    [Property("Rule", "IMP-28")]
    [MethodDataSource(nameof(InvalidValues))]
    public void an_invalid_value_names_its_field(Func<ImportAgentLayer> write, string field)
    {
        // Act
        var exception = Should.Throw<ValidationException>(() => write());

        // Assert
        exception.Property.ShouldBe(field);
    }

    [Test]
    [Property("Rule", "IMP-28")]
    [Arguments("1000-01-01")]
    [Arguments("9998-12-31")]
    public void a_date_at_an_edge_of_the_range_is_taken(string date)
    {
        // Act
        var layer = Layer(date: date);

        // Assert
        layer.Date.ToString(ImportAgentLayer.DateFormat, CultureInfo.InvariantCulture).ShouldBe(date);
    }

    [Test]
    [Property("Rule", "IMP-10")]
    public void a_zone_that_is_not_a_place_gets_the_message_of_the_batch_zone()
    {
        // Act
        var exception = Should.Throw<ValidationException>(() => Layer(timeZone: "Factory"));

        // Assert
        exception.Message.ShouldBe(ImportZone.UnknownMessage);
    }

    [Test]
    [Property("Rule", "IMP-11")]
    public void the_layer_keeps_the_markdown_and_its_document_keeps_each_word()
    {
        // Arrange
        const string raw = "Went to the *lake* with Sam.\n\n- swam\n- read";

        // Act
        var layer = Layer(raw: raw);

        // Assert
        layer.Raw.ShouldBe(raw);
        var document = layer.ToDocument();
        foreach (var word in new[] { "Went", "lake", "Sam.", "swam", "read" })
            document.ShouldContain(word);
    }

    [Test]
    [Property("Rule", "IMP-11")]
    public void raw_of_more_than_the_limit_of_a_draft_is_refused()
    {
        // Act
        var exception = Should.Throw<ValidationException>(() => Layer(raw: new string('a', ImportAgentLayer.RawMaxLength + 1)));

        // Assert
        exception.Property.ShouldBe("raw");
    }

    [Test]
    [Property("Rule", "IMP-5")]
    public void a_layer_with_the_same_values_is_the_same_layer()
    {
        // Act
        var first = Layer(time: "21:00", moods: ["calm"], topics: ["Lake"]);
        var second = Layer(time: "21:00", moods: ["Calm"], topics: ["Lake"]);

        // Assert
        first.ShouldBe(second);
        first.Moods.ShouldBe(["Calm"]);
    }
}
