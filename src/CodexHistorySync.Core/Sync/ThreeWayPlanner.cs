using CodexHistorySync.Core.Model;

namespace CodexHistorySync.Core.Sync;

public enum SyncActionKind
{
    Upload,
    Download,
    Accept,
    ApplyTombstone,
    PublishTombstone,
    Conflict
}

public sealed record ObjectVersion(
    LogicalObjectId Id,
    ObjectKind Kind,
    ContentHash PlaintextHash,
    string Revision,
    bool IsDeleted);

public sealed record SyncAction(
    LogicalObjectId ObjectId,
    SyncActionKind Kind,
    ObjectVersion? Local,
    ObjectVersion? Remote,
    ObjectVersion? Baseline);

public sealed record SyncPlan(IReadOnlyList<SyncAction> Actions);

public static class ThreeWayPlanner
{
    public static SyncPlan CreatePlan(
        IReadOnlyDictionary<LogicalObjectId, ObjectVersion> local,
        IReadOnlyDictionary<LogicalObjectId, ObjectVersion> remote,
        IReadOnlyDictionary<LogicalObjectId, ObjectVersion> baseline)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(baseline);

        var objectIds = local.Keys
            .Concat(remote.Keys)
            .Concat(baseline.Keys)
            .Distinct()
            .OrderBy(id => id.Value, StringComparer.Ordinal);
        var actions = new List<SyncAction>();

        foreach (var objectId in objectIds)
        {
            local.TryGetValue(objectId, out var localVersion);
            remote.TryGetValue(objectId, out var remoteVersion);
            baseline.TryGetValue(objectId, out var baselineVersion);
            actions.Add(new SyncAction(
                objectId,
                Reconcile(localVersion, remoteVersion, baselineVersion),
                localVersion,
                remoteVersion,
                baselineVersion));
        }

        return new SyncPlan(actions);
    }

    private static SyncActionKind Reconcile(
        ObjectVersion? local,
        ObjectVersion? remote,
        ObjectVersion? baseline)
    {
        if (baseline is null)
        {
            if (local is null)
            {
                return remote?.IsDeleted == true ? SyncActionKind.ApplyTombstone : SyncActionKind.Download;
            }

            if (remote is null)
            {
                return local.IsDeleted ? SyncActionKind.PublishTombstone : SyncActionKind.Upload;
            }

            return Equivalent(local, remote) ? SyncActionKind.Accept : SyncActionKind.Conflict;
        }

        var localChanged = !Equivalent(local, baseline);
        var remoteChanged = !Equivalent(remote, baseline);

        if (!localChanged && !remoteChanged)
        {
            return SyncActionKind.Accept;
        }

        if (localChanged && remoteChanged)
        {
            return Equivalent(local, remote) ? SyncActionKind.Accept : SyncActionKind.Conflict;
        }

        if (localChanged)
        {
            return local?.IsDeleted != false ? SyncActionKind.PublishTombstone : SyncActionKind.Upload;
        }

        return remote?.IsDeleted != false ? SyncActionKind.ApplyTombstone : SyncActionKind.Download;
    }

    private static bool Equivalent(ObjectVersion? left, ObjectVersion? right) =>
        left is null || right is null
            ? left is null && right is null
            : left.Kind == right.Kind &&
              left.PlaintextHash == right.PlaintextHash &&
              left.IsDeleted == right.IsDeleted;
}
