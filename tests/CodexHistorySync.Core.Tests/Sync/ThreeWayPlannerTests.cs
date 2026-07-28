using CodexHistorySync.Core.Model;
using CodexHistorySync.Core.Sync;

namespace CodexHistorySync.Core.Tests.Sync;

public sealed class ThreeWayPlannerTests
{
    private static readonly LogicalObjectId ObjectId = new("session-1");

    [Theory]
    [MemberData(nameof(ReconciliationCases))]
    public void CreatePlan_ReconcilesEachVersionCombination(
        ObjectVersion? local,
        ObjectVersion? remote,
        ObjectVersion? baseline,
        SyncActionKind expected)
    {
        var plan = ThreeWayPlanner.CreatePlan(
            Versions(local),
            Versions(remote),
            Versions(baseline));

        var action = Assert.Single(plan.Actions);

        Assert.Equal(expected, action.Kind);
        Assert.Equal(ObjectId, action.ObjectId);
    }

    public static IEnumerable<object?[]> ReconciliationCases()
    {
        var baseVersion = Version("base");
        var localChange = Version("local");
        var remoteChange = Version("remote");
        var deleted = Deleted("base");

        yield return [localChange, baseVersion, baseVersion, SyncActionKind.Upload];
        yield return [baseVersion, remoteChange, baseVersion, SyncActionKind.Download];
        yield return [localChange, localChange, baseVersion, SyncActionKind.Accept];
        yield return [localChange, remoteChange, baseVersion, SyncActionKind.Conflict];
        yield return [deleted, baseVersion, baseVersion, SyncActionKind.PublishTombstone];
        yield return [baseVersion, deleted, baseVersion, SyncActionKind.ApplyTombstone];
        yield return [Version("new"), Version("new"), null, SyncActionKind.Accept];
        yield return [Version("new-local"), Version("new-remote"), null, SyncActionKind.Conflict];
        yield return [deleted, remoteChange, baseVersion, SyncActionKind.Conflict];
        yield return [localChange, deleted, baseVersion, SyncActionKind.Conflict];
    }

    [Fact]
    public void CreatePlan_OrdersActionsByLogicalObjectId()
    {
        var plan = ThreeWayPlanner.CreatePlan(
            new Dictionary<LogicalObjectId, ObjectVersion>
            {
                [new LogicalObjectId("z")] = Version("z", "z"),
                [new LogicalObjectId("a")] = Version("a", "a")
            },
            new Dictionary<LogicalObjectId, ObjectVersion>(),
            new Dictionary<LogicalObjectId, ObjectVersion>());

        Assert.Equal([new LogicalObjectId("a"), new LogicalObjectId("z")], plan.Actions.Select(action => action.ObjectId));
    }

    private static IReadOnlyDictionary<LogicalObjectId, ObjectVersion> Versions(ObjectVersion? version) =>
        version is null
            ? new Dictionary<LogicalObjectId, ObjectVersion>()
            : new Dictionary<LogicalObjectId, ObjectVersion> { [version.Id] = version };

    private static ObjectVersion Version(string hash, string? id = null) =>
        new(new LogicalObjectId(id ?? ObjectId.Value), ObjectKind.ActiveSession, new ContentHash(hash), hash, false);

    private static ObjectVersion Deleted(string hash) =>
        new(ObjectId, ObjectKind.ActiveSession, new ContentHash(hash), hash, true);
}
