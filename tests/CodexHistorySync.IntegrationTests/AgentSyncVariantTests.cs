using CodexHistorySync.Cli;

namespace CodexHistorySync.IntegrationTests;

public sealed class AgentSyncVariantTests
{
    [Fact]
    public void Default_options_select_the_full_executable_and_its_sidecar()
    {
        var options = AgentSyncUpdate.Options();

        Assert.Equal(["agent-sync.exe"], options.ExecutableAssetNames);
        Assert.Equal("agent-sync.exe.sha256", options.ChecksumAssetName);
    }

    [Fact]
    public void Light_options_select_the_light_executable_and_its_sidecar()
    {
        var options = AgentSyncUpdate.Options(AgentSyncUpdate.Variant.Light);

        Assert.Equal(["agent-sync-light.exe"], options.ExecutableAssetNames);
        Assert.Equal("agent-sync-light.exe.sha256", options.ChecksumAssetName);
    }

    [Theory]
    [InlineData("light")]
    [InlineData("Light")]
    [InlineData("  light\r\n")]
    public void Light_marker_keeps_the_light_variant(string marker)
    {
        using var layout = CreateVariantLayout(marker);

        Assert.Equal(AgentSyncUpdate.Variant.Light, AgentSyncUpdate.InstalledVariant(layout.ExecutablePath));
    }

    [Theory]
    [InlineData("full")]
    [InlineData("FULL")]
    [InlineData("unexpected")]
    [InlineData("")]
    public void Other_markers_fall_back_to_full(string marker)
    {
        using var layout = CreateVariantLayout(marker);

        Assert.Equal(AgentSyncUpdate.Variant.Full, AgentSyncUpdate.InstalledVariant(layout.ExecutablePath));
    }

    [Fact]
    public void Missing_marker_falls_back_to_full()
    {
        using var layout = CreateVariantLayout(null);

        Assert.Equal(AgentSyncUpdate.Variant.Full, AgentSyncUpdate.InstalledVariant(layout.ExecutablePath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative\\agent-sync.exe")]
    public void Unusable_process_path_falls_back_to_full(string? processPath)
    {
        Assert.Equal(AgentSyncUpdate.Variant.Full, AgentSyncUpdate.InstalledVariant(processPath));
    }

    private static VariantLayout CreateVariantLayout(string? marker)
    {
        var directory = Path.Combine(Path.GetTempPath(), "agent-sync-variant-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        if (marker is not null)
            File.WriteAllText(Path.Combine(directory, AgentSyncUpdate.VariantMarker), marker);
        return new VariantLayout(directory);
    }

    private sealed class VariantLayout(string directory) : IDisposable
    {
        public string ExecutablePath { get; } = Path.Combine(directory, "agent-sync.exe");

        public void Dispose()
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
