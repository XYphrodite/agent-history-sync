using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexHistorySync.Core.Antigravity;
using CodexHistorySync.Core.Model;
using Xunit;

namespace CodexHistorySync.Core.Tests.Antigravity;

public sealed class AntigravityPackagingChallengeTests : IDisposable
{
    private readonly AntigravityTestFixture fixture = new();

    public void Dispose()
    {
        fixture.Dispose();
    }

    [Fact]
    public void Stress_WorkspaceUris_CanonicalOrderingEnsuresIdenticalHash()
    {
        var sessionId1 = Guid.NewGuid().ToString("D").ToLowerInvariant();
        var sessionId2 = Guid.NewGuid().ToString("D").ToLowerInvariant();

        // Session 1 has URIs in ascending order
        var uris1 = new[] { "file:///A", "file:///B", "file:///C" };
        var row1 = new AntigravitySummaryRow(
            sessionId1, null, 0, "teamwork", "Test Ordering", "COMPLETED", uris1,
            DateTimeOffset.FromUnixTimeSeconds(1000), DateTimeOffset.FromUnixTimeSeconds(2000));
        AntigravityDatabase.WriteSummary(fixture.Paths.ConversationSummariesDbPath, row1);

        // Session 2 has same URIs in reverse order
        var uris2 = new[] { "file:///C", "file:///B", "file:///A" };
        var row2 = new AntigravitySummaryRow(
            sessionId2, null, 0, "teamwork", "Test Ordering", "COMPLETED", uris2,
            DateTimeOffset.FromUnixTimeSeconds(1000), DateTimeOffset.FromUnixTimeSeconds(2000));
        AntigravityDatabase.WriteSummary(fixture.Paths.ConversationSummariesDbPath, row2);

        var pkg1 = AntigravitySessionPackage.BuildFromLocal(fixture.Paths, sessionId1);
        var pkg2 = AntigravitySessionPackage.BuildFromLocal(fixture.Paths, sessionId2);

        // Replace sessionId in JSON to compare structural equality
        var json1 = Encoding.UTF8.GetString(pkg1).Replace(sessionId1, "fixed-id");
        var json2 = Encoding.UTF8.GetString(pkg2).Replace(sessionId2, "fixed-id");

        Assert.Equal(json1, json2);
    }

    [Fact]
    public void Stress_Artifacts_OrdinalOrderingEnsuresIdenticalHash()
    {
        var sessionId = Guid.NewGuid().ToString("D").ToLowerInvariant();
        fixture.BuildStandardSession(sessionId, "Artifact Ordering", "prompt", "reply");

        var brainDir = fixture.Paths.BrainSessionDirectory(sessionId);
        Directory.CreateDirectory(Path.Combine(brainDir, "z_folder"));
        Directory.CreateDirectory(Path.Combine(brainDir, "a_folder"));

        File.WriteAllText(Path.Combine(brainDir, "z_folder", "file2.txt"), "content 2\n");
        File.WriteAllText(Path.Combine(brainDir, "a_folder", "file1.txt"), "content 1\n");
        File.WriteAllText(Path.Combine(brainDir, "root.txt"), "content root\n");

        var pkg1 = AntigravitySessionPackage.BuildFromLocal(fixture.Paths, sessionId);
        var pkg2 = AntigravitySessionPackage.BuildFromLocal(fixture.Paths, sessionId);

        Assert.Equal(pkg1, pkg2);
        Assert.Equal(AntigravitySessionPackage.HashPackage(pkg1), AntigravitySessionPackage.HashPackage(pkg2));
    }

    [Fact]
    public void Stress_Steps_OutOfOrderInDatabase_SortsByIndex()
    {
        var sessionId = Guid.NewGuid().ToString("D").ToLowerInvariant();
        fixture.InsertSummaryRow(sessionId, "Steps Out of Order");

        // Insert steps out of order: idx 2, then idx 0, then idx 1
        fixture.InsertAssistantStep(sessionId, idx: 2, text: "Step 2");
        fixture.InsertUserStep(sessionId, idx: 0, text: "Step 0");
        fixture.InsertAssistantStep(sessionId, idx: 1, text: "Step 1");

        var pkg = AntigravitySessionPackage.BuildFromLocal(fixture.Paths, sessionId);
        using var doc = JsonDocument.Parse(pkg);
        var steps = doc.RootElement.GetProperty("steps");
        Assert.Equal(3, steps.GetArrayLength());
        Assert.Equal(0, steps[0].GetProperty("Index").GetInt32());
        Assert.Equal(1, steps[1].GetProperty("Index").GetInt32());
        Assert.Equal(2, steps[2].GetProperty("Index").GetInt32());
    }

