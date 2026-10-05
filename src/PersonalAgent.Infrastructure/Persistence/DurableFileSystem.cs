using System.ComponentModel;
using Microsoft.Win32.SafeHandles;
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
            using var handle = CreateDirectoryHandle(path);
            if (!FlushFileBuffers(handle))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not flush the SQLite data directory.");
            }

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

    private static SafeFileHandle CreateDirectoryHandle(string path)
    {
        var handle = CreateFile(
            path,
            GenericRead | GenericWrite,
            ShareRead | ShareWrite | ShareDelete,
            IntPtr.Zero,
            OpenExisting,
            BackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not open the SQLite data directory for flushing.");
        }

        return handle;
    }

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint ShareRead = 0x00000001;
    private const uint ShareWrite = 0x00000002;
    private const uint ShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string path,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", EntryPoint = "FlushFileBuffers", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushFileBuffers(SafeFileHandle handle);

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
