namespace CodexHistorySync.Core.Conversion;

public enum ConversationLineKind { RoleHeader, Text, Blank }

public sealed record ConversationLine(ConversationLineKind Kind, string Text, int TurnIndex);

/// <summary>
/// A conversation flattened once into wrapped display lines. Scrolling and search then work on
/// an indexed list instead of re-wrapping text on every frame; the viewer rebuilds only when the
/// pane width changes.
/// </summary>
public sealed class ConversationDocument
{
    public const int MinimumWidth = 8;

    private ConversationDocument(IReadOnlyList<ConversationLine> lines, int width)
    {
        Lines = lines;
        Width = width;
    }

    public IReadOnlyList<ConversationLine> Lines { get; }
    public int Width { get; }

    public static ConversationDocument Build(PortableConversation conversation, int width)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        var effectiveWidth = Math.Max(MinimumWidth, width);
        var lines = new List<ConversationLine>();

        for (var index = 0; index < conversation.Turns.Count; index++)
        {
            if (lines.Count != 0) lines.Add(new ConversationLine(ConversationLineKind.Blank, string.Empty, index));
            var turn = conversation.Turns[index];
            lines.Add(new ConversationLine(ConversationLineKind.RoleHeader, RoleName(turn.Role), index));
            foreach (var paragraph in SplitParagraphs(turn.Text))
            {
                if (paragraph.Length == 0)
                {
                    lines.Add(new ConversationLine(ConversationLineKind.Blank, string.Empty, index));
                    continue;
                }
                foreach (var wrapped in Wrap(paragraph, effectiveWidth))
                    lines.Add(new ConversationLine(ConversationLineKind.Text, wrapped, index));
            }
        }

        return new ConversationDocument(lines, effectiveWidth);
    }

    /// <summary>Indexes of lines containing <paramref name="query"/>, case-insensitive.</summary>
    public IReadOnlyList<int> FindMatches(string? query)
    {
        if (string.IsNullOrEmpty(query)) return [];
        var matches = new List<int>();
        for (var index = 0; index < Lines.Count; index++)
            if (Lines[index].Text.Contains(query, StringComparison.OrdinalIgnoreCase))
                matches.Add(index);
        return matches;
    }

    private static IEnumerable<string> SplitParagraphs(string text)
    {
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
            yield return line.TrimEnd();
    }

    /// <summary>
    /// Wraps on word boundaries. A word wider than the pane is hard-split rather than allowed to
    /// overflow, so a pasted path or a base64 blob cannot break the layout.
    /// </summary>
    private static IEnumerable<string> Wrap(string paragraph, int width)
    {
        var current = new System.Text.StringBuilder();
        foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var pending = word;
            while (pending.Length > width)
            {
                if (current.Length != 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }
                yield return pending[..width];
                pending = pending[width..];
            }

            if (current.Length == 0) current.Append(pending);
            else if (current.Length + 1 + pending.Length <= width) current.Append(' ').Append(pending);
            else
            {
                yield return current.ToString();
                current.Clear();
                current.Append(pending);
            }
        }

        if (current.Length != 0) yield return current.ToString();
    }

    private static string RoleName(ConversationRole role) => role switch
    {
        ConversationRole.User => "User",
        ConversationRole.Assistant => "Assistant",
        _ => role.ToString()
    };
}