    [Fact]
    public void Stress_LineEndings_NormalizesCrlfToLf()
    {
        var sessionId = Guid.NewGuid().ToString("D").ToLowerInvariant();
        fixture.BuildStandardSession(sessionId, "CRLF Test", "prompt", "reply");

        var annotPath = fixture.Paths.AnnotationPath(sessionId);
        File.WriteAllText(annotPath, "line1\r\nline2\r\nline3\r\n");

        var transcriptPath = fixture.Paths.TranscriptPath(sessionId);
        File.WriteAllText(transcriptPath, "{\"t\":1}\r\n{\"t\":2}\r\n");

        var brainDir = fixture.Paths.BrainSessionDirectory(sessionId);
        File.WriteAllText(Path.Combine(brainDir, "text.txt"), "hello\r\nworld\r\n");

        var pkg = AntigravitySessionPackage.BuildFromLocal(fixture.Paths, sessionId);
        var json = Encoding.UTF8.GetString(pkg);

        Assert.DoesNotContain("\\r\\n", json);
        Assert.DoesNotContain("\r\n", json);
    }

    [Fact]
    public void Stress_Base64Payload_PreservesAll256ByteValuesExactly()
    {
        var sessionId = Guid.NewGuid().ToString("D").ToLowerInvariant();
        fixture.InsertSummaryRow(sessionId, "Base64 Byte Fidelity");

        var allBytes = new byte[256];
        for (var i = 0; i < 256; i++) allBytes[i] = (byte)i;

        var dbPath = fixture.Paths.ConversationDatabasePath(sessionId);
        var step = new AntigravityStepRow(0, AntigravityProtobuf.StepTypeUserInput, 1, allBytes);
        AntigravityDatabase.WriteSteps(dbPath, "traj_" + sessionId, sessionId, new[] { step });

        var pkg = AntigravitySessionPackage.BuildFromLocal(fixture.Paths, sessionId);
        using var targetFixture = new AntigravityTestFixture();
        AntigravitySessionPackage.Materialize(targetFixture.Paths, pkg);

        var readSteps = AntigravityDatabase.ReadSteps(targetFixture.Paths.ConversationDatabasePath(sessionId));
        Assert.Single(readSteps);
        Assert.Equal(allBytes, readSteps[0].Payload);
    }

    [Fact]
    public void Stress_RoundTripIdempotency_BuildMaterializeBuild_ProducesExactHash()
    {
        var sessionId = Guid.NewGuid().ToString("D").ToLowerInvariant();
        fixture.BuildStandardSession(sessionId, "Roundtrip Idempotency", "prompt", "reply");

        var brainDir = fixture.Paths.BrainSessionDirectory(sessionId);
        File.WriteAllText(Path.Combine(brainDir, "artifact.md"), "# Title\r\nSome markdown text\r\n");
        File.WriteAllBytes(Path.Combine(brainDir, "binary.bin"), new byte[] { 0x00, 0x11, 0x22, 0x33 });

        var pkg1 = AntigravitySessionPackage.BuildFromLocal(fixture.Paths, sessionId);
        var hash1 = AntigravitySessionPackage.HashPackage(pkg1);

        using var targetFixture1 = new AntigravityTestFixture();
        AntigravitySessionPackage.Materialize(targetFixture1.Paths, pkg1);
        var pkg2 = AntigravitySessionPackage.BuildFromLocal(targetFixture1.Paths, sessionId);
        var hash2 = AntigravitySessionPackage.HashPackage(pkg2);

        Assert.Equal(hash1, hash2);

        using var targetFixture2 = new AntigravityTestFixture();
        AntigravitySessionPackage.Materialize(targetFixture2.Paths, pkg2);
        var pkg3 = AntigravitySessionPackage.BuildFromLocal(targetFixture2.Paths, sessionId);
        var hash3 = AntigravitySessionPackage.HashPackage(pkg3);

        Assert.Equal(hash1, hash3);
    }

