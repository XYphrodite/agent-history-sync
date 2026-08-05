using System.ComponentModel;
using CodexHistorySync.Windows;

namespace CodexHistorySync.Windows.Tests;

public sealed class CodexProcessDetectorTests
{
    [Fact]
    public void Configured_path_does_not_trust_an_arbitrary_process_with_only_the_same_custom_name()
    {
        var configured = Path.GetFullPath(@"C:\Program Files\Codex\codex-custom.exe");
        var secondTrustedRoot = Path.GetFullPath(@"C:\Users\Test\AppData\Local\Programs\OpenAI");
        var catalog = new FakeProcessCatalog(new FakeProcess(10, "codex-custom", @"C:\Temp\codex-custom.exe"));
        var detector = new CodexProcessDetector(
            new CodexProcessDetectorOptions(configured, ["codex"],
                [Path.GetDirectoryName(configured)!, secondTrustedRoot]), catalog);

        Assert.False(detector.IsRunning());
    }

    [Fact]
    public void Configured_path_matches_canonical_equivalent_path()
    {
        var configured = Path.GetFullPath(@"C:\Program Files\Codex\..\Codex\codex.exe");
        var catalog = new FakeProcessCatalog(new FakeProcess(10, "codex", @"C:\Program Files\Codex\codex.exe"));
        var detector = new CodexProcessDetector(
            new CodexProcessDetectorOptions(configured, ["codex"], [Path.GetDirectoryName(configured)!]), catalog);

        Assert.True(detector.IsRunning());
    }

    [Fact]
    public void Configured_path_and_known_name_in_a_second_trusted_root_are_additive()
    {
        var configured = Path.GetFullPath(@"C:\Program Files\Codex\codex.exe");
        var secondTrustedRoot = Path.GetFullPath(@"C:\Users\Test\AppData\Local\Programs\OpenAI");
        var catalog = new FakeProcessCatalog(
            new FakeProcess(10, "codex", Path.Combine(secondTrustedRoot, "codex.exe")));
        var detector = new CodexProcessDetector(
            new CodexProcessDetectorOptions(configured, ["codex"],
                [Path.GetDirectoryName(configured)!, secondTrustedRoot]), catalog);

        Assert.True(detector.IsRunning());
    }

    [Theory]
    [InlineData(5)]
    [InlineData(299)]
    public void Access_denied_or_unavailable_path_inspection_fails_closed_as_active(int nativeErrorCode)
    {
        var configured = Path.GetFullPath(@"C:\Program Files\Codex\codex.exe");
        var catalog = new FakeProcessCatalog(new FakeProcess(10, "codex", new Win32Exception(nativeErrorCode)));
        var detector = new CodexProcessDetector(
            new CodexProcessDetectorOptions(configured, ["codex"], [Path.GetDirectoryName(configured)!]), catalog);

        Assert.True(detector.IsRunning());
    }

    [Fact]
    public void Known_name_fallback_is_configurable_when_no_executable_is_configured()
    {
        var catalog = new FakeProcessCatalog(new FakeProcess(10, "FutureCodex", @"C:\Future\FutureCodex.exe"));
        var detector = new CodexProcessDetector(
            new CodexProcessDetectorOptions(null, ["FutureCodex"], []), catalog);

        Assert.True(detector.IsRunning());
    }

    [Fact]
    public void Known_codex_name_at_an_unrecognized_accessible_path_fails_closed_as_active()
    {
        var trustedRoot = Path.GetFullPath(@"C:\Program Files\Codex");
        var catalog = new FakeProcessCatalog(new FakeProcess(10, "codex", @"C:\Temp\codex.exe"));
        var detector = new CodexProcessDetector(
            new CodexProcessDetectorOptions(null, ["codex"], [trustedRoot]), catalog);

        Assert.True(detector.IsRunning());
    }

    [Fact]
    public void Default_options_recognize_the_first_party_vscode_extension_executable_shape()
    {
        var executable = Path.Combine(CodexExecutableLocator.DefaultUserProfile(),
            ".vscode", "extensions", "openai.chatgpt-0.146.0-win32-x64", "bin", "windows-x86_64", "codex.exe");
        var catalog = new FakeProcessCatalog(new FakeProcess(10, "codex", executable));

        var detector = new CodexProcessDetector(new CodexProcessDetectorOptions(), catalog);

        Assert.True(detector.IsRunning());
    }

    [Fact]
    public async Task WaitForExitAsync_waits_for_every_matching_process_and_reenumerates()
    {
        var first = new FakeProcess(10, "codex", @"C:\Program Files\Codex\codex.exe");
        var second = new FakeProcess(11, "codex", @"C:\Program Files\Codex\codex.exe");
        var catalog = new FakeProcessCatalog(first, second);
        var configured = Path.GetFullPath(@"C:\Program Files\Codex\codex.exe");
        var detector = new CodexProcessDetector(
            new CodexProcessDetectorOptions(configured, ["codex"], [Path.GetDirectoryName(configured)!], TimeSpan.FromMilliseconds(1)), catalog);

        var waiting = detector.WaitForExitAsync(CancellationToken.None);
        first.Exit();
        await Task.Yield();
        Assert.False(waiting.IsCompleted);

        second.Exit();
        await waiting.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(catalog.EnumerationCount >= 2);
    }

    private sealed class FakeProcessCatalog(params FakeProcess[] processes) : IProcessCatalog
    {
        public int EnumerationCount { get; private set; }

        public IReadOnlyList<IProcessObservation> FindByNames(IReadOnlySet<string> names)
        {
            EnumerationCount++;
            return processes.Where(process => !process.Exited && names.Contains(process.Name)).Cast<IProcessObservation>().ToArray();
        }
    }

    private sealed class FakeProcess : IProcessObservation
    {
        private readonly string? path;
        private readonly Exception? inspectionFailure;
        private readonly TaskCompletionSource exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeProcess(int id, string name, string path)
        {
            Id = id;
            Name = name;
            this.path = path;
        }

        public FakeProcess(int id, string name, Exception inspectionFailure)
        {
            Id = id;
            Name = name;
            this.inspectionFailure = inspectionFailure;
        }

        public int Id { get; }
        public string Name { get; }
        public bool Exited { get; private set; }
        public string GetExecutablePath() => inspectionFailure is null ? path! : throw inspectionFailure;
        public Task WaitForExitAsync(CancellationToken cancellationToken) => exited.Task.WaitAsync(cancellationToken);
        public void Dispose() { }

        public void Exit()
        {
            Exited = true;
            exited.TrySetResult();
        }
    }
}
