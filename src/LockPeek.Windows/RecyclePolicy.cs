using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LockPeek.Windows;

public sealed record RecycleTarget(string Path, long Length, DateTime LastWriteUtc, FileAttributes Attributes, uint Volume, ulong FileId);
public sealed record RecycleOutcome(string Path, bool Recycled, string Message, bool Deleted = false);

public static class RecyclePolicy
{
    public static RecycleTarget Inspect(string path, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();

        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            if (path.Length < 7 || !char.IsAsciiLetter(path[4]) || path[5] != ':' || path[6] != '\\')
            {
                throw new IOException("Only local drive paths can be deleted here.");
            }
            path = path[4..];
        }

        string full = Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
        if (!System.IO.Path.IsPathFullyQualified(path) || full.StartsWith(@"\\"))
        {
            throw new IOException("Only local files and folders can be recycled here.");
        }

        string root = System.IO.Path.GetPathRoot(full)!;
        if (full.Equals(System.IO.Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("A drive root cannot be recycled.");
        }

        if (new DriveInfo(root).DriveType != DriveType.Fixed)
        {
            throw new IOException("Use Explorer for removable or network drives; recycling cannot be guaranteed here.");
        }

        string[] protectedRoots = [
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            AppContext.BaseDirectory
        ];

        foreach (string special in protectedRoots.Where(p => p.Length > 0))
        {
            string normalized = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(special));
            if (Within(full, normalized) || Within(normalized, full))
            {
                throw new IOException("Windows, installed applications and their parent folders are excluded. Use Explorer to manage them.");
            }
        }

        var personalRoots = new[]
        {
            Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolder.DesktopDirectory,
            Environment.SpecialFolder.MyDocuments,
            Environment.SpecialFolder.MyPictures,
            Environment.SpecialFolder.MyMusic,
            Environment.SpecialFolder.MyVideos
        };

        foreach (var special in personalRoots)
        {
            if (full.Equals(Environment.GetFolderPath(special), StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("A personal root folder cannot be recycled here.");
            }
        }

        for (string? current = full; current is not null && current.Length > root.Length; current = System.IO.Path.GetDirectoryName(current))
        {
            var a = File.GetAttributes(current);
            if ((a & (FileAttributes.Offline | FileAttributes.System)) != 0)
            {
                throw new IOException("Offline placeholders and system items are excluded from recycling here.");
            }
            if ((a & FileAttributes.ReparsePoint) != 0 && new DirectoryInfo(current).LinkTarget is not null)
            {
                throw new IOException("Links and junctions are excluded from recycling here.");
            }
        }

        var attributes = File.GetAttributes(full);
        using var handle = CreateFile(
            full.StartsWith(@"\\?\") ? full : @"\\?\" + full,
            0x80,
            7,
            IntPtr.Zero,
            3,
            0x02200000,
            IntPtr.Zero);

        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var identity))
        {
            throw new IOException("The selected item's identity could not be verified.");
        }

        ulong fileId = ((ulong)identity.IndexHigh << 32) | identity.IndexLow;

        if ((attributes & FileAttributes.Directory) != 0)
        {
            var pending = new Stack<IEnumerator<FileSystemInfo>>();
            try
            {
                pending.Push(new DirectoryInfo(full).EnumerateFileSystemInfos().GetEnumerator());
                while (pending.Count > 0)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var current = pending.Peek();
                    if (!current.MoveNext())
                    {
                        pending.Pop().Dispose();
                        continue;
                    }

                    var child = current.Current;
                    var childAttributes = child.Attributes;

                    if ((childAttributes & (FileAttributes.Offline | FileAttributes.System)) != 0)
                    {
                        throw new IOException("This folder contains an offline placeholder or system item. Use Explorer to manage it.");
                    }
                    if ((childAttributes & FileAttributes.ReparsePoint) != 0 && child.LinkTarget is not null)
                    {
                        throw new IOException("This folder contains a link or junction. Use Explorer to manage it.");
                    }

                    if ((childAttributes & FileAttributes.Directory) != 0)
                    {
                        pending.Push(new DirectoryInfo(child.FullName).EnumerateFileSystemInfos().GetEnumerator());
                    }
                }
            }
            finally
            {
                while (pending.TryPop(out var entry)) entry.Dispose();
            }

            return new(full, 0, Directory.GetLastWriteTimeUtc(full), attributes, identity.Volume, fileId);
        }

        return new(full, new FileInfo(full).Length, File.GetLastWriteTimeUtc(full), attributes, identity.Volume, fileId);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdentity
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileIdentity information);

    public static bool Within(string path, string parent) =>
        path.Equals(parent, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(parent.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    public static RecycleTarget[] Prepare(IEnumerable<string> paths, CancellationToken cancellation = default)
    {
        string[] selected = paths.Select(path =>
        {
            if (!System.IO.Path.IsPathFullyQualified(path))
            {
                throw new IOException("An absolute path is required.");
            }
            return System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
        }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        var result = new List<RecycleTarget>();
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string path in selected.OrderBy(p => p.Length))
        {
            cancellation.ThrowIfCancellationRequested();
            string full = System.IO.Path.GetFullPath(path);
            bool included = false;
            for (string? parent = System.IO.Path.GetDirectoryName(full); parent is not null; parent = System.IO.Path.GetDirectoryName(parent))
            {
                if (folders.Contains(System.IO.Path.TrimEndingDirectorySeparator(parent)))
                {
                    included = true;
                    break;
                }
            }
            if (included) continue;

            var target = Inspect(path, cancellation);
            result.Add(target);
            if ((target.Attributes & FileAttributes.Directory) != 0)
            {
                folders.Add(target.Path);
            }
        }
        return result.ToArray();
    }
}