    [Fact]
    public void Stress_MultipleSessionsInSummaryDb_PackagesOnlyTargetSession()
    {
        var sessionA = Guid.NewGuid().ToString("D").ToLowerInvariant();
        var sessionB = Guid.NewGuid().ToString("D").ToLowerInvariant();

        fixture.BuildStandardSession(sessionA, "Session A Title", "Prompt A", "Reply A");
        fixture.BuildStandardSession(sessionB, "Session B Title", "Prompt B", "Reply B");

        var pkgA = AntigravitySessionPackage.BuildFromLocal(fixture.Paths, sessionA);
        var jsonA = Encoding.UTF8.GetString(pkgA);

        Assert.Contains(sessionA, jsonA);
        Assert.Contains("Session A Title", jsonA);
        Assert.DoesNotContain(sessionB, jsonA);
        Assert.DoesNotContain("Session B Title", jsonA);

        // Now delete anchor for session A and verify session B remains untouched
        var anchorA = fixture.Paths.AnchorPath(sessionA);
        AntigravitySessionPackage.WriteAnchorSnapshot(anchorA);
        AntigravitySessionPackage.DeleteAnchor(anchorA);

        var summaries = AntigravityDatabase.ReadSummaries(fixture.Paths.ConversationSummariesDbPath);
        Assert.DoesNotContain(summaries, s => s.ConversationId == sessionA);
        Assert.Contains(summaries, s => s.ConversationId == sessionB);
        Assert.True(File.Exists(fixture.Paths.ConversationDatabasePath(sessionB)));
    }

    [Fact]
    public void Challenge_PathTraversalInArtifact_EscapesBrainDirectory()
    {
        var sessionId = Guid.NewGuid().ToString("D").ToLowerInvariant();
        var outsideFile = Path.Combine(fixture.Paths.Home, "escaped_outside_brain.txt");
        if (File.Exists(outsideFile)) File.Delete(outsideFile);

        // Construct malicious package JSON with relative path attempting traversal
        var maliciousJson = $$"""
        {
          "v": 1,
          "id": "{{sessionId}}",
          "summary": {
            "conversationId": "{{sessionId}}",
            "nestingDepth": 0,
            "title": "Malicious Traversal Package",
            "workspaceUris": [],
            "createdAtUnix": 1000,
            "updatedAtUnix": 1000
          },
          "steps": [],
          "artifacts": [
            {
              "relativePath": "../../escaped_outside_brain.txt",
              "contentText": "PWNED BY PATH TRAVERSAL",
              "contentBase64": null
            }
          ]
        }
        """;

        var packageBytes = Encoding.UTF8.GetBytes(maliciousJson);

        try
        {
            AntigravitySessionPackage.Materialize(fixture.Paths, packageBytes);
            var escapedFileExists = File.Exists(outsideFile);
            if (escapedFileExists)
            {
                File.Delete(outsideFile);
            }
            Assert.False(escapedFileExists, "SECURITY VULNERABILITY: AntigravitySessionPackage.Materialize permitted path traversal outside brain directory!");
        }
        catch (ArgumentException)
        {
            // Expected defense: throws when path traversal is detected
        }
        catch (InvalidDataException)
        {
            // Expected defense: throws when path traversal is detected
        }
    }

    [Fact]
    public void Challenge_ArtifactDeletion_StaleArtifactCausesHashMismatchOnUpdate()
    {
        var sessionId = Guid.NewGuid().ToString("D").ToLowerInvariant();
        fixture.BuildStandardSession(sessionId, "Artifact Deletion Test", "prompt", "reply");

        var brainDir = fixture.Paths.BrainSessionDirectory(sessionId);
        File.WriteAllText(Path.Combine(brainDir, "kept.txt"), "kept content");
        File.WriteAllText(Path.Combine(brainDir, "deleted_in_v2.txt"), "to be deleted in v2");

        var pkgV1 = AntigravitySessionPackage.BuildFromLocal(fixture.Paths, sessionId);

        using var targetFixture = new AntigravityTestFixture();
        AntigravitySessionPackage.Materialize(targetFixture.Paths, pkgV1);

        // Now locally on source, delete deleted_in_v2.txt and build pkgV2
        File.Delete(Path.Combine(brainDir, "deleted_in_v2.txt"));
        var pkgV2 = AntigravitySessionPackage.BuildFromLocal(fixture.Paths, sessionId);
        var expectedV2Hash = AntigravitySessionPackage.HashPackage(pkgV2);

        // Materialize pkgV2 on target
        AntigravitySessionPackage.Materialize(targetFixture.Paths, pkgV2);

        // Rebuild from targetFixture
        var rebuiltOnTarget = AntigravitySessionPackage.BuildFromLocal(targetFixture.Paths, sessionId);
        var actualTargetHash = AntigravitySessionPackage.HashPackage(rebuiltOnTarget);

        Assert.Equal(expectedV2Hash, actualTargetHash);
    }
}
