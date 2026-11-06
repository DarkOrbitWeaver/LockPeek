using LockPeek.Core;

namespace LockPeek.Windows;

public sealed record ExpandedResources(string[] Paths, IReadOnlyList<ScanIssue> Issues, bool Cancelled);

public static class ResourceExpansion
{
    public static ExpandedResources Expand(IEnumerable<string> input, CancellationToken token = default)
    {
        var paths = new List<string>();
        var issues = new List<ScanIssue>();
        bool cancelled = Visit(input, paths.Add, issues.Add, token);
        return new(paths.ToArray(), issues, cancelled);
    }

    public static bool Visit(IEnumerable<string> input, Action<string> file, Action<ScanIssue> issue, CancellationToken token = default)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string path in input)
        {
            if (token.IsCancellationRequested) return true;
            try
            {
                if (!Path.IsPathFullyQualified(path))
                {
                    throw new IOException("An absolute path is required");
                }
                selected.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                issue(new(path, e.Message));
            }
        }

        foreach (string path in selected.OrderBy(p => p.Length))
        {
            if (token.IsCancellationRequested) return true;

            try
            {
                if (IsNetworkPath(path))
                {
                    issue(new(path, "Files on network shares cannot be monitored by local Windows Restart Manager.", true));
                }

                var attributes = File.GetAttributes(path);
                bool included = false;
                for (string? parent = Path.GetDirectoryName(path); parent is not null; parent = Path.GetDirectoryName(parent))
                {
                    if (roots.Contains(Path.TrimEndingDirectorySeparator(parent)))
                    {
                        included = true;
                        break;
                    }
                }
                if (included) continue;

                var itemInfo = (attributes & FileAttributes.Directory) != 0
                    ? (FileSystemInfo)new DirectoryInfo(path)
                    : new FileInfo(path);

                if ((attributes & FileAttributes.Offline) != 0)
                {
                    issue(new(path, "Cloud placeholder is offline; download the file to check it."));
                    continue;
                }

                if (IsLinkOrJunction(itemInfo, attributes))
                {
                    issue(new(path, "Link or shortcut skipped; select an available local file instead."));
                    continue;
                }

                if ((attributes & FileAttributes.Directory) == 0)
                {
                    file(path);
                    continue;
                }

                roots.Add(path);
                issue(new(path, "Apps using the folder itself may not appear, even when its files were checked.", true));
                Walk(path, file, issue, token);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                issue(new(path, e.Message));
            }
        }

        return token.IsCancellationRequested;
    }

    private static void Walk(string root, Action<string> file, Action<ScanIssue> issue, CancellationToken token)
    {
        var pending = new Stack<(string DirectoryPath, IEnumerator<FileSystemInfo> Enumerator)>();
        try
        {
            var dirInfo = new DirectoryInfo(root);
            pending.Push((root, dirInfo.EnumerateFileSystemInfos().GetEnumerator()));

            while (pending.Count > 0 && !token.IsCancellationRequested)
            {
                var (currentDir, enumerator) = pending.Peek();
                FileSystemInfo child;
                try
                {
                    if (!enumerator.MoveNext())
                    {
                        pending.Pop().Enumerator.Dispose();
                        continue;
                    }
                    child = enumerator.Current;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    issue(new(currentDir, "A folder could not be fully read: " + e.Message));
                    pending.Pop().Enumerator.Dispose();
                    continue;
                }

                try
                {
                    var attributes = child.Attributes;
                    string childPath = child.FullName;

                    if ((attributes & FileAttributes.Offline) != 0)
                    {
                        issue(new(childPath, "Cloud placeholder is offline; download the file to check it."));
                    }
                    else if (IsLinkOrJunction(child, attributes))
                    {
                        issue(new(childPath, "Link skipped."));
                    }
                    else if ((attributes & FileAttributes.Directory) != 0)
                    {
                        var subDir = new DirectoryInfo(childPath);
                        pending.Push((childPath, subDir.EnumerateFileSystemInfos().GetEnumerator()));
                    }
                    else
                    {
                        file(childPath);
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    issue(new(child.FullName, e.Message));
                }
            }
        }
        finally
        {
            while (pending.TryPop(out var entry)) entry.Enumerator.Dispose();
        }
    }

    private static bool IsLinkOrJunction(FileSystemInfo info, FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReparsePoint) == 0) return false;
        return info.LinkTarget is not null;
    }

    private static bool IsNetworkPath(string path)
    {
        if (path.StartsWith(@"\\") || path.StartsWith("//")) return true;
        try
        {
            string? root = Path.GetPathRoot(path);
            if (!string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Network)
            {
                return true;
            }
        }
        catch
        {
        }
        return false;
    }
}
