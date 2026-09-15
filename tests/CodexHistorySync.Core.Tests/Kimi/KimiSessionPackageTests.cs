using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexHistorySync.Core.Kimi;

namespace CodexHistorySync.Core.Tests.Kimi;

public sealed class KimiSessionPackageTests
{
    [Fact]
    public void BuildParseRoundTrip_PreservesIdWorkDirKeyAndFiles()
    {
        using var fixture = new KimiHomeFixture();
        var sessionDirectory = fixture.WriteSession(title: "round trip");
        var workDirKey = KimiPaths.ComputeWorkDirKey(fixture.WorkDir);
        var wirePath = Path.Combine(sessionDirectory, "agents", "main", KimiSessionPackage.WireFileName);

        var package = KimiSessionPackage.BuildFromDirectory(sessionDirectory, workDirKey);
        var info = KimiSessionPackage.Parse(package);

        Assert.Equal(KimiHomeFixture.MainSessionId, info.SessionId);
        Assert.Equal(workDirKey, info.WorkDirKey);
        Assert.Equal(fixture.WorkDir, info.WorkingDirectory);
        Assert.Contains(info.Files, file => file.Path == "state.json");
        Assert.Contains(info.Files, file => file.Path == "agents/main/wire.jsonl");
        // The raw wire bytes survive verbatim - thinking blocks and tool traffic included.
        var wire = Assert.Single(info.Files, file => file.Path == "agents/main/wire.jsonl");
        Assert.Contains("\"think\"", Encoding.UTF8.GetString(wire.Content), StringComparison.Ordinal);
        Assert.True(File.Exists(wirePath));
        Assert.Equal("ki-" + KimiHomeFixture.MainSessionId, KimiSessionPackage.ToLogicalId(KimiHomeFixture.MainSessionId));
    }

    [Fact]
    public void BuildRefusesSessionsWithNothingSynchronizable()
    {
        using var fixture = new KimiHomeFixture();
        var missingState = fixture.WriteSession();
        File.Delete(Path.Combine(missingState, KimiSessionPackage.StateFileName));
        var workDirKey = KimiPaths.ComputeWorkDirKey(fixture.WorkDir);
        Assert.Throws<InvalidDataException>(() => KimiSessionPackage.BuildFromDirectory(missingState, workDirKey));

        var missingWire = fixture.WriteSession(KimiHomeFixture.SecondSessionId);
        File.Delete(Path.Combine(missingWire, "agents", "main", KimiSessionPackage.WireFileName));
        Assert.Throws<InvalidDataException>(() => KimiSessionPackage.BuildFromDirectory(missingWire, workDirKey));

        Assert.Throws<InvalidDataException>(() =>
            KimiSessionPackage.BuildFromDirectory(Path.Combine(fixture.Paths.Sessions, "wd_x_0123456789ab", "not-a-session"), workDirKey));
        Assert.Throws<InvalidDataException>(() =>
            KimiSessionPackage.BuildFromDirectory(missingWire, "bad-key"));
    }

    [Fact]
    public void BuildRefusesStateWhoseIdDisagreesWithTheDirectory()
    {
        using var fixture = new KimiHomeFixture();
        var sessionDirectory = fixture.WriteSession();
        var statePath = Path.Combine(sessionDirectory, KimiSessionPackage.StateFileName);
        var state = JsonNode.Parse(File.ReadAllText(statePath))!;
        state["id"] = "session_99999999-0000-0000-0000-000000000009";
        File.WriteAllText(statePath, state.ToJsonString());

        Assert.Throws<InvalidDataException>(() =>
            KimiSessionPackage.BuildFromDirectory(sessionDirectory, KimiPaths.ComputeWorkDirKey(fixture.WorkDir)));
    }

