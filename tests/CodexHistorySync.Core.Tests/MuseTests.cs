using System.Text;
using CodexHistorySync.Core.Model;
using CodexHistorySync.Core.Muse;

public sealed class MuseTests
{
    [Fact]
    public void MusePaths_TryResolve_ReturnsNullWhenHomeMissing()
    {
        var paths = MusePaths.TryResolve(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        Assert.Null(paths);
    }

    [Fact]
    public async Task MuseSessionScanner_ScansAndHashesMuseSession()
    {
        var home = Path.Combine(Path.GetTempPath(), "muse-home-" + Guid.NewGuid().ToString("N"));
        var sessions = Path.Combine(home, "sessions");
        var sessionId = Guid.NewGuid().ToString();
        var sessionDir = Path.Combine(sessions, sessionId);
        Directory.CreateDirectory(sessionDir);
        try
        {
            var sessionFile = Path.Combine(sessionDir, MusePaths.SessionFileName);
            await File.WriteAllTextAsync(sessionFile, "{\"role\":\"user\",\"text\":\"hello muse\"}\n", Encoding.UTF8);

            var paths = new MusePaths(home, sessions);
            var scanner = new MuseSessionScanner(TimeSpan.Zero, TimeSpan.Zero);
            var result = await scanner.ScanDetailedAsync(paths, CancellationToken.None);

            Assert.Single(result.Objects);
            var obj = result.Objects[0];
            Assert.Equal(ObjectKind.MuseSession, obj.Kind);
            Assert.Equal(MuseSessionPackage.ToLogicalId(sessionId), obj.Id.Value);
            Assert.True(obj.Hash.Hex.Length == 64);
        }
        finally
        {
            try { Directory.Delete(home, true); } catch { }
        }
    }

    [Fact]
    public void MuseSessionPackage_BuildAndParseRoundTrips()
    {
        var sessionId = Guid.NewGuid().ToString();
        var parent = Path.Combine(Path.GetTempPath(), "muse-pack-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(parent, sessionId);
        Directory.CreateDirectory(dir);
        try
        {
            var sessionFile = Path.Combine(dir, MusePaths.SessionFileName);
            File.WriteAllText(sessionFile, "{\"role\":\"user\",\"text\":\"test content\"}\n", Encoding.UTF8);

            var packageBytes = MuseSessionPackage.BuildFromDirectory(dir);
            var hash = MuseSessionPackage.HashPackage(packageBytes);
            Assert.True(hash.Hex.Length == 64);

            var info = MuseSessionPackage.Parse(packageBytes);
            Assert.Equal(sessionId, info.SessionId);
            Assert.Single(info.Files);
            Assert.Equal(MusePaths.SessionFileName, info.Files[0].Path);

            var home2 = Path.Combine(Path.GetTempPath(), "muse-home2-" + Guid.NewGuid().ToString("N"));
            var sessions2 = Path.Combine(home2, "sessions");
            Directory.CreateDirectory(sessions2);
            try
            {
                var paths2 = new MusePaths(home2, sessions2);
                MuseSessionPackage.Materialize(info, paths2);
                var materialized = Path.Combine(sessions2, sessionId, MusePaths.SessionFileName);
                Assert.True(File.Exists(materialized));
                Assert.Equal("{\"role\":\"user\",\"text\":\"test content\"}\n", File.ReadAllText(materialized));
            }
            finally
            {
                try { Directory.Delete(home2, true); } catch { }
            }
        }
        finally
        {
            try { Directory.Delete(parent, true); } catch { }
        }
    }

    [Fact]
    public void MuseSessionPackage_LogicalIdValidation()
    {
        var good = MuseSessionPackage.ToLogicalId(Guid.NewGuid().ToString());
        Assert.StartsWith(MuseSessionPackage.LogicalIdPrefix, good);
        Assert.True(MuseSessionPackage.IsMuseLogicalId(good));
        Assert.False(MuseSessionPackage.IsMuseLogicalId("mu-not-a-uuid"));
        Assert.False(MuseSessionPackage.IsMuseLogicalId("ki-" + Guid.NewGuid().ToString()));
    }
}
