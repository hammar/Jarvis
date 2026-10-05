using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PersonalAgent.Infrastructure.Persistence;

internal static class DurableFileSystem
{
    internal static string ResolvePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows())
        {
            return fullPath;
        }

        var resolvedPointer = RealPath(fullPath, IntPtr.Zero);
        if (resolvedPointer != IntPtr.Zero)
        {
            try
            {
                return Marshal.PtrToStringUTF8(resolvedPointer)
                    ?? throw new IOException("The resolved SQLite path could not be decoded.");
            }
            finally
            {
                Free(resolvedPointer);
            }
        }

        var parent = Path.GetDirectoryName(fullPath);
        if (parent is null || string.Equals(parent, fullPath, StringComparison.Ordinal))
        {
            return fullPath;
        }

        return Path.Combine(ResolvePath(parent), Path.GetFileName(fullPath));
    }

    internal static void FlushDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            // Win32 does not support flushing directory handles. Critical name
            // transitions use MoveFileEx with MOVEFILE_WRITE_THROUGH instead.
            return;
        }

        var descriptor = OpenDirectory(path, 0);
        if (descriptor < 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not open the SQLite data directory for flushing.");
        }

        try
        {
            if (Fsync(descriptor) != 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not flush the SQLite data directory.");
            }
        }
        finally
        {
            Close(descriptor);
        }
    }

    internal static void MoveFileDurably(string sourcePath, string destinationPath, bool overwrite)
    {
        if (OperatingSystem.IsWindows())
        {
            var flags = MoveFileWriteThrough | (overwrite ? MoveFileReplaceExisting : 0);
            if (!MoveFileEx(sourcePath, destinationPath, flags))
            {
                throw new Win32Exception(
                    Marshal.GetLastPInvokeError(),
                    $"Could not durably move SQLite recovery file '{Path.GetFileName(sourcePath)}'.");
            }

            return;
        }

        File.Move(sourcePath, destinationPath, overwrite);
    }

    private const uint MoveFileReplaceExisting = 0x00000001;
    private const uint MoveFileWriteThrough = 0x00000008;

    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string existingFileName, string newFileName, uint flags);

    [DllImport("libc", EntryPoint = "open", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int OpenDirectory(string path, int flags);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int Fsync(int descriptor);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int descriptor);

    [DllImport("libc", EntryPoint = "realpath", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr RealPath(string path, IntPtr resolvedPath);

    [DllImport("libc", EntryPoint = "free")]
    private static extern void Free(IntPtr pointer);
}
