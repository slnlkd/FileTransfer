using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace RemoteFiles.Client;

internal sealed partial class SqlTemplatePreview : RichTextBox
{
    public static readonly DependencyProperty SqlProperty = DependencyProperty.Register(
        nameof(Sql), typeof(string), typeof(SqlTemplatePreview),
        new PropertyMetadata(null, (target, args) => ((SqlTemplatePreview)target).ShowSql((string?)args.NewValue)));

    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "DISTINCT", "FROM", "WHERE", "UPDATE", "SET", "INSERT", "INTO", "VALUES",
        "DELETE", "JOIN", "INNER", "LEFT", "RIGHT", "FULL", "OUTER", "CROSS", "ON", "AS",
        "AND", "OR", "IS", "NOT", "NULL", "IN", "EXISTS", "BETWEEN", "LIKE", "GROUP", "BY",
        "HAVING", "ORDER", "ASC", "DESC", "NULLS", "FIRST", "LAST", "FETCH", "NEXT",
        "ROW", "ROWS", "ONLY", "FOR", "UNION", "ALL", "SYSDATE"
    };
    private static readonly HashSet<string> ClauseStarts = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "INSERT", "UPDATE", "DELETE", "FROM", "WHERE", "SET", "VALUES", "JOIN", "INNER", "LEFT", "RIGHT", "FULL",
        "CROSS", "GROUP", "HAVING", "ORDER", "FETCH", "FOR", "UNION"
    };

    static SqlTemplatePreview() => IsReadOnlyProperty.OverrideMetadata(
        typeof(SqlTemplatePreview), new FrameworkPropertyMetadata(true));

    public string? Sql
    {
        get => (string?)GetValue(SqlProperty);
        set => SetValue(SqlProperty, value);
    }

    // Keep quoted identifiers, literals, placeholders and comments intact when laying out the template.
    [GeneratedRegex("""(?<space>\s+)|(?<comment>--[^\r\n]*|/\*[\s\S]*?\*/)|(?<literal>[qQ]'(?:\[[\s\S]*?\]|\{[\s\S]*?\}|\([\s\S]*?\)|<[\s\S]*?>)'|'(?:''|[^'])*')|"(?:""|[^"])*"|\{[^}]*\}|(?<parameter>:[a-zA-Z_][\w$#]*)|[a-zA-Z_][\w$#]*|.""")]
    private static partial Regex Tokens();

    private void ShowSql(string? sql)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0) };
        if (!string.IsNullOrWhiteSpace(sql)) FormatSql(paragraph, sql);
        Document = new FlowDocument(paragraph) { PagePadding = new Thickness(12, 10, 12, 10) };
        ScrollToHome();
    }

    private static void FormatSql(Paragraph paragraph, string sql)
    {
        var parentheses = new Stack<bool>();
        var queryDepth = 0;
        var between = false;
        var previous = "";
        var gap = false;
        var breakAfterComment = false;
        for (var token = Tokens().Match(sql); token.Success; token = token.NextMatch())
        {
            if (token.Groups["space"].Success)
            {
                gap = true;
                continue;
            }

            var value = token.Value;
            var word = value.ToUpperInvariant();
            var keyword = Keywords.Contains(word);
            var closesQuery = value == ")" && parentheses.TryPop(out var query) && query;
            if (closesQuery) queryDepth--;
            var condition = word == "OR" || (word == "AND" && !between);
            var startsClause = ClauseStarts.Contains(word) &&
                               !(word == "FROM" && previous == "DELETE") &&
                               !(word == "UPDATE" && previous == "FOR") &&
                               !(word == "JOIN" && previous is "INNER" or "LEFT" or "RIGHT" or "FULL" or "OUTER" or "CROSS");
            if (previous.Length > 0 && (startsClause || condition || closesQuery || breakAfterComment))
            {
                paragraph.Inlines.Add(new LineBreak());
                var indent = queryDepth + (condition ? 1 : 0);
                if (indent > 0) paragraph.Inlines.Add(new Run(new string(' ', indent * 4)));
            }
            else if (previous.Length > 0 && (gap || previous == ",") && value is not "," and not ")")
            {
                paragraph.Inlines.Add(new Run(" "));
            }

            var run = new Run(value);
            if (keyword)
            {
                run.FontWeight = FontWeights.SemiBold;
                run.SetResourceReference(TextElement.ForegroundProperty, "AccentBrush");
            }
            else if (token.Groups["parameter"].Success)
            {
                run.SetResourceReference(TextElement.ForegroundProperty, "WarningBrush");
            }
            else if (token.Groups["literal"].Success)
            {
                run.SetResourceReference(TextElement.ForegroundProperty, "SuccessBrush");
            }
            else if (token.Groups["comment"].Success)
            {
                run.SetResourceReference(TextElement.ForegroundProperty, "MutedBrush");
            }
            paragraph.Inlines.Add(run);

            if (value == "(")
            {
                var next = token.NextMatch();
                if (next.Groups["space"].Success) next = next.NextMatch();
                var startsQuery = next.Value.Equals("SELECT", StringComparison.OrdinalIgnoreCase);
                parentheses.Push(startsQuery);
                if (startsQuery) queryDepth++;
            }
            if (word == "BETWEEN") between = true;
            else if (word == "AND" || startsClause) between = false;
            breakAfterComment = value.StartsWith("--", StringComparison.Ordinal);
            previous = word;
            gap = false;
        }
    }
}
