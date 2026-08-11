using System.Diagnostics;
using CodexHistorySync.Core.Conversion;
using Xunit.Sdk;

namespace CodexHistorySync.Core.Tests.Conversion;

internal static class ConversationWriterReparseTestSupport
{
    public static void CreateDirectoryReparsePoint(string link, string target)
    {
        Directory.CreateDirectory(target);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var startInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                startInfo.ArgumentList.Add("/d");
                startInfo.ArgumentList.Add("/c");
                startInfo.ArgumentList.Add("mklink");
                startInfo.ArgumentList.Add("/J");
                startInfo.ArgumentList.Add(link);
                startInfo.ArgumentList.Add(target);
                using var process = Process.Start(startInfo)
                    ?? throw new InvalidOperationException("Unable to start the directory-junction helper.");
                var output = process.StandardOutput.ReadToEnd();
                var error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0)
                    throw new IOException($"Directory-junction creation failed with exit code {process.ExitCode}: {output}{error}");
            }
            else
            {
                Directory.CreateSymbolicLink(link, target);
            }

            if (!File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("The directory link is not reported as a reparse point.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            throw SkipException.ForSkip($"Directory reparse points are unavailable: {exception.GetType().Name}");
        }
    }

    public static void RemoveDirectoryReparsePoint(string link)
    {
        if (Directory.Exists(link) && File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint))
            Directory.Delete(link);
    }
}

internal sealed class IndependentConversationStagingDirectoryFactory(string parentDirectory)
    : IConversationStagingDirectoryFactory
{
    public IConversationStagingDirectory Create(string ignoredParentDirectory) =>
        SystemConversationStagingDirectoryFactory.Instance.Create(parentDirectory);
}
