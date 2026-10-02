using System.Text.Json;
using CodexHistorySync.Core.Conversion;
using CodexHistorySync.Core.Mimo;

namespace CodexHistorySync.Core.Management;

internal sealed class MimoSessionCatalogSource(MimoPaths paths) : ILocalSessionCatalogSource
{
    private const int MaximumTitleLength = 80;

    public ManagedAgent Agent => ManagedAgent.Mimo;

    public Task<IReadOnlyList<SessionCatalogCandidate>> ScanAsync(
        SessionCatalogReadLimiter limiter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(limiter);
        cancellationToken.ThrowIfCancellationRequested();
        MimoReadResult read;
        try
        {
            read = MimoSessionDatabase.ReadAll(paths);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or
                                          Microsoft.Data.Sqlite.SqliteException)
        {
            throw new IOException("MiMo sessions could not be read.", exception);
        }

        var rows = read.Snapshots
            .Where(snapshot => !snapshot.IsSubagent && !snapshot.IsLegacyGeneration)
            .Select(snapshot =>
        {
            var title = Normalize(CellText(snapshot.Session, "title"));
            var official = title is not null;
            title ??= Preview(ExtractFirstUserText(snapshot));
            return new SessionCatalogCandidate(
                snapshot.SessionId,
                paths.AnchorPath(snapshot.SessionId),
                title ?? snapshot.SessionId,
                Timestamp(snapshot.LastActiveUnix),
                true,
                official ? ManagedTitleSource.Official
                    : title is null ? ManagedTitleSource.SessionId : ManagedTitleSource.Fallback);
        }).ToArray();

        var duplicates = rows.GroupBy(row => row.SessionId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        return Task.FromResult<IReadOnlyList<SessionCatalogCandidate>>(
            rows.Select(row => duplicates.Contains(row.SessionId) ? row with { CanRead = false } : row).ToArray());
    }

    private static string? ExtractFirstUserText(MimoSnapshot snapshot)
    {
        for (var i = 0; i < snapshot.Messages.Count; i++)
        {
            var msg = snapshot.Messages[i];
            var dataCell = msg.FirstOrDefault(c => string.Equals(c.Name, "data", StringComparison.Ordinal));
            if (dataCell?.Text is null) continue;
            try
            {
                using var doc = JsonDocument.Parse(dataCell.Text);
                if (!doc.RootElement.TryGetProperty("role", out var role) || role.GetString() != "user") continue;
                // Try parts first
                if (i < snapshot.Parts.Count)
                {
                    foreach (var part in snapshot.Parts[i])
                    {
                        var partData = part.Cells.FirstOrDefault(c => string.Equals(c.Name, "data", StringComparison.Ordinal));
                        if (partData?.Text is null) continue;
                        try
                        {
                            using var pdoc = JsonDocument.Parse(partData.Text);
                            if (pdoc.RootElement.TryGetProperty("type", out var t) && t.GetString() == "text" &&
                                pdoc.RootElement.TryGetProperty("text", out var txt) && txt.ValueKind == JsonValueKind.String)
                            {
                                var text = txt.GetString();
                                if (!string.IsNullOrWhiteSpace(text) && !ConversationTechnicalText.IsWrapper(text))
                                    return text;
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }
        return null;
    }

    private static string? CellText(IReadOnlyList<MimoCell> cells, string name)
    {
        var cell = cells.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal));
        return cell?.Type == "text" ? cell.Text : null;
    }

    private static string? Normalize(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var trimmed = title.Trim().Replace('\r', ' ').Replace('\n', ' ');
        if (trimmed.Length == 0 || ConversationTechnicalText.IsWrapper(trimmed)) return null;
        return trimmed.Length <= MaximumTitleLength ? trimmed : trimmed[..MaximumTitleLength];
    }

    private static string? Preview(string? text) => Normalize(text);

    private static DateTimeOffset Timestamp(double unixSeconds)
    {
        try
        {
            var millis = (long)Math.Round(unixSeconds * 1000d, MidpointRounding.AwayFromZero);
            return DateTimeOffset.FromUnixTimeMilliseconds(millis);
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTimeOffset.UnixEpoch;
        }
    }
}
