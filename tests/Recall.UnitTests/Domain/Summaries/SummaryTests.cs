namespace Recall.UnitTests.Domain.Summaries;

using Recall.Server.Domain.Summaries;
using Shouldly;

public class SummaryTests
{
    private static readonly PeriodKey Day = new(SummaryPeriod.Day, new DateOnly(2026, 11, 2));
    private static readonly SourceState Sources = new(3, new string('a', 64));
    private static readonly SourceState Changed = new(3, new string('b', 64));
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    private static Summary NewSummary() => Summary.WithFirstRun("ten_1", "usr_1", Day, jobId: null);

    [Test]
    public void a_new_summary_is_generating()
    {
        // Act
        var summary = NewSummary();

        // Assert
        summary.StatusFor(Sources).ShouldBe(SummaryStatus.Generating);
        summary.RunId.ShouldNotBeNull();
    }

    [Test]
    public void a_completed_run_is_ready_and_reads_its_sources()
    {
        // Arrange
        var summary = NewSummary();

        // Act
        var completed = summary.Complete(summary.RunId!, "They wrote.", Sources, Now);

        // Assert
        completed.ShouldBeTrue();
        summary.StatusFor(Sources).ShouldBe(SummaryStatus.Ready);
        summary.Content.ShouldBe("They wrote.");
        summary.SourceCount.ShouldBe(3);
        summary.GeneratedAt.ShouldBe(Now);
    }

    [Test]
    public void a_failed_run_is_failed_and_never_ready()
    {
        // Arrange
        var summary = NewSummary();

        // Act
        summary.Fail(summary.RunId!, SummaryFailure.ProviderRefused);

        // Assert
        summary.StatusFor(Sources).ShouldBe(SummaryStatus.Failed);
        summary.Failure.ShouldBe(SummaryFailure.ProviderRefused);
        summary.Content.ShouldBeNull();
        summary.GeneratedAt.ShouldBeNull();
    }

    [Test]
    public void a_ready_summary_whose_sources_changed_is_stale()
    {
        // Arrange
        var summary = NewSummary();
        summary.Complete(summary.RunId!, "They wrote.", Sources, Now);

        // Act and Assert
        summary.StatusFor(Changed).ShouldBe(SummaryStatus.Stale);
        summary.StatusFor(SourceState.None).ShouldBe(SummaryStatus.Missing);
    }

    [Test]
    public void an_older_run_ends_nothing()
    {
        // Arrange
        var summary = NewSummary();
        var older = summary.RunId!;
        var newer = summary.BeginRun(jobId: null);

        // Act
        var completed = summary.Complete(older, "Old words.", Sources, Now);
        var failed = summary.Fail(older, SummaryFailure.Interrupted);

        // Assert
        completed.ShouldBeFalse();
        failed.ShouldBeFalse();
        summary.IsRunning(newer).ShouldBeTrue();
        summary.Content.ShouldBeNull();
    }

    [Test]
    public void a_new_run_keeps_the_words_of_the_last_ready_run()
    {
        // Arrange
        var summary = NewSummary();
        summary.Complete(summary.RunId!, "They wrote.", Sources, Now);

        // Act
        var runId = summary.BeginRun(Guid.NewGuid());
        summary.Fail(runId, SummaryFailure.ProviderUnavailable);

        // Assert
        summary.StatusFor(Changed).ShouldBe(SummaryStatus.Failed);
        summary.Content.ShouldBe("They wrote.");
        summary.RunJobId.ShouldBeNull();
    }

    [Test]
    [Arguments("")]
    [Arguments("   \n ")]
    public void an_empty_answer_is_no_content(string answer)
    {
        // Act
        var content = SummaryPrompt.ContentOf(answer);

        // Assert
        content.ShouldBeNull();
    }

    [Test]
    public void an_answer_longer_than_the_limit_is_no_content()
    {
        // Act
        var content = SummaryPrompt.ContentOf(new string('x', Summary.ContentMaxLength + 1));

        // Assert
        content.ShouldBeNull();
    }

    [Test]
    public void an_answer_is_trimmed()
    {
        // Act
        var content = SummaryPrompt.ContentOf("  They wrote.\n");

        // Assert
        content.ShouldBe("They wrote.");
    }

    [Test]
    public void the_words_of_the_sources_go_in_the_text_and_not_in_the_instructions()
    {
        // Act
        var text = SummaryPrompt.TextOf(Day, [new SummarySourceText(Day.Start, "first words"), new SummarySourceText(Day.Start, "second words")]);

        // Assert
        text.ShouldStartWith("<summary-sources period=\"Day\" first-day=\"2026-11-02\" last-day=\"2026-11-02\">");
        text.ShouldContain("<source n=\"1\" kind=\"Entry\" day=\"2026-11-02\">\nfirst words\n</source>");
        text.ShouldContain("<source n=\"2\" kind=\"Entry\" day=\"2026-11-02\">\nsecond words\n</source>");
        SummaryPrompt.Instructions.ShouldNotContain("first words");
    }
}
