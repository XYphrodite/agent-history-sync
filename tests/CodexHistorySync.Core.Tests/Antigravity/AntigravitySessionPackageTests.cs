using System.Text;
using CodexHistorySync.Core.Antigravity;
using CodexHistorySync.Core.Model;
using Xunit;

namespace CodexHistorySync.Core.Tests.Antigravity;

public sealed class AntigravitySessionPackageTests : IDisposable
{
    private readonly AntigravityTestFixture fixture = new();

    public void Dispose()
    {
        fixture.Dispose();
    }

    [Fact]
    public void LogicalId_ValidSessionId_FormatsCorrectlyAndRoundTrips()
    {
        var sessionId = Guid.NewGuid().ToString("D").ToLowerInvariant();
        var logicalId = AntigravitySessionPackage.ToLogicalId(sessionId);
        Assert.Equal("ag-" + sessionId, logicalId);
        Assert.True(AntigravitySessionPackage.IsAntigravityLogicalId(logicalId));

        Assert.True(AntigravitySessionPackage.TrySplitLogicalId(logicalId, out var parsedSessionId));
        Assert.Equal(sessionId, parsedSessionId);

        Assert.True(AntigravitySessionPackage.TrySplitLogicalId(logicalId, out var profile, out var parsedWithProfile));
        Assert.Equal("default", profile);
        Assert.Equal(sessionId, parsedWithProfile);
    }

    [Fact]
    public void LogicalId_InvalidOrTraversalId_Rejects()
    {
        Assert.False(AntigravitySessionPackage.IsAntigravityLogicalId("ag-../secret"));
        Assert.False(AntigravitySessionPackage.IsAntigravityLogicalId("ag-not-a-guid"));
        Assert.False(AntigravitySessionPackage.IsAntigravityLogicalId(""));
        Assert.False(AntigravitySessionPackage.IsAntigravityLogicalId("he-abc"));
        Assert.False(AntigravitySessionPackage.TrySplitLogicalId("ag-../traversal", out _));

        Assert.Throws<ArgumentException>(() => AntigravitySessionPackage.ToLogicalId("../secret"));
        Assert.Throws<ArgumentException>(() => AntigravitySessionPackage.ToLogicalId(""));
        Assert.Throws<ArgumentException>(() => AntigravitySessionPackage.ToLogicalId(Guid.Empty.ToString()));
    }

    [Fact]
    public void DeterministicHashing_ProducesIdenticalHashAcrossRepeatedBuilds()
    {
        var sessionId = Guid.NewGuid().ToString("D").ToLowerInvariant();
        fixture.BuildStandardSession(
            sessionId,
            title: "Deterministic Packaging Test",
            userPrompt: "Hello Antigravity!",
            assistantResponse: "Greetings! Ready to synchronize.",
            workspaceUri: "file:///C:/Repos/agent-sync");

        // Write an additional artifact in brain/
        var brainDir = fixture.Paths.BrainSessionDirectory(sessionId);
        Directory.CreateDirectory(brainDir);
        File.WriteAllText(Path.Combine(brainDir, "metadata.json"), "{\"status\":\"ok\"}\n");

        var package1 = AntigravitySessionPackage.BuildFromLocal(fixture.Paths, sessionId);
        var package2 = AntigravitySessionPackage.BuildFromLocal(fixture.Paths, sessionId);

        Assert.Equal(package1, package2);
        var hash1 = AntigravitySessionPackage.HashPackage(package1);
        var hash2 = AntigravitySessionPackage.HashPackage(package2);
        Assert.Equal(hash1, hash2);
        Assert.Equal(AntigravitySessionPackage.ToLogicalId(sessionId), AntigravitySessionPackage.LogicalIdOf(package1));
    }

