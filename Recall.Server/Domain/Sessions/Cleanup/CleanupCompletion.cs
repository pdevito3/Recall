namespace Recall.Server.Domain.Sessions.Cleanup;

using Content;
using Corrections;
using Databases;
using Microsoft.EntityFrameworkCore;
using People;
using System.Text;

/// <summary>
/// Turns an output into what a Session stores, the same way for a model on the server and a model on a device. It reads the
/// rows of the owner. With approval off, it adds a Person for a new name to a result that fits, and the caller saves the Person.
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

        var markdown = WithHardReplacements(output.CleanedMarkdown, corrections);
        if (markdown.Length > Session.DraftMaxLength)
            return null;

        var cleaned = MarkdownToProseMirror.ConvertToJson(markdown);
        if (cleaned.Length > Session.DraftMaxLength)
            return null;

        var plain = ProseMirrorToPlainText.Render(cleaned);
        var names = output.PersonNames.Where(name => PeopleTagSpans.Occurrences(plain, name).Count > 0).ToList();

        IReadOnlyList<PersonProposal> proposals = [];
        var added = new List<Person>();
        if (approvalIsOn)
        {
            proposals = names.Select(name => new PersonProposal(name, MatchOf(people, name)?.Id)).ToList();
        }
        else
        {
            var spans = new List<MentionSpan>();
            var newPeople = new List<Person>();
            foreach (var name in names)
            {
                var person = MatchOf(people.Concat(newPeople), name);
                if (person is null)
                {
                    person = Person.CreateFromProposedName(session.TenantId, session.UserId, name);
                    newPeople.Add(person);
                }

                spans.AddRange(PeopleTagSpans.Spans(plain, name, person.Id, person.Label));
            }

            // A name with a mark across it, or inside a longer name, holds no tag, and a Person with no tag is a change that nothing on the page explains.
            cleaned = MentionInsertion.Insert(cleaned, spans);
            var tagged = PersonMentions.PersonIdsIn(cleaned);
            added.AddRange(newPeople.Where(person => tagged.Contains(person.Id)));
        }

        if (cleaned.Length > Session.DraftMaxLength)
            return null;

        await dbContext.People.AddRangeAsync(added, cancellationToken);
        return new CleanupOutcome(cleaned, output.Synopsis, output.TopicSuggestions, output.MoodSuggestions, proposals);
    }

    public static Person? MatchOf(IEnumerable<Person> people, string name)
        => people.FirstOrDefault(person => Person.KeyOf(person.Label) == Person.KeyOf(name));

    /// <summary>Each hard-replace mishearing becomes its term, whole word and in any case. Raw is not an input here.</summary>
    public static string WithHardReplacements(string markdown, IEnumerable<Correction> corrections)
    {
        foreach (var correction in corrections.Where(c => c.HardReplace))
        foreach (var mishearing in correction.Mishearings.Where(m => m.Length > 0))
        {
            markdown = ReplaceWholeWord(markdown, mishearing, correction.CanonicalTerm);
            if (markdown.Length > Session.DraftMaxLength)
                return markdown;
        }

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

            // A short mishearing and a long term can grow a text 100 times, and a longer text cannot be a Draft.
            if (result.Length > Session.DraftMaxLength)
                break;
        }

        return result.Append(text, position, text.Length - position).ToString();
    }
}
