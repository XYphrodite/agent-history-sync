using CodexHistorySync.Core.IO;

namespace CodexHistorySync.Core.Sync;

internal interface IOperationDirectoryCleaner
{
    void Delete(string operationDirectory, string markerFileName);
}

internal sealed class OperationDirectoryCleaner : IOperationDirectoryCleaner
{
    public void Delete(string operationDirectory, string markerFileName)
    {
        if (!Directory.Exists(operationDirectory)) return;
        var root = Path.GetFullPath(operationDirectory);
        var marker = Path.Combine(root, markerFileName);
        PathSafety.RejectReparsePoints(root, nameof(operationDirectory));
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .Select(Path.GetFullPath)
                     .Where(path => !StringComparer.OrdinalIgnoreCase.Equals(path, marker)))
        {
            PathSafety.RejectReparsePoints(file, nameof(operationDirectory));
            File.Delete(file);
        }
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
        {
            PathSafety.RejectReparsePoints(directory, nameof(operationDirectory));
            Directory.Delete(directory, recursive: false);
        }
        if (File.Exists(marker)) File.Delete(marker);
        Directory.Delete(root, recursive: false);
    }
}