    [Fact]
    public void Materialize_FullSession_RestoresDatabaseSummariesStepsAnnotationsAndBrainFiles()
    {
        var sessionId = Guid.NewGuid().ToString("D").ToLowerInvariant();
        fixture.BuildStandardSession(
            sessionId,
            title: "Full Materialization Test",
            userPrompt: "Analyze this repository layout",
            assistantResponse: "The layout contains clean modular boundaries.",
            workspaceUri: "file:///C:/Repos/agent-sync");

        // Write binary and text artifacts in brain/<id>/
        var brainDir = fixture.Paths.BrainSessionDirectory(sessionId);
        Directory.CreateDirectory(Path.Combine(brainDir, "sub"));
        File.WriteAllText(Path.Combine(brainDir, "sub", "notes.txt"), "Important findings\n");
        File.WriteAllBytes(Path.Combine(brainDir, "data.bin"), new byte[] { 0x00, 0x01, 0x02, 0xFF });

        var package = AntigravitySessionPackage.BuildFromLocal(fixture.Paths, sessionId);
        Assert.NotNull(package);

        // Materialize into a clean target fixture
        using var targetFixture = new AntigravityTestFixture();
        AntigravitySessionPackage.Materialize(targetFixture.Paths, package);

        // 1. Verify summaries DB
        var summaries = AntigravityDatabase.ReadSummaries(targetFixture.Paths.ConversationSummariesDbPath);
        var summary = Assert.Single(summaries);
        Assert.Equal(sessionId, summary.ConversationId);
        Assert.Equal("Full Materialization Test", summary.Title);
        Assert.Contains("file:///C:/Repos/agent-sync", summary.WorkspaceUris);

        // 2. Verify steps DB
        var steps = AntigravityDatabase.ReadSteps(targetFixture.Paths.ConversationDatabasePath(sessionId));
        Assert.Equal(2, steps.Count);
        Assert.Equal(AntigravityProtobuf.StepTypeUserInput, steps[0].StepType);
        Assert.Equal("Analyze this repository layout", AntigravityProtobuf.ExtractUserInput(steps[0].Payload));
        Assert.Equal(AntigravityProtobuf.StepTypePlannerResponse, steps[1].StepType);
        var (resp, _) = AntigravityProtobuf.ExtractPlannerResponse(steps[1].Payload);
        Assert.Equal("The layout contains clean modular boundaries.", resp);

        // 3. Verify annotation file
        var annotPath = targetFixture.Paths.AnnotationPath(sessionId);
        Assert.True(File.Exists(annotPath));
        Assert.Contains($"note: \"Standard session {sessionId}\"", File.ReadAllText(annotPath));

        // 4. Verify brain files
        var targetBrainDir = targetFixture.Paths.BrainSessionDirectory(sessionId);
        Assert.True(File.Exists(targetFixture.Paths.TranscriptPath(sessionId)));
        Assert.True(File.Exists(Path.Combine(targetBrainDir, "sub", "notes.txt")));
        Assert.Equal("Important findings\n", File.ReadAllText(Path.Combine(targetBrainDir, "sub", "notes.txt")).Replace("\r\n", "\n"));
        Assert.True(File.Exists(Path.Combine(targetBrainDir, "data.bin")));
        Assert.Equal(new byte[] { 0x00, 0x01, 0x02, 0xFF }, File.ReadAllBytes(Path.Combine(targetBrainDir, "data.bin")));

        // 5. Re-building from target must match original package hash
        var rebuilt = AntigravitySessionPackage.BuildFromLocal(targetFixture.Paths, sessionId);
        Assert.Equal(AntigravitySessionPackage.HashPackage(package), AntigravitySessionPackage.HashPackage(rebuilt));
    }

    [Fact]
    public void Rematerialize_OverwritesPreviousStateWithoutCorruption()
    {
        var sessionId = Guid.NewGuid().ToString("D").ToLowerInvariant();
        fixture.BuildStandardSession(sessionId, "V1 Title", "V1 Prompt", "V1 Response");

        var pkg1 = AntigravitySessionPackage.BuildFromLocal(fixture.Paths, sessionId);

        using var targetFixture = new AntigravityTestFixture();
        AntigravitySessionPackage.Materialize(targetFixture.Paths, pkg1);

        // Now create updated session V2 in fixture
        fixture.InsertSummaryRow(sessionId, "V2 Title Updated");
        fixture.InsertUserStep(sessionId, idx: 2, text: "Follow-up question");
        fixture.InsertAssistantStep(sessionId, idx: 3, text: "Follow-up answer");

        var pkg2 = AntigravitySessionPackage.BuildFromLocal(fixture.Paths, sessionId);
        Assert.NotEqual(AntigravitySessionPackage.HashPackage(pkg1), AntigravitySessionPackage.HashPackage(pkg2));

        // Materialize V2 over V1 in target
        AntigravitySessionPackage.Materialize(targetFixture.Paths, pkg2);

        var summaries = AntigravityDatabase.ReadSummaries(targetFixture.Paths.ConversationSummariesDbPath);
        var summary = Assert.Single(summaries);
        Assert.Equal("V2 Title Updated", summary.Title);

        var steps = AntigravityDatabase.ReadSteps(targetFixture.Paths.ConversationDatabasePath(sessionId));
        Assert.Equal(4, steps.Count);
        Assert.Equal("Follow-up answer", AntigravityProtobuf.ExtractPlannerResponse(steps[3].Payload).Response);
    }

