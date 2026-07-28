using CodexHistorySync.Core.Codex;

return await RunAsync(args);

static async Task<int> RunAsync(string[] args)
{
    if (args.Length != 5 || args[0] != "doctor") return InvalidArguments();
    var values = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var index = 1; index < args.Length; index += 2)
    {
        if ((args[index] is not "--compatibility-session" and not "--codex-exe") || !values.TryAdd(args[index], args[index + 1]) || string.IsNullOrWhiteSpace(args[index + 1])) return InvalidArguments();
    }
    if (!values.TryGetValue("--compatibility-session", out var session) || !values.TryGetValue("--codex-exe", out var codex)) return InvalidArguments();
    var result = await new CodexCompatibilityProbe().ProbeAsync(codex, session, CancellationToken.None);
    Console.WriteLine($"Codex version: {result.CodexVersion}");
    Console.WriteLine($"Diagnostic: {result.Diagnostic}");
    return result.IsCompatible ? 0 : 3;
}

static int InvalidArguments()
{
    Console.Error.WriteLine("Usage: codex-sync doctor --compatibility-session <path> --codex-exe <path>");
    return 2;
}
