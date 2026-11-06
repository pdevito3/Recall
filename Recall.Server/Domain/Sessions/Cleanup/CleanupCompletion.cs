namespace Recall.Server.Domain.Sessions.Cleanup;

using Content;
using Corrections;
using Databases;
using Microsoft.EntityFrameworkCore;
using People;
using System.Text;

/// <summary>
/// Turns an output into what a Session stores, the same way for a model on the server and a model on a device. It reads the
/// rows of the owner. With approval off, it adds a Person for a new name, and the caller saves the Person.
/// </summary>
public sealed class CleanupCompletion(AppDbContext dbContext)
{
    /// <summary>Null when the stored form is longer than a Draft can be.</summary>
    public async Task<CleanupOutcome?> OutcomeOfAsync(Session session, CleanupOutput output, CancellationToken cancellationToken)
    {
        var corrections = await dbContext.Corrections.AsNoTracking()
            .TagWithOperationCallSite("sessions.cleanup.corrections")
            .ToListAsync(cancellationToken);
        var people = await dbContext.People.AsNoTracking()
            .TagWithOperationCallSite("sessions.cleanup.people")
            .ToListAsync(cancellationToken);
        var approvalIsOn = await dbContext.UserSettings.AsNoTracking()
            .TagWithOperationCallSite("sessions.cleanup.settings")
            .Select(s => (bool?)s.RequirePeopleProposalApproval)
            .FirstOrDefaultAsync(cancellationToken) ?? true;

        var cleaned = MarkdownToProseMirror.ConvertToJson(WithHardReplacements(output.CleanedMarkdown, corrections));
        var plain = ProseMirrorToPlainText.Render(cleaned);
        var names = output.PersonNames.Where(name => PeopleTagSpans.Occurrences(plain, name).Count > 0).ToList();

        IReadOnlyList<PersonProposal> proposals = [];
        if (approvalIsOn)
        {
            proposals = names.Select(name => new PersonProposal(name, MatchOf(people, name)?.Id)).ToList();
        }
        else
        {
            var spans = new List<MentionSpan>();
            foreach (var name in names)
            {
                var person = MatchOf(people, name) ?? await AddPersonAsync(session, name, cancellationToken);
                spans.AddRange(PeopleTagSpans.Spans(plain, name, person.Id, person.Label));
            }

            cleaned = MentionInsertion.Insert(cleaned, spans);
        }

        return cleaned.Length > Session.DraftMaxLength
            ? null
            : new CleanupOutcome(cleaned, output.Synopsis, output.TopicSuggestions, output.MoodSuggestions, proposals);
    }

    private static Person? MatchOf(IEnumerable<Person> people, string name)
        => people.FirstOrDefault(person => person.Label.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));

    private async Task<Person> AddPersonAsync(Session session, string name, CancellationToken cancellationToken)
    {
        var person = Person.Create(session.TenantId, session.UserId, name);
        await dbContext.People.AddAsync(person, cancellationToken);
        return person;
    }

    /// <summary>Each hard-replace mishearing becomes its term, whole word and in any case. Raw is not an input here.</summary>
    public static string WithHardReplacements(string markdown, IEnumerable<Correction> corrections)
    {
        foreach (var correction in corrections.Where(c => c.HardReplace))
        foreach (var mishearing in correction.Mishearings.Where(m => m.Length > 0))
            markdown = ReplaceWholeWord(markdown, mishearing, correction.CanonicalTerm);

        return markdown;
    }

    private static string ReplaceWholeWord(string text, string word, string term)
    {
        var result = new StringBuilder(text.Length);
        var position = 0;
        while (position < text.Length)
        {
            var found = text.IndexOf(word, position, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
                break;

            var end = found + word.Length;
            var isWhole = (found == 0 || !char.IsLetterOrDigit(text[found - 1])) && (end == text.Length || !char.IsLetterOrDigit(text[end]));
            result.Append(text, position, found - position).Append(isWhole ? term : text.Substring(found, word.Length));
            position = end;
        }

        return result.Append(text, position, text.Length - position).ToString();
    }
}