    [Fact]
    public void AnchorSnapshot_WriteSnapshotMaterializeAndFingerprint()
    {
        var sessionId = Guid.NewGuid().ToString("D").ToLowerInvariant();
        fixture.BuildStandardSession(sessionId, "Anchor Test", "Prompt", "Response");

        var anchorPath = fixture.Paths.AnchorPath(sessionId);
        AntigravitySessionPackage.WriteAnchorSnapshot(anchorPath);
        Assert.True(File.Exists(anchorPath));

        var pkg = AntigravitySessionPackage.BuildFromLocal(fixture.Paths, sessionId);
        var expectedHash = AntigravitySessionPackage.HashPackage(pkg);

        var anchorHash = AntigravitySessionPackage.HashAnchor(anchorPath);
        Assert.NotNull(anchorHash);
        Assert.Equal(expectedHash, anchorHash!.Value);

        var fingerprint = AntigravitySessionPackage.Fingerprint(anchorPath);
        Assert.NotEmpty(fingerprint);
        Assert.Equal(expectedHash.Hex, Convert.ToHexString(fingerprint).ToLowerInvariant());

        // Materialize using anchor into a separate home
        var secondaryRoot = Path.Combine(Path.GetTempPath(), "antigravity-anchor-" + Guid.NewGuid().ToString("N"));
        try
        {
            var secondaryPaths = new AntigravityPaths(secondaryRoot);
            var secondaryAnchor = secondaryPaths.AnchorPath(sessionId);
            Directory.CreateDirectory(Path.GetDirectoryName(secondaryAnchor)!);
            File.Copy(anchorPath, secondaryAnchor);

            AntigravitySessionPackage.MaterializeAnchor(secondaryAnchor);
            var secondaryPkg = AntigravitySessionPackage.BuildFromLocal(secondaryPaths, sessionId);
            Assert.Equal(expectedHash, AntigravitySessionPackage.HashPackage(secondaryPkg));
        }
        finally
        {
            if (Directory.Exists(secondaryRoot)) Directory.Delete(secondaryRoot, recursive: true);
        }
    }

    [Fact]
    public void DeleteAnchor_RemovesDatabaseStepsSummariesAndArtifacts()
    {
        var sessionId = Guid.NewGuid().ToString("D").ToLowerInvariant();
        fixture.BuildStandardSession(sessionId, "To Delete", "User prompt", "Assistant text");

        var anchorPath = fixture.Paths.AnchorPath(sessionId);
        AntigravitySessionPackage.WriteAnchorSnapshot(anchorPath);
        Assert.True(File.Exists(anchorPath));
        Assert.True(File.Exists(fixture.Paths.ConversationDatabasePath(sessionId)));

        AntigravitySessionPackage.DeleteAnchor(anchorPath);

        // Verify conversation DB, annotations, brain files are gone
        Assert.False(File.Exists(fixture.Paths.ConversationDatabasePath(sessionId)));
        Assert.False(File.Exists(fixture.Paths.AnnotationPath(sessionId)));
        Assert.False(Directory.Exists(fixture.Paths.BrainSessionDirectory(sessionId)));

        // Verify summary row removed
        var summaries = AntigravityDatabase.ReadSummaries(fixture.Paths.ConversationSummariesDbPath);
        Assert.DoesNotContain(summaries, s => string.Equals(s.ConversationId, sessionId, StringComparison.Ordinal));
    }

    [Fact]
    public void BuildFromLocal_ThrowsInvalidDataException_WhenSessionDoesNotExist()
    {
        var nonExistentId = Guid.NewGuid().ToString("D").ToLowerInvariant();
        Assert.Throws<InvalidDataException>(() => AntigravitySessionPackage.BuildFromLocal(fixture.Paths, nonExistentId));
        Assert.Null(AntigravitySessionPackage.TryBuild(fixture.Paths, nonExistentId));
    }
}
