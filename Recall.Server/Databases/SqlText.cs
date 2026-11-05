namespace Recall.Server.Databases;

/// <summary>
/// SQLite ignores the case of a name, so a name is in lower case here.
/// </summary>
internal static class SqlText
{
    private static readonly HashSet<string> WriteKeywords = ["insert", "replace", "update", "delete", "drop", "alter"];

    /// <summary>
    /// The words before a table name. SQLite also takes a string literal as a name, so <c>UPDATE 'users'</c> writes the
    /// table. The last five words are the conflict actions of <c>UPDATE OR REPLACE</c>.
    /// </summary>
    private static readonly HashSet<string> WordsBeforeATable =
        ["update", "into", "from", "table", "exists", "on", "rollback", "abort", "replace", "fail", "ignore"];

    internal enum TokenKind
    {
        Word,
        QuotedName,
    }

    internal readonly record struct Token(TokenKind Kind, string Text);

    /// <summary>
    /// The first of <paramref name="tables"/> that a word of the SQL names, or null. A word that is a column or an
    /// alias with the name of a table also counts, so the guard errs toward a refusal.
    /// </summary>
    public static string? FirstNamed(string sql, IReadOnlySet<string> tables)
        => Tokens(sql)
            .Where(token => token.Kind is TokenKind.Word or TokenKind.QuotedName)
            .Select(token => token.Text)
            .FirstOrDefault(tables.Contains);

    public static bool IsWrite(string sql)
        => Tokens(sql).Any(token => token.Kind == TokenKind.Word && WriteKeywords.Contains(token.Text));

    internal static IEnumerable<Token> Tokens(string sql)
    {
        var i = 0;
        var tableCanFollow = false;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (c == '-' && Next(sql, i) == '-')
                i = AfterLineComment(sql, i);
            else if (c == '/' && Next(sql, i) == '*')
                i = AfterBlockComment(sql, i);
            else if (c is '\'' or '"' or '`' or '[')
            {
                var close = c == '[' ? ']' : c;
                var end = AfterQuoted(sql, i, close);
                if (c != '\'' || tableCanFollow)
                {
                    var name = sql[(i + 1)..Math.Max(i + 1, end - 1)].Replace($"{close}{close}", $"{close}", StringComparison.Ordinal);
                    yield return new Token(TokenKind.QuotedName, name.ToLowerInvariant());
                }

                tableCanFollow = false;
                i = end;
            }
            else if (char.IsLetterOrDigit(c) || c == '_')
            {
                var start = i;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] is '_' or '$'))
                    i++;

                var word = sql[start..i].ToLowerInvariant();
                tableCanFollow = WordsBeforeATable.Contains(word);
                if (!char.IsAsciiDigit(word[0]))
                    yield return new Token(TokenKind.Word, word);
            }
            else
            {
                if (!char.IsWhiteSpace(c))
                    tableCanFollow = c == '.';
                i++;
            }
        }
    }

    private static char? Next(string sql, int i) => i + 1 < sql.Length ? sql[i + 1] : null;

    private static int AfterLineComment(string sql, int start)
    {
        var end = sql.IndexOf('\n', start);
        return end < 0 ? sql.Length : end + 1;
    }

    /// <summary>SQLite does not nest block comments, so a comment ends at the first <c>*/</c>.</summary>
    private static int AfterBlockComment(string sql, int start)
    {
        var end = sql.IndexOf("*/", start + 2, StringComparison.Ordinal);
        return end < 0 ? sql.Length : end + 2;
    }

    /// <summary>A doubled quote is a quote in the text. A square bracket has no such escape, so it ends at the first bracket.</summary>
    private static int AfterQuoted(string sql, int start, char quote)
    {
        var i = start + 1;
        while (i < sql.Length)
        {
            if (sql[i] != quote)
                i++;
            else if (quote != ']' && Next(sql, i) == quote)
                i += 2;
            else
                return i + 1;
        }

        return sql.Length;
    }
}
