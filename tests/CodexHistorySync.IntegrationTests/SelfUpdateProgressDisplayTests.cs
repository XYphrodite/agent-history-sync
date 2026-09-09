using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexHistorySync.Cli;
using CodexHistorySync.Core.Update;
using Spectre.Console;

namespace CodexHistorySync.IntegrationTests;

public sealed class SelfUpdateProgressDisplayTests
{
    private const string Notes = "- Added local MCP session search.";
    private const string ReleaseUrl = "https://github.com/XYphrodite/agent-history-sync/releases/tag/v0.12.0";

    [Fact]
    public async Task RedirectedUpdateWritesEachPhaseOnceAndInstallsTheStreamedAsset()
    {
        using var fixture = new Fixture();

        var result = await fixture.RunAsync(interactive: false);

        Assert.Equal(SelfUpdateStatus.Updated, result.Status);
        Assert.Equal(new[]
        {
            "Checking GitHub...",
            "Release notes (0.12.0):",
            Notes,
            "Full release notes: " + ReleaseUrl,
            "Downloading v0.12.0 (agent-sync.exe)...",
            "Verifying checksum and executable...",
            "Installing and checking the new version..."
        }, fixture.Output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(fixture.Handler.Payload, File.ReadAllBytes(fixture.Installed));
        Assert.Equal(1, fixture.Handler.AssetRequests);
    }

    [Theory]
    [InlineData(true, true, 160)]
    [InlineData(false, true, 160)]
    [InlineData(false, false, 160)]
    [InlineData(true, true, 80)]
    public async Task InteractiveUpdateRendersDownloadDetailsOnlyWhenTheSizeIsKnown(bool headerSize, bool declaredSize, int width)
    {
        using var fixture = new Fixture();
        fixture.Handler.HeaderSize = headerSize;
        fixture.Handler.DeclaredSize = declaredSize;
        fixture.Handler.Slow = true;
        fixture.Width = width;

        var result = await fixture.RunAsync(interactive: true);

        var output = fixture.Output.ToString();
        Assert.Equal(SelfUpdateStatus.Updated, result.Status);
        Assert.Contains("Checking GitHub", output);
        Assert.Contains("v0.12.0", output);
        Assert.Contains("Verifying checksum", output);
        Assert.Contains("Installing and checking", output);
        if (headerSize || declaredSize)
        {
            Assert.Contains("50%", output);
            Assert.True(output.Contains("/s", StringComparison.Ordinal), output);
        }
        else
        {
            Assert.DoesNotContain("%", output);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CheckOnlyDoesNotStartADownload(bool interactive)
    {
        using var fixture = new Fixture();

        var result = await fixture.RunAsync(interactive, checkOnly: true);

        Assert.Equal(SelfUpdateStatus.UpdateAvailable, result.Status);
        Assert.Equal(0, fixture.Handler.AssetRequests);
        Assert.DoesNotContain("Downloading", fixture.Output.ToString());
        Assert.Contains(Notes, fixture.Output.ToString());
        Assert.Contains(ReleaseUrl, fixture.Output.ToString());
        Assert.Equal("installed", File.ReadAllText(fixture.Installed));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NotesAreShownBeforeDownloadingAndAreNotRepeatedForEveryChunk(bool interactive)
    {
        using var fixture = new Fixture();
        fixture.Handler.BeforeDownload = () =>
        {
            Assert.Contains(Notes, fixture.Output.ToString());
            Assert.Contains(ReleaseUrl, fixture.Output.ToString());
            Assert.Equal("installed", File.ReadAllText(fixture.Installed));
        };

        await fixture.RunAsync(interactive);

        Assert.Equal(1, fixture.Handler.AssetRequests);
        Assert.Equal(1, fixture.Output.ToString().Split(Notes).Length - 1);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task AlreadyCurrentDoesNotShowNotesOrDownload(bool interactive, bool checkOnly)
    {
        using var fixture = new Fixture();
        fixture.InstalledVersion = new ReleaseVersion(0, 12, 0);

        var result = await fixture.RunAsync(interactive, checkOnly);

        Assert.Equal(SelfUpdateStatus.AlreadyCurrent, result.Status);
        Assert.DoesNotContain("Release notes", fixture.Output.ToString());
        Assert.Equal(0, fixture.Handler.AssetRequests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PinningTheInstalledVersionStillShowsItsNotes(bool interactive)
    {
        using var fixture = new Fixture();
        fixture.InstalledVersion = new ReleaseVersion(0, 12, 0);

        var result = await fixture.RunAsync(interactive, checkOnly: true, tag: "v0.12.0");

        Assert.Equal(SelfUpdateStatus.UpdateAvailable, result.Status);
        Assert.Contains(Notes, fixture.Output.ToString());
        Assert.Contains(ReleaseUrl, fixture.Output.ToString());
        Assert.Equal(0, fixture.Handler.AssetRequests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EmptyNotesStillShowTheReleaseLink(bool interactive)
    {
        using var fixture = new Fixture();
        fixture.Handler.NotesBody = null;

        await fixture.RunAsync(interactive, checkOnly: true);

        Assert.Contains("No release notes provided.", fixture.Output.ToString());
        Assert.Contains(ReleaseUrl, fixture.Output.ToString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReleaseNotesAreLiteralTextRatherThanConsoleMarkup(bool interactive)
    {
        using var fixture = new Fixture();
        fixture.Handler.NotesBody = "[red]literal[/] Привет 👋\u001b[31m\u0007\u202e";

        await fixture.RunAsync(interactive, checkOnly: true);

        Assert.Contains("[red]literal[/] Привет", fixture.Output.ToString());
        // Culture-aware comparisons can ignore control characters; these must be byte-like checks.
        Assert.DoesNotContain("\u001b[31m", fixture.Output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("\u0007", fixture.Output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("\u202e", fixture.Output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedChecksumStopsTheDisplayAndLeavesTheInstalledBinaryIntact()
    {
        using var fixture = new Fixture();
        fixture.Handler.BadChecksum = true;

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.RunAsync(interactive: true));

        Assert.Equal("installed", File.ReadAllText(fixture.Installed));
        Assert.Empty(Directory.GetDirectories(fixture.DirectoryPath, ".agent-sync-update-*"));
        // A failed live display must release the console so the command can print its error.
        await fixture.Console!.Status().StartAsync("Next operation", _ => Task.CompletedTask);
        Assert.DoesNotContain("Installing and checking", fixture.Output.ToString());
    }

    private sealed class Fixture : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "chs-update-progress-" + Guid.NewGuid().ToString("N"));
        public string Installed => Path.Combine(DirectoryPath, "agent-sync.exe");
        public StringWriter Output { get; } = new();
        public ReleaseHandler Handler { get; } = new();
        public IAnsiConsole? Console { get; private set; }
        public int Width { get; set; } = 160;
        public ReleaseVersion InstalledVersion { get; set; } = new(0, 11, 1);

        public Fixture()
        {
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(Installed, "installed");
        }

        public async Task<SelfUpdateReport> RunAsync(bool interactive, bool checkOnly = false, string? tag = null)
        {
            Console = AnsiConsole.Create(new AnsiConsoleSettings
            {
                Ansi = interactive ? AnsiSupport.Yes : AnsiSupport.No,
                Interactive = interactive ? InteractionSupport.Yes : InteractionSupport.No,
                ColorSystem = ColorSystemSupport.NoColors,
                Out = new ConsoleOutput(Output, interactive, Width)
            });
            using var source = new GitHubReleaseSource(Handler);
            var service = new SelfUpdateService(Installed, InstalledVersion, source,
                probe: async (_, ct) =>
                {
                    if (Handler.Slow) await Task.Delay(250, ct);
                    return true;
                });
            return await new SelfUpdateProgressDisplay(Console).RunAsync(service,
                new SelfUpdateRequest(CheckOnly: checkOnly, Tag: tag), CancellationToken.None);
        }

        public void Dispose()
        {
            Directory.Delete(DirectoryPath, recursive: true);
            Output.Dispose();
        }
    }

    private sealed class ConsoleOutput(TextWriter writer, bool interactive, int width) : IAnsiConsoleOutput
    {
        public TextWriter Writer => writer;
        public bool IsTerminal => interactive;
        public int Width => width;
        public int Height => 40;
        public void SetEncoding(Encoding encoding) { }
    }

    private sealed class ReleaseHandler : HttpMessageHandler
    {
        private const string AssetUrl = "https://github.com/XYphrodite/agent-history-sync/releases/download/v0.12.0/agent-sync.exe";
        public byte[] Payload { get; } = [(byte)'M', (byte)'Z', .. new byte[163838]];
        public bool HeaderSize { get; set; } = true;
        public bool DeclaredSize { get; set; } = true;
        public bool BadChecksum { get; set; }
        public bool Slow { get; set; }
        public int AssetRequests { get; private set; }
        public string? NotesBody { get; set; } = Notes;
        public Action? BeforeDownload { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Slow) await Task.Delay(250, cancellationToken);
            var url = request.RequestUri!.AbsoluteUri;
            HttpContent content;
            if (url.Contains("api.github.com", StringComparison.Ordinal))
            {
                content = new StringContent(JsonSerializer.Serialize(new
                {
                    tag_name = "v0.12.0",
                    body = NotesBody,
                    assets = new[]
                    {
                        new { name = "agent-sync.exe", browser_download_url = AssetUrl, size = DeclaredSize ? Payload.Length : 0 },
                        new { name = "agent-sync.exe.sha256", browser_download_url = AssetUrl + ".sha256", size = 80 }
                    }
                }));
            }
            else if (url.EndsWith(".sha256", StringComparison.Ordinal))
            {
                content = new StringContent((BadChecksum ? new string('a', 64) :
                    Convert.ToHexString(SHA256.HashData(Payload))) + "  agent-sync.exe");
            }
            else
            {
                AssetRequests++;
                BeforeDownload?.Invoke();
                content = new StreamContent(new DownloadStream(Payload, Slow, HeaderSize));
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }

    private sealed class DownloadStream(byte[] payload, bool slow, bool knownLength) : MemoryStream(payload)
    {
        public override bool CanSeek => knownLength;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (slow) await Task.Delay(250, cancellationToken);
            return await base.ReadAsync(buffer[..(int)Math.Min(buffer.Length, Length / 2)], cancellationToken);
        }
    }
}
