using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CodexHistorySync.Cli;

internal static class WindowsOwnedTreeDeleter
{
    private const uint DeleteAccess = 0x00010000;
    private const uint FileReadDataOrListDirectory = 0x00000001;
    private const uint FileReadAttributes = 0x00000080;
    private const uint Synchronize = 0x00100000;
    private const uint FileShareRead = 0x00000001;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint ObjCaseInsensitive = 0x00000040;
    private const uint FileOpen = 1;
    private const uint FileDirectoryFile = 0x00000001;
    private const uint FileNonDirectoryFile = 0x00000040;
    private const uint FileSynchronousIoNonAlert = 0x00000020;
    private const uint FileOpenReparsePoint = 0x00200000;
    private const uint FileDispositionDelete = 0x00000001;
    private const uint FileDispositionPosixSemantics = 0x00000002;
    private const uint FileDispositionIgnoreReadonlyAttribute = 0x00000010;
    private const int StatusNoMoreFiles = unchecked((int)0x80000006);
    private const int FileAttributeDirectory = 0x10;
    private const int FileAttributeReparsePoint = 0x400;
    private const int FileIdBothDirectoryInformation = 37;
    private const int FileIdInfo = 18;
    private const int FileAttributeTagInfo = 9;
    private const int FileDispositionInfoEx = 21;
    private const int DirectoryEntryNameOffset = 104;

    internal readonly record struct FileIdentity(ulong VolumeSerialNumber, ulong Low, ulong High);

