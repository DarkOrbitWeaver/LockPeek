using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LockPeek.App;

public sealed record FileSelection(string[] Paths, int Unsupported = 0, bool RecycleIntent = false);

internal static class SelectionTransport
{
    public static string PipeName => "LockPeek-" + WindowsIdentity.GetCurrent().User!.Value;

    public static async Task<FileSelection> ReadLaunchAsync()
    {
        var args = Environment.GetCommandLineArgs();
        if (args.Length == 3 && args[1] == "--selection-pipe")
        {
            using var pipe = new AnonymousPipeClientStream(PipeDirection.In, args[2]);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            return await ReadAsync(pipe, timeout.Token);
        }

        var normalized = args.Skip(1)
            .Where(p => !p.StartsWith("--") && !string.IsNullOrWhiteSpace(p))
            .Select(p =>
            {
                try
                {
                    return Path.GetFullPath(p);
                }
                catch
                {
                    return p;
                }
            })
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToArray();

        return new(normalized);
    }

    public static async Task<FileSelection> ReadAsync(Stream stream, CancellationToken token)
    {
        using var buffer = new MemoryStream();
        byte[] block = new byte[8192];
        int read;

        while ((read = await stream.ReadAsync(block, token)) > 0)
        {
            if (buffer.Length + read > 16 * 1024 * 1024)
            {
                throw new IOException("Selection exceeds the 16 MB transfer limit");
            }
            buffer.Write(block, 0, read);
        }

        buffer.Position = 0;
        using var reader = new BinaryReader(buffer, Encoding.Unicode);
        uint version = reader.ReadUInt32();
        if (version != 0x314B504C && version != 0x324B504C)
        {
            throw new IOException("Unsupported selection transfer version");
        }

        uint count = reader.ReadUInt32();
        uint skipped = reader.ReadUInt32();
        if (count > 100000 || skipped > int.MaxValue)
        {
            throw new IOException("Selection exceeds supported transfer counts");
        }

        uint mode = version == 0x324B504C ? reader.ReadUInt32() : 0;
        if (mode > 1)
        {
            throw new IOException("Unsupported selection mode");
        }

        var paths = new string[count];
        for (int i = 0; i < count; i++)
        {
            uint chars = reader.ReadUInt32();
            if (chars == 0 || chars > 32767 || chars * 2 > buffer.Length - buffer.Position)
            {
                throw new IOException("Invalid selection path length");
            }

            paths[i] = Encoding.Unicode.GetString(reader.ReadBytes(checked((int)chars * 2)));
            if (paths[i].Contains('\0') || !Path.IsPathFullyQualified(paths[i]))
            {
                throw new IOException("Selection contains an invalid file path");
            }
        }

        if (buffer.Position != buffer.Length)
        {
            throw new IOException("Unexpected selection data");
        }

        return new(paths, (int)skipped, mode == 1);
    }

    public static async Task SendAsync(FileSelection selection)
    {
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(10000);
        LockPeek.Windows.WindowActivation.GrantToPipeServer(pipe.SafePipeHandle);

        using var writer = new BinaryWriter(pipe, Encoding.Unicode, true);
        writer.Write(0x324B504Cu);
        writer.Write((uint)selection.Paths.Length);
        writer.Write((uint)selection.Unsupported);
        writer.Write(selection.RecycleIntent ? 1u : 0u);

        foreach (var path in selection.Paths)
        {
            writer.Write((uint)path.Length);
            writer.Write(Encoding.Unicode.GetBytes(path));
        }

        writer.Flush();
    }
}