    [Fact]
    public void MaterializeSubstitutesHomeDirAndMergesTheIndex()
    {
        using var fixture = new KimiHomeFixture();
        var sessionDirectory = fixture.WriteSession();
        fixture.WriteIndex(
            (KimiHomeFixture.SecondSessionId, Path.Combine(fixture.Paths.Home, "other-work")),
            (KimiHomeFixture.MainSessionId, fixture.WorkDir));
        var workDirKey = KimiPaths.ComputeWorkDirKey(fixture.WorkDir);

        var package = KimiSessionPackage.BuildFromDirectory(sessionDirectory, workDirKey);
        // Import onto the same machine is still an import: rewrite state as if from elsewhere is
        // covered by the cross-machine test below; here we check files, homedir and the index.
        KimiSessionPackage.Materialize(KimiSessionPackage.Parse(package), fixture.Paths);

        var materializedState = File.ReadAllText(Path.Combine(sessionDirectory, KimiSessionPackage.StateFileName));
        var expectedHome = Path.Combine(sessionDirectory, "agents", "main").Replace('\\', '/');
        Assert.Contains($"\"homedir\":\"{expectedHome}\"", materializedState, StringComparison.Ordinal);

        var indexLines = File.ReadAllLines(fixture.Paths.IndexFilePath);
        Assert.Equal(2, indexLines.Length);
        var entries = KimiSessionIndex.Parse(File.ReadAllText(fixture.Paths.IndexFilePath));
        var mainEntry = KimiSessionIndex.Find(entries, KimiPaths.SessionIdPrefix + KimiHomeFixture.MainSessionId);
        Assert.NotNull(mainEntry);
        Assert.Equal(sessionDirectory.Replace('\\', '/'), mainEntry["sessionDir"]!.GetValue<string>());
        Assert.Equal(fixture.WorkDir, mainEntry["workDir"]!.GetValue<string>());
    }