    public static bool TryGetIdentity(string path, out FileIdentity identity)
    {
        identity = default;
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var handle = OpenRoot(path);
            identity = ReadIdentity(handle);
            return true;
        }
        catch (Exception exception) when (IsNativeFailure(exception))
        {
            return false;
        }
    }

    public static bool TryDelete(string rootPath, FileIdentity expectedRootIdentity, string markerName,
        string markerToken, Func<bool>? afterValidation, Func<bool>? beforeFirstMutation)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var retained = new List<Node>();
        try
        {
            var rootHandle = OpenRoot(rootPath);
            Node root;
            try { root = new Node(string.Empty, isDirectory: true, rootHandle, ReadIdentity(rootHandle)); }
            catch
            {
                rootHandle.Dispose();
                throw;
            }
            retained.Add(root);
            RequireIdentity(root, expectedRootIdentity);
            RequireConcreteType(root, expectDirectory: true);

            Collect(root, retained);
            var marker = FindMarker(root, markerName);
            VerifyMarker(marker, markerToken);

            if (afterValidation is not null && !afterValidation()) return false;
            ValidateSnapshot(root, marker, markerToken);
            if (beforeFirstMutation is not null && !beforeFirstMutation()) return false;

            foreach (var node in PostOrder(root).Where(node => !ReferenceEquals(node, marker)))
                DeleteByHandle(node.Handle, node.Name);
            DeleteByHandle(marker.Handle, marker.Name);
            DeleteByHandle(root.Handle, "<root>");
            return true;
        }
        catch (Exception exception) when (IsNativeFailure(exception))
        {
            return false;
        }
        finally
        {
            for (var index = retained.Count - 1; index >= 0; index--) retained[index].Handle.Dispose();
        }
    }

    public static bool TryDeleteDescendantTree(string rootPath, string targetPath,
        Func<bool>? afterTreeCapture = null, Func<bool>? beforeFirstMutation = null)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var retained = new List<Node>();
        try
        {
            var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
            var canonicalTarget = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetPath));
            var relative = Path.GetRelativePath(canonicalRoot, canonicalTarget);
            if (relative is "." or ".." || Path.IsPathRooted(relative) ||
                relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
                return false;
            var segments = relative.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0) return false;

            var anchorHandle = OpenRoot(canonicalRoot);
            Node anchor;
            try { anchor = new Node(string.Empty, isDirectory: true, anchorHandle, ReadIdentity(anchorHandle)); }
            catch
            {
                anchorHandle.Dispose();
                throw;
            }
            retained.Add(anchor);
            RequireConcreteType(anchor, expectDirectory: true);

            var current = anchor;
            foreach (var segment in segments)
            {
                ValidateName(segment);
                var handle = OpenChild(current.Handle, segment, expectDirectory: true);
                Node child;
                try { child = new Node(segment, isDirectory: true, handle, ReadIdentity(handle)); }
                catch
                {
                    handle.Dispose();
                    throw;
                }
                retained.Add(child);
                RequireConcreteType(child, expectDirectory: true);
                current = child;
            }

            var target = current;
            Collect(target, retained);
            if (afterTreeCapture is not null && !afterTreeCapture()) return false;
            ValidateSnapshot(target);
            if (beforeFirstMutation is not null && !beforeFirstMutation()) return false;

            foreach (var node in PostOrder(target)) DeleteByHandle(node.Handle, node.Name);
            return true;
        }
        catch (Exception exception) when (IsNativeFailure(exception))
        {
            return false;
        }
        finally
        {
            for (var index = retained.Count - 1; index >= 0; index--) retained[index].Handle.Dispose();
        }
    }

    private static SafeFileHandle OpenRoot(string path)
    {
        var handle = CreateFileW(path, DeleteAccess | FileReadDataOrListDirectory | FileReadAttributes | Synchronize,
            FileShareRead, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        return handle;
    }

    private static SafeFileHandle OpenChild(SafeFileHandle parent, string name, bool expectDirectory)
    {
        ValidateName(name);
        var nameBuffer = Marshal.StringToHGlobalUni(name);
        var unicodePointer = IntPtr.Zero;
        try
        {
            var unicode = new UnicodeString
            {
                Length = checked((ushort)(name.Length * sizeof(char))),
                MaximumLength = checked((ushort)(name.Length * sizeof(char))),
                Buffer = nameBuffer
            };
            unicodePointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
            Marshal.StructureToPtr(unicode, unicodePointer, false);
            var attributes = new ObjectAttributes
            {
                Length = Marshal.SizeOf<ObjectAttributes>(),
                RootDirectory = parent.DangerousGetHandle(),
                ObjectName = unicodePointer,
                Attributes = ObjCaseInsensitive
            };
            var options = FileOpenReparsePoint | FileSynchronousIoNonAlert |
                          (expectDirectory ? FileDirectoryFile : FileNonDirectoryFile);
            var status = NtCreateFile(out var handle,
                DeleteAccess | FileReadDataOrListDirectory | FileReadAttributes | Synchronize,
                ref attributes, out _, IntPtr.Zero, 0, FileShareRead, FileOpen, options, IntPtr.Zero, 0);
            if (status < 0)
            {
                handle?.Dispose();
                throw new IOException($"Native relative open failed with NTSTATUS 0x{status:X8}.");
            }
            return handle;
        }
        finally
        {
            if (unicodePointer != IntPtr.Zero) Marshal.FreeHGlobal(unicodePointer);
            Marshal.FreeHGlobal(nameBuffer);
        }
    }

    private static void Collect(Node directory, List<Node> retained)
    {
        foreach (var entry in Enumerate(directory.Handle))
        {
            var child = new Node(entry.Name, entry.IsDirectory,
                OpenChild(directory.Handle, entry.Name, entry.IsDirectory), default);
            retained.Add(child);
            child.Identity = ReadIdentity(child.Handle);
            RequireConcreteType(child, entry.IsDirectory);
            directory.Children.Add(child);
            if (child.IsDirectory) Collect(child, retained);
        }
    }

    private static IReadOnlyList<DirectoryEntry> Enumerate(SafeFileHandle directory)
    {
        var result = new List<DirectoryEntry>();
        var buffer = new byte[64 * 1024];
        var restart = true;
        while (true)
        {
            Array.Clear(buffer);
            var status = NtQueryDirectoryFile(directory, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                out var ioStatus, buffer, (uint)buffer.Length, FileIdBothDirectoryInformation, 0, IntPtr.Zero,
                restart ? (byte)1 : (byte)0);
            restart = false;
            if (status == StatusNoMoreFiles) break;
            if (status < 0)
                throw new IOException($"Native directory enumeration failed with NTSTATUS 0x{status:X8}.");
            var returnedLength = checked((int)ioStatus.Information);
            if (returnedLength <= 0 || returnedLength > buffer.Length)
                throw new InvalidDataException("Directory enumeration returned an invalid buffer length.");

            var offset = 0;
            while (true)
            {
                if (offset < 0 || offset > returnedLength - DirectoryEntryNameOffset)
                    throw new InvalidDataException("Directory enumeration returned an invalid record offset.");
                var next = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset, 4));
                var attributes = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset + 56, 4));
                var nameByteLength = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset + 60, 4));
                if ((nameByteLength & 1) != 0 || nameByteLength > returnedLength - offset - DirectoryEntryNameOffset)
                    throw new InvalidDataException("Directory enumeration returned an invalid name length.");
                var name = System.Text.Encoding.Unicode.GetString(buffer, offset + DirectoryEntryNameOffset,
                    checked((int)nameByteLength));
                if (name is not "." and not "..")
                {
                    ValidateName(name);
                    if ((attributes & FileAttributeReparsePoint) != 0)
                        throw new InvalidDataException("Reparse points are not owned temporary content.");
                    result.Add(new DirectoryEntry(name, (attributes & FileAttributeDirectory) != 0));
                }
                if (next == 0) break;
                if (next < DirectoryEntryNameOffset || next > returnedLength - offset)
                    throw new InvalidDataException("Directory enumeration returned an invalid next offset.");
                offset += checked((int)next);
            }
        }
        return result;
    }

    private static void ValidateSnapshot(Node root, Node marker, string markerToken)
    {
        ValidateSnapshot(root);
        VerifyMarker(marker, markerToken);
    }

    private static void ValidateSnapshot(Node root)
    {
        foreach (var node in Flatten(root))
        {
            RequireIdentity(node, node.Identity);
            RequireConcreteType(node, node.IsDirectory);
            if (!node.IsDirectory) continue;
            var actual = Enumerate(node.Handle).OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            var expected = node.Children.Select(child => new DirectoryEntry(child.Name, child.IsDirectory))
                .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            if (!actual.SequenceEqual(expected))
                throw new InvalidDataException("Owned temporary content changed during validation.");
        }
    }

    private static Node FindMarker(Node root, string markerName)
    {
        var marker = root.Children.SingleOrDefault(node =>
            StringComparer.Ordinal.Equals(node.Name, markerName) && !node.IsDirectory);
        return marker ?? throw new InvalidDataException("The ownership marker is missing or changed type.");
    }

    private static void VerifyMarker(Node marker, string markerToken)
    {
        var expected = System.Text.Encoding.ASCII.GetBytes(markerToken);
        if (!GetFileSizeEx(marker.Handle, out var length) || length != expected.Length)
            throw new InvalidDataException("Temporary directory ownership marker has an invalid length.");
        var actual = new byte[expected.Length];
        if (!SetFilePointerEx(marker.Handle, 0, out _, 0) ||
            !ReadFile(marker.Handle, actual, (uint)actual.Length, out var read, IntPtr.Zero) || read != actual.Length ||
            !actual.AsSpan().SequenceEqual(expected))
            throw new InvalidDataException("Temporary directory ownership marker does not match this process.");
    }

    private static void RequireIdentity(Node node, FileIdentity expected)
    {
        if (ReadIdentity(node.Handle) != expected)
            throw new InvalidDataException("Owned temporary content changed identity.");
    }

    private static FileIdentity ReadIdentity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandleEx(handle, FileIdInfo, out FileIdInformation information,
                (uint)Marshal.SizeOf<FileIdInformation>()))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return new FileIdentity(information.VolumeSerialNumber, information.FileId.Low, information.FileId.High);
    }

    private static void RequireConcreteType(Node node, bool expectDirectory)
    {
        if (!GetFileInformationByHandleEx(node.Handle, FileAttributeTagInfo, out FileAttributeTagInformation information,
                (uint)Marshal.SizeOf<FileAttributeTagInformation>()))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var isDirectory = (information.FileAttributes & FileAttributeDirectory) != 0;
        if ((information.FileAttributes & FileAttributeReparsePoint) != 0 || isDirectory != expectDirectory)
            throw new InvalidDataException("Temporary content changed type or became a reparse point.");
    }

    private static void DeleteByHandle(SafeFileHandle handle, string name)
    {
        var disposition = new FileDispositionInformationEx
        {
            Flags = FileDispositionDelete | FileDispositionPosixSemantics | FileDispositionIgnoreReadonlyAttribute
        };
        if (!SetFileInformationByHandle(handle, FileDispositionInfoEx, ref disposition,
                (uint)Marshal.SizeOf<FileDispositionInformationEx>()))
            throw new IOException($"Native handle deletion failed for '{name}'.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        handle.Dispose();
    }

    private static IEnumerable<Node> Flatten(Node root)
    {
        yield return root;
        foreach (var child in root.Children)
        foreach (var descendant in Flatten(child))
            yield return descendant;
    }

    private static IEnumerable<Node> PostOrder(Node root)
    {
        foreach (var child in root.Children)
        foreach (var descendant in PostOrder(child))
            yield return descendant;
        if (!string.IsNullOrEmpty(root.Name)) yield return root;
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrEmpty(name) || name is "." or ".." || name.Contains('\0') ||
            name.Contains(Path.DirectorySeparatorChar) || name.Contains(Path.AltDirectorySeparatorChar))
            throw new InvalidDataException("Directory enumeration returned an unsafe child name.");
    }

    private static bool IsNativeFailure(Exception exception) => exception is IOException or UnauthorizedAccessException or
        InvalidDataException or ArgumentException or Win32Exception or NotSupportedException or EntryPointNotFoundException or
        DllNotFoundException;

    private sealed class Node(string name, bool isDirectory, SafeFileHandle handle, FileIdentity identity)
    {
        public string Name { get; } = name;
        public bool IsDirectory { get; } = isDirectory;
        public SafeFileHandle Handle { get; } = handle;
        public FileIdentity Identity { get; set; } = identity;
        public List<Node> Children { get; } = [];
    }

    private readonly record struct DirectoryEntry(string Name, bool IsDirectory);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileId128 { public ulong Low; public ulong High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInformation { public ulong VolumeSerialNumber; public FileId128 FileId; }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInformation { public uint FileAttributes; public uint ReparseTag; }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInformationEx { public uint Flags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock { public IntPtr Status; public IntPtr Information; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass,
        out FileIdInformation information, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass,
        out FileAttributeTagInformation information, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass,
        ref FileDispositionInformationEx information, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileSizeEx(SafeFileHandle handle, out long size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFilePointerEx(SafeFileHandle handle, long distance, out long newPosition,
        uint moveMethod);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeFileHandle handle, [Out] byte[] buffer, uint bytesToRead,
        out int bytesRead, IntPtr overlapped);

    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(out SafeFileHandle handle, uint desiredAccess,
        ref ObjectAttributes objectAttributes, out IoStatusBlock ioStatusBlock, IntPtr allocationSize,
        uint fileAttributes, uint shareAccess, uint createDisposition, uint createOptions,
        IntPtr eaBuffer, uint eaLength);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryDirectoryFile(SafeFileHandle handle, IntPtr eventHandle, IntPtr apcRoutine,
        IntPtr apcContext, out IoStatusBlock ioStatusBlock, [Out] byte[] information, uint length,
        int fileInformationClass, byte returnSingleEntry, IntPtr fileName, byte restartScan);
}
