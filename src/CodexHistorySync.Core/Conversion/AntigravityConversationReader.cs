using CodexHistorySync.Core.Antigravity;

namespace CodexHistorySync.Core.Conversion;

public sealed class AntigravityConversationReader : IConversationReader
{
    private const int TitlePreviewLength = 80;

    public Task<PortableConversation> ReadAsync(string nativePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(nativePath))
            throw new ArgumentException("An Antigravity session path is required.", nameof(nativePath));

        cancellationToken.ThrowIfCancellationRequested();

        string sessionId;
        string convDbPath;
        string? summariesDbPath = null;

        if (AntigravityPaths.TryParseAnchor(nativePath, out var home, out var anchorSessionId))
        {
            sessionId = anchorSessionId;
            var paths = new AntigravityPaths(home);
            convDbPath = paths.ConversationDatabasePath(sessionId);
            summariesDbPath = paths.ConversationSummariesDbPath;
        }
        else if (nativePath.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
        {
            var rawId = Path.GetFileNameWithoutExtension(nativePath);
            convDbPath = Path.GetFullPath(nativePath);
            if (AntigravityPaths.IsSessionId(rawId))
            {
                sessionId = rawId;
            }
            else
            {
                var meta = AntigravityDatabase.ReadTrajectoryMeta(convDbPath);
                if (meta is not null && AntigravityPaths.IsSessionId(meta.ConversationId))
                {
                    sessionId = meta.ConversationId;
                }
                else
                {
                    throw new InvalidDataException("Antigravity conversation is invalid.");
                }
            }

            var parentDir = Path.GetDirectoryName(convDbPath);
            if (parentDir is not null && string.Equals(Path.GetFileName(parentDir), "conversations", StringComparison.OrdinalIgnoreCase))
            {
                var root = Path.GetDirectoryName(parentDir);
                if (root is not null)
                {
                    summariesDbPath = Path.Combine(root, AntigravityPaths.SummariesDatabaseName);
                }
            }
        }
        else
        {
            throw new InvalidDataException("Antigravity conversation is invalid.");
        }

        if (!File.Exists(convDbPath))
        {
            throw new InvalidDataException("Antigravity conversation is invalid.");
        }

        try
        {
            var steps = AntigravityDatabase.ReadSteps(convDbPath);
            var turns = new List<PortableTurn>();

            foreach (var step in steps)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (step.StepType == AntigravityProtobuf.StepTypeUserInput)
                {
                    var text = AntigravityProtobuf.ExtractUserInput(step.Payload);
                    if (!string.IsNullOrWhiteSpace(text) && !ConversationTechnicalText.IsWrapper(text))
                    {
                        turns.Add(new PortableTurn(ConversationRole.User, text));
                    }
                }
                else if (step.StepType == AntigravityProtobuf.StepTypePlannerResponse)
                {
                    var (response, thinking) = AntigravityProtobuf.ExtractPlannerResponse(step.Payload);
                    var text = !string.IsNullOrWhiteSpace(response) ? response : thinking;
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        turns.Add(new PortableTurn(ConversationRole.Assistant, text));
                    }
                }
            }

            if (turns.Count == 0)
            {
                throw new InvalidDataException("Antigravity conversation is invalid.");
            }

            string? title = null;
            string? workingDirectory = null;
            DateTimeOffset? createdAt = null;
            DateTimeOffset? updatedAt = null;

            if (!string.IsNullOrEmpty(summariesDbPath) && File.Exists(summariesDbPath))
            {
                try
                {
                    var summaries = AntigravityDatabase.ReadSummaries(summariesDbPath);
                    var summary = summaries.FirstOrDefault(s => string.Equals(s.ConversationId, sessionId, StringComparison.Ordinal));
                    if (summary is not null)
                    {
                        if (!string.IsNullOrWhiteSpace(summary.Title))
                        {
                            title = summary.Title.Trim();
                        }

                        if (summary.WorkspaceUris is { Count: > 0 } && !string.IsNullOrWhiteSpace(summary.WorkspaceUris[0]))
                        {
                            var uri = summary.WorkspaceUris[0];
                            if (Uri.TryCreate(uri, UriKind.Absolute, out var parsedUri) && parsedUri.IsFile)
                            {
                                workingDirectory = parsedUri.LocalPath;
                            }
                            else
                            {
                                workingDirectory = uri;
                            }
                        }

                        if (summary.CreatedAt > DateTimeOffset.MinValue)
                            createdAt = summary.CreatedAt;
                        if (summary.UpdatedAt > DateTimeOffset.MinValue)
                            updatedAt = summary.UpdatedAt;
                    }
                }
                catch
                {
                    // Fall back to message content and file timestamps
                }
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                var firstUserTurn = turns.FirstOrDefault(t => t.Role == ConversationRole.User);
                if (firstUserTurn is not null)
                {
                    var preview = firstUserTurn.Text.Trim().Replace('\r', ' ').Replace('\n', ' ');
                    if (preview.Length > TitlePreviewLength)
                    {
                        var len = TitlePreviewLength;
                        if (len > 0 && char.IsHighSurrogate(preview[len - 1]))
                        {
                            len--;
                        }
                        preview = preview[..len];
                    }
                    title = preview;
                }

                if (string.IsNullOrWhiteSpace(title))
                {
                    title = sessionId;
                }
            }

            DateTimeOffset finalCreated;
            DateTimeOffset finalModified;
            if (createdAt.HasValue && updatedAt.HasValue)
            {
                finalCreated = createdAt.Value;
                finalModified = updatedAt.Value;
            }
            else
            {
                var fileInfo = new FileInfo(convDbPath);
                finalCreated = createdAt ?? new DateTimeOffset(fileInfo.CreationTimeUtc, TimeSpan.Zero);
                finalModified = updatedAt ?? new DateTimeOffset(fileInfo.LastWriteTimeUtc, TimeSpan.Zero);
            }

            if (finalCreated > finalModified)
                finalCreated = finalModified;

            return Task.FromResult(new PortableConversation(
                ConversationAgent.Antigravity,
                sessionId,
                title,
                workingDirectory,
                finalCreated,
                finalModified,
                turns));
        }
        catch (Exception ex) when (ex is not InvalidDataException &&
                                  ex is IOException or UnauthorizedAccessException or
                                        Microsoft.Data.Sqlite.SqliteException or ArgumentException)
        {
            throw new InvalidDataException("Antigravity conversation is invalid.", ex);
        }
    }
}
