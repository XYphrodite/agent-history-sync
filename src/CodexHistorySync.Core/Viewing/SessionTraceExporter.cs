using System.Text;
using CodexHistorySync.Core.IO;
using CodexHistorySync.Core.Management;

namespace CodexHistorySync.Core.Viewing;

/// <summary>Exports visible transcripts and tool input/output to new Markdown files.</summary>
public sealed class SessionTraceExporter(ISessionTraceReader reader)
{
    public async Task<string> ExportAsync(SessionTrace trace, string directory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(trace);
        var root = PrepareRoot(directory);
        var destination = Path.Combine(root, Name(trace.Session) + "-" + Guid.NewGuid().ToString("N")[..8] + ".md");
        await WriteNewAsync(destination, Render(trace), cancellationToken).ConfigureAwait(false);
        return destination;
    }

    public async Task<string> ExportFamilyAsync(SessionThread family, string directory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(family);
        var root = PrepareRoot(directory);
        var name = Name(family.Session) + "-" + DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
        var destination = Path.Combine(root, name);
        var staging = Path.Combine(root, ".export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var index = new StringBuilder("# " + Heading(family.Session.Title) + "\n\n");
            index.AppendLine("Each file includes the available messages and tool calls/results.\n");
            AppendIndex(index, family, 0);
            foreach (var node in family.DescendantsAndSelf())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var trace = await reader.ReadAsync(node.Session, cancellationToken).ConfigureAwait(false);
                await WriteNewAsync(Path.Combine(staging, Name(node.Session) + ".md"), Render(trace), cancellationToken).ConfigureAwait(false);
            }
            await WriteNewAsync(Path.Combine(staging, "index.md"), index.ToString(), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(staging, destination);
            return Path.Combine(destination, "index.md");
        }
        finally
        {
            // Only this operation's newly-created, private staging directory can be removed.
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    public static string Render(SessionTrace trace)
    {
        var builder = new StringBuilder("# " + Heading(trace.Session.Title) + "\n\n");
        builder.AppendLine($"- Agent: {trace.Session.Agent}");
        builder.AppendLine($"- Session: `{trace.Session.SessionId}`");
        foreach (var warning in trace.Warnings) builder.AppendLine("\n> " + Heading(warning));
        foreach (var entry in trace.Entries)
        {
            builder.AppendLine("\n## " + Heading(entry.Label));
            if (entry.Timestamp is { } timestamp) builder.AppendLine("\n" + timestamp.ToString("u"));
            if (entry.CallId is not null) builder.AppendLine("\nCall ID: " + Heading(entry.CallId));
            builder.AppendLine();
            if (entry.IsTool || entry.Kind == TraceEntryKind.Notification)
            {
                var longest = 0;
                var current = 0;
                foreach (var character in entry.Text)
                {
                    current = character == '`' ? current + 1 : 0;
                    longest = Math.Max(longest, current);
                }
                var fence = new string('`', Math.Max(3, longest + 1));
                builder.AppendLine(fence + "text").AppendLine(entry.Text).AppendLine(fence);
            }
            else builder.AppendLine(entry.Text);
        }
        return builder.ToString();
    }

    private static void AppendIndex(StringBuilder builder, SessionThread node, int depth)
    {
        var title = Heading(node.Session.Title).Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal).Replace("]", "\\]", StringComparison.Ordinal);
        builder.Append(' ', depth * 2).Append("- [").Append(title).Append("](")
            .Append(Name(node.Session)).AppendLine(".md)");
        foreach (var child in node.Children) AppendIndex(builder, child, depth + 1);
    }

    private static string Heading(string text) => text.Replace('\r', ' ').Replace('\n', ' ');
    private static string Name(ManagedSession session)
    {
        PathSafety.ValidateFileComponent(session.SessionId, nameof(session));
        return PathSafety.ValidateFileComponent($"{session.Agent.ToString().ToLowerInvariant()}-{session.SessionId}", nameof(session));
    }

    private static string PrepareRoot(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var root = Path.GetFullPath(directory);
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task WriteNewAsync(string destination, string text, CancellationToken cancellationToken)
    {
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, text, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
