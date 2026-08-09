using CodexHistorySync.Windows;

namespace CodexHistorySync.Windows.Tests;

public sealed class CodexExecutableLocatorTests
{
    [Fact]
    public void UserProfile_environment_path_wins_over_a_redirected_special_folder()
    {
        var selected = CodexExecutableLocator.SelectUserProfile(@"C:\Users\Gamer", @"C:\Users\Sandbox");

        Assert.Equal(Path.GetFullPath(@"C:\Users\Gamer"), selected, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Configured_executable_is_canonical_and_takes_precedence()
    {
        var configured = @"C:\Tools\Codex\..\Codex\codex.exe";
        var locator = new CodexExecutableLocator(
            configured,
            @"C:\Users\Test",
            @"C:\Path",
            path => StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(path), Path.GetFullPath(configured)),
            _ => []);

        var resolved = locator.Resolve();

        Assert.Equal(Path.GetFullPath(configured), resolved, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void First_party_vscode_extension_executable_is_resolved_with_its_real_install_shape()
    {
        var userProfile = Path.GetFullPath(@"C:\Users\Test");
        var extensionRoot = Path.Combine(userProfile, ".vscode", "extensions");
        var extension = Path.Combine(extensionRoot, "openai.chatgpt-0.146.0-win32-x64");
        var executable = Path.Combine(extension, "bin", "windows-x86_64", "codex.exe");
        var locator = new CodexExecutableLocator(
            null,
            userProfile,
            string.Empty,
            path => StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(path), executable),
            root => StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(root), extensionRoot) ? [extension] : []);

        var resolved = locator.Resolve();

        Assert.Equal(executable, resolved, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Arbitrary_same_shape_extension_publisher_is_not_trusted()
    {
        var userProfile = Path.GetFullPath(@"C:\Users\Test");
        var extensionRoot = Path.Combine(userProfile, ".vscode", "extensions");
        var extension = Path.Combine(extensionRoot, "someone.chatgpt-0.146.0-win32-x64");
        var executable = Path.Combine(extension, "bin", "windows-x86_64", "codex.exe");
        var locator = new CodexExecutableLocator(
            null,
            userProfile,
            string.Empty,
            _ => true,
            root => StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(root), extensionRoot) ? [extension] : []);

        var resolved = locator.Resolve();

        Assert.Null(resolved);
    }

    [Fact]
    public void Cursor_extension_install_is_resolved()
    {
        var userProfile = Path.GetFullPath(@"C:\Users\Test");
        var extensionRoot = Path.Combine(userProfile, ".cursor", "extensions");
        var extension = Path.Combine(extensionRoot, "openai.chatgpt-0.150.0-win32-x64");
        var executable = Path.Combine(extension, "bin", "windows-x86_64", "codex.exe");
        var locator = new CodexExecutableLocator(
            null,
            userProfile,
            string.Empty,
            path => StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(path), executable),
            root => StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(root), extensionRoot) ? [extension] : []);

        var resolved = locator.Resolve();

        Assert.Equal(executable, resolved, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void VSCodium_vscode_oss_extension_install_is_resolved()
    {
        var userProfile = Path.GetFullPath(@"C:\Users\Test");
        var extensionRoot = Path.Combine(userProfile, ".vscode-oss", "extensions");
        var extension = Path.Combine(extensionRoot, "openai.chatgpt-0.150.0-win32-x64");
        var executable = Path.Combine(extension, "bin", "windows-x86_64", "codex.exe");
        var locator = new CodexExecutableLocator(
            null,
            userProfile,
            string.Empty,
            path => StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(path), executable),
            root => StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(root), extensionRoot) ? [extension] : []);

        var resolved = locator.Resolve();

        Assert.Equal(executable, resolved, StringComparer.OrdinalIgnoreCase);
    }
}
