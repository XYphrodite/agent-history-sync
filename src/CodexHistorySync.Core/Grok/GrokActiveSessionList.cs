using System.Text.Json;

namespace CodexHistorySync.Core.Grok;

public readonly record struct GrokActiveSessionRecord(string SessionId, int ProcessId);

public static class GrokActiveSessionList
{
    public static IReadOnlyList<GrokActiveSessionRecord> Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return [];

        var records = new List<GrokActiveSessionRecord>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (!item.TryGetProperty("session_id", out var id) || id.ValueKind != JsonValueKind.String) continue;
            var sessionId = id.GetString();
            if (string.IsNullOrWhiteSpace(sessionId)) continue;
            if (!item.TryGetProperty("pid", out var pid) || pid.ValueKind != JsonValueKind.Number) continue;
            if (!pid.TryGetInt32(out var processId) || processId <= 0) continue;
            records.Add(new GrokActiveSessionRecord(sessionId, processId));
        }

        return records;
    }
}
