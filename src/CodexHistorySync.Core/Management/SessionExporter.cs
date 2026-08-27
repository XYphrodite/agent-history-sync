using System.Text;
using CodexHistorySync.Core.Conversion;
using CodexHistorySync.Core.IO;

namespace CodexHistorySync.Core.Management;

public interface ISessionExporter
{
    Task<string> ExportAsync(ManagedSession session, PortableConversation conversation, CancellationToken cancellationToken);
}

/// <summary>
/// Writes a conversation as Markdown for reading elsewhere. The native bytes stay where they
/// are; anyone who wants those already has the path, which the viewer shows.
/// </summary>
public sealed class SessionExporter : ISessionExporter
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly string root;

    public SessionExporter() : this(DefaultRoot()) { }

    public SessionExporter(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("An export root is required.", nameof(root));
        this.root = Path.GetFullPath(root);
    }

    public static string DefaultRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "agent-sync");

    public async Task<string> ExportAsync(
        ManagedSession session,
        PortableConversation conversation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(conversation);
        cancellationToken.ThrowIfCancellationRequested();

        // A session id reaches this from a catalog row and steers the write, so it is checked
        // on its own merits first: prefixing the agent would otherwise turn "" or ".." into a
        // harmless-looking name and hide that the row is broken.
        PathSafety.ValidateFileComponent(session.SessionId, nameof(session));
        var name = PathSafety.ValidateFileComponent(
            $"{session.Agent.ToString().ToLowerInvariant()}-{session.SessionId}", nameof(session));
        Directory.CreateDirectory(root);
        var destination = Path.Combine(root, name + ".md");

        var temporary = destination + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, Render(session, conversation), Utf8, cancellationToken)
                .ConfigureAwait(false);
            // Move over the top, so an interrupted export never leaves half a document behind.
            File.Move(temporary, destination, overwrite: true);
            return destination;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string Render(ManagedSession session, PortableConversation conversation)
    {
        var builder = new StringBuilder();
        builder.Append("# ").AppendLine(conversation.Title);
        builder.AppendLine();
        builder.Append("- Agent: ").AppendLine(session.Agent.ToString());
        builder.Append("- Session: ").AppendLine(session.SessionId);
        if (!string.IsNullOrWhiteSpace(conversation.WorkingDirectory))
            builder.Append("- Working directory: ").AppendLine(conversation.WorkingDirectory);
        builder.Append("- Created: ").AppendLine(conversation.CreatedAt.ToString("u"));
        builder.Append("- Last modified: ").AppendLine(conversation.LastModifiedAt.ToString("u"));

        foreach (var turn in conversation.Turns)
        {
            builder.AppendLine();
            builder.Append("## ").AppendLine(turn.Role == ConversationRole.User ? "User" : "Assistant");
            builder.AppendLine();
            builder.AppendLine(turn.Text);
        }

        return builder.ToString();
    }
}