    [Fact]
    public void HashIsStableAcrossMachinesWithDifferentRoots()
    {
        // The homedir placeholder must make the package identical no matter which user profile or
        // drive the session is materialized under; otherwise every pull sees a local change and
        // republishes forever.
        using var source = new KimiHomeFixture();
        var sourceDirectory = source.WriteSession();
        var workDirKey = KimiPaths.ComputeWorkDirKey(source.WorkDir);
        var package = KimiSessionPackage.BuildFromDirectory(sourceDirectory, workDirKey);

        using var target = new KimiHomeFixture();
        KimiSessionPackage.Materialize(KimiSessionPackage.Parse(package), target.Paths);
        var targetDirectory = target.Paths.SessionDirectory(workDirKey, KimiPaths.SessionIdPrefix + KimiHomeFixture.MainSessionId);
        var rebuilt = KimiSessionPackage.BuildFromDirectory(targetDirectory, workDirKey);

        Assert.Equal(KimiSessionPackage.HashPackage(package), KimiSessionPackage.HashPackage(rebuilt));
        // And the materialized session points homedir at the target machine's absolute path.
        var state = File.ReadAllText(Path.Combine(targetDirectory, KimiSessionPackage.StateFileName));
        Assert.Contains(targetDirectory.Replace('\\', '/'), state, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseRejectsTamperedPackages()
    {
        using var fixture = new KimiHomeFixture();
        var sessionDirectory = fixture.WriteSession();
        var workDirKey = KimiPaths.ComputeWorkDirKey(fixture.WorkDir);
        var valid = KimiSessionPackage.BuildFromDirectory(sessionDirectory, workDirKey);
        var dto = DeserializeForTest(valid);

        Assert.Throws<InvalidDataException>(() => KimiSessionPackage.Parse(
            SerializeForTest(dto with { V = 99 })));
        Assert.Throws<InvalidDataException>(() => KimiSessionPackage.Parse(
            SerializeForTest(dto with { WorkDirKey = "../escape" })));

        var withTraversal = dto with
        {
            Files = [.. dto.Files, new FileDtoForTest("../escape.json", Convert.ToBase64String("x"u8.ToArray()))]
        };
        Assert.Throws<InvalidDataException>(() => KimiSessionPackage.Parse(SerializeForTest(withTraversal)));

        var withAbsolute = dto with
        {
            Files = [.. dto.Files, new FileDtoForTest("C:/outside/state.json", Convert.ToBase64String("x"u8.ToArray()))]
        };
        Assert.Throws<InvalidDataException>(() => KimiSessionPackage.Parse(SerializeForTest(withAbsolute)));

        var withJunk = dto with
        {
            Files = [.. dto.Files, new FileDtoForTest("logs/kimi-code.log", Convert.ToBase64String("x"u8.ToArray()))]
        };
        Assert.Throws<InvalidDataException>(() => KimiSessionPackage.Parse(SerializeForTest(withJunk)));

        var withDuplicate = dto with { Files = [dto.Files[0], dto.Files[0]] };
        Assert.Throws<InvalidDataException>(() => KimiSessionPackage.Parse(SerializeForTest(withDuplicate)));
    }

    [Fact]
    public void RuntimeOnlyContentNeverEntersThePackage()
    {
        using var fixture = new KimiHomeFixture();
        var sessionDirectory = fixture.WriteSession();
        Directory.CreateDirectory(Path.Combine(sessionDirectory, "logs"));
        File.WriteAllText(Path.Combine(sessionDirectory, "logs", "kimi-code.log"), "diagnostic");
        Directory.CreateDirectory(Path.Combine(sessionDirectory, "notify"));
        File.WriteAllText(Path.Combine(sessionDirectory, "notify", "state.json"), "{}");
        File.WriteAllText(Path.Combine(sessionDirectory, "upcoming-goals.json"), "[]");
        Directory.CreateDirectory(Path.Combine(sessionDirectory, "tasks"));
        Directory.CreateDirectory(Path.Combine(sessionDirectory, "cron"));
        var plans = Path.Combine(sessionDirectory, "agents", "main", "plans");
        Directory.CreateDirectory(plans);
        File.WriteAllText(Path.Combine(plans, "plan-1.md"), "# synthetic plan");

        var package = KimiSessionPackage.BuildFromDirectory(
            sessionDirectory, KimiPaths.ComputeWorkDirKey(fixture.WorkDir));
        var info = KimiSessionPackage.Parse(package);

        Assert.DoesNotContain(info.Files, file => file.Path.Contains("logs", StringComparison.Ordinal));
        Assert.DoesNotContain(info.Files, file => file.Path.Contains("notify", StringComparison.Ordinal));
        Assert.DoesNotContain(info.Files, file => file.Path.Contains("tasks", StringComparison.Ordinal));
        Assert.DoesNotContain(info.Files, file => file.Path.Contains("cron", StringComparison.Ordinal));
        Assert.DoesNotContain(info.Files, file => file.Path.Contains("upcoming-goals", StringComparison.Ordinal));
        Assert.Contains(info.Files, file => file.Path == "agents/main/plans/plan-1.md");
    }

    [Fact]
    public void SubAgentWiresArePackagedAlongsideTheMainOne()
    {
        using var fixture = new KimiHomeFixture();
        var sessionDirectory = fixture.WriteSession();
        var subAgent = Path.Combine(sessionDirectory, "agents", "agent-0");
        Directory.CreateDirectory(subAgent);
        File.WriteAllText(Path.Combine(subAgent, KimiSessionPackage.WireFileName),
            "{\"type\":\"agent.message.appended\",\"message\":{\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"sub result\"}]}},\"time\":1}\n",
            new UTF8Encoding(false));

        var package = KimiSessionPackage.BuildFromDirectory(
            sessionDirectory, KimiPaths.ComputeWorkDirKey(fixture.WorkDir));
        var info = KimiSessionPackage.Parse(package);

        Assert.Contains(info.Files, file => file.Path == "agents/agent-0/wire.jsonl");
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private sealed record PackageDtoForTest(int V, string Id, string WorkDirKey, List<FileDtoForTest> Files);
    private sealed record FileDtoForTest(string Path, string ContentBase64);

    private static PackageDtoForTest DeserializeForTest(byte[] package) =>
        JsonSerializer.Deserialize<PackageDtoForTest>(package, JsonOptions)
        ?? throw new InvalidDataException("test package did not deserialize");

    private static byte[] SerializeForTest(PackageDtoForTest dto) =>
        JsonSerializer.SerializeToUtf8Bytes(dto, JsonOptions);
}
