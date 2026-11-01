using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]

namespace CryptoTrading.Exchange.Binance;

/// <summary>Linux-only descriptor-relative, durable journal for cooperative quota admission.</summary>
internal sealed class BinanceQuotaJournal : IAsyncDisposable
{
    private static readonly Lazy<string> Boot = new(() =>
    {
        if (!OperatingSystem.IsLinux()) throw Rejected("RATE_JOURNAL_PLATFORM_UNSUPPORTED");
        var value = File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim();
        return Guid.TryParseExact(value, "D", out _) ? value : throw Rejected("RATE_BOOT_ID_UNAVAILABLE");
    });
    internal static string BootIdentifier => Boot.Value;
    internal static string TimeNamespaceIdentifier
    {
        get
        {
            if (!OperatingSystem.IsLinux()) throw Rejected("RATE_JOURNAL_PLATFORM_UNSUPPORTED");
            var value = new FileInfo("/proc/self/ns/time").LinkTarget;
            if (value is null || !value.StartsWith("time:[", StringComparison.Ordinal) || value[^1] != ']')
                throw Rejected("RATE_TIME_NAMESPACE_UNAVAILABLE");
            return value;
        }
    }
    private const int ReadOnly = 0;
    private const int WriteOnly = 1;
    private const int ReadWrite = 2;
    private const int Create = 0x40;
    private const int Exclusive = 0x80;
    private const int NonBlock = 0x800;
    private const int DirectoryOnly = 0x10000;
    private const int NoFollow = 0x20000;
    private const int CloseOnExec = 0x80000;
    private const int FileFlags = NonBlock | NoFollow | CloseOnExec;
    private readonly SafeFileHandle directory;
    private readonly FileStream marker;
    private readonly string name;
    private readonly bool isNew;

    private BinanceQuotaJournal(SafeFileHandle directory, FileStream marker, string name, bool isNew)
    { this.directory = directory; this.marker = marker; this.name = name; this.isNew = isNew; }

    public static async Task<BinanceQuotaJournal> OpenAsync(string path, string name, TimeProvider clock, CancellationToken token)
    {
        if (!OperatingSystem.IsLinux()) throw Rejected("RATE_JOURNAL_PLATFORM_UNSUPPORTED");
        if (name.Length != 64 || !name.All(static c => char.IsAsciiHexDigit(c))) throw Rejected("RATE_JOURNAL_NAME_INVALID");
        var directory = OpenDirectory(path);
        FileStream? marker = null;
        try
        {
            marker = OpenStream(directory, name + ".lock", ReadWrite | Create, FileAccess.ReadWrite);
            for (var attempt = 0; ; attempt++)
            {
                token.ThrowIfCancellationRequested();
                if (QuotaNative.Flock(marker.SafeFileHandle, 2 | 4) == 0) break;
                if (Marshal.GetLastPInvokeError() != 11 || attempt >= 99) throw Rejected("RATE_JOURNAL_LOCK_UNAVAILABLE");
                await Task.Delay(TimeSpan.FromMilliseconds(5), clock, token).ConfigureAwait(false);
            }
            var isNew = marker.Length == 0;
            if (isNew)
            {
                // Establish the durable tombstone before any usable state can
                // be created. A later missing journal is never a fresh budget.
                await marker.WriteAsync(new byte[] { 1 }, token).ConfigureAwait(false);
                await marker.FlushAsync(token).ConfigureAwait(false);
                Sync(marker.SafeFileHandle);
                Sync(directory);
            }
            else ValidateMarker(marker);
            return new(directory, marker, name, isNew);
        }
        catch
        {
            if (marker is not null) await marker.DisposeAsync().ConfigureAwait(false);
            directory.Dispose();
            throw;
        }
    }

    public async Task<byte[]?> ReadAsync(int maximumBytes, CancellationToken token)
    {
        var raw = QuotaNative.OpenAt(directory, name + ".json", ReadOnly | FileFlags, 0);
        if (raw < 0)
        {
            if (Marshal.GetLastPInvokeError() == 2 && isNew) return null;
            throw Rejected("RATE_JOURNAL_MISSING_OR_UNSAFE");
        }
        using var handle = Handle(raw);
        var status = Inspect(handle);
        ValidateFile(status);
        if (status.Size is 0 || status.Size > (ulong)maximumBytes) throw Rejected("RATE_JOURNAL_SIZE_INVALID");
        var stream = new FileStream(handle, FileAccess.Read);
        await using var lifetime = stream.ConfigureAwait(false);
        var bytes = new byte[(int)status.Size];
        await stream.ReadExactlyAsync(bytes.AsMemory(), token).ConfigureAwait(false);
        return bytes;
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> payload, CancellationToken token)
    {
        var temporary = name + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var stream = OpenStream(directory, temporary, WriteOnly | Create | Exclusive, FileAccess.Write);
            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(payload, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
                Sync(stream.SafeFileHandle);
            }
            if (QuotaNative.RenameAt(directory, temporary, directory, name + ".json") != 0)
                throw Rejected("RATE_JOURNAL_RENAME_FAILED");
            Sync(directory);
            ValidateMarker(marker);
        }
        finally { _ = QuotaNative.UnlinkAt(directory, temporary, 0); }
    }

    public static async Task PoisonAsync(string path, string name)
    {
        using var directory = OpenDirectory(path);
        var stream = OpenStream(directory, name + ".lock", ReadWrite, FileAccess.ReadWrite);
        await using var lifetime = stream.ConfigureAwait(false);
        stream.Position = 0;
        await stream.WriteAsync(new byte[] { 2 }, CancellationToken.None).ConfigureAwait(false);
        await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        Sync(stream.SafeFileHandle);
        Sync(directory);
    }

    public async ValueTask DisposeAsync()
    {
        try { await marker.DisposeAsync().ConfigureAwait(false); }
        finally { directory.Dispose(); }
    }

    private static SafeFileHandle OpenDirectory(string path)
    {
        if (!OperatingSystem.IsLinux()) throw Rejected("RATE_JOURNAL_PLATFORM_UNSUPPORTED");
        var current = Handle(QuotaNative.Open("/", ReadOnly | DirectoryOnly | FileFlags, 0));
        try
        {
            var components = Path.GetFullPath(path).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < components.Length; i++)
            {
                var raw = QuotaNative.OpenAt(current, components[i], ReadOnly | DirectoryOnly | FileFlags, 0);
                if (raw < 0 && Marshal.GetLastPInvokeError() == 2)
                {
                    if (QuotaNative.MkdirAt(current, components[i], 0x1C0) != 0 && Marshal.GetLastPInvokeError() != 17)
                        throw Rejected("RATE_JOURNAL_DIRECTORY_CREATE_FAILED");
                    Sync(current);
                    raw = QuotaNative.OpenAt(current, components[i], ReadOnly | DirectoryOnly | FileFlags, 0);
                }
                var next = Handle(raw);
                try
                {
                    var status = Inspect(next);
                    var own = status.Uid == QuotaNative.GetEffectiveUid();
                    var privateTarget = i == components.Length - 1;
                    var stickyRoot = status.Uid == 0 && (status.Mode & 0x200) != 0;
                    if ((status.Mode & 0xF000) != 0x4000 || (!own && status.Uid != 0) ||
                        privateTarget && (!own || (status.Mode & 0xFFF) != 0x1C0) ||
                        !privateTarget && (status.Mode & 0x12) != 0 && !stickyRoot)
                        throw Rejected("RATE_JOURNAL_DIRECTORY_UNSAFE");
                }
                catch { next.Dispose(); throw; }
                current.Dispose();
                current = next;
            }
            if (components.Length == 0) throw Rejected("RATE_JOURNAL_ROOT_REJECTED");
            return current;
        }
        catch { current.Dispose(); throw; }
    }

    private static SafeFileHandle OpenFile(SafeFileHandle directory, string name, int flags)
    {
        var handle = Handle(QuotaNative.OpenAt(directory, name, flags | FileFlags, 0x180));
        try { ValidateFile(Inspect(handle)); return handle; }
        catch { handle.Dispose(); throw; }
    }

    private static void ValidateFile(QuotaNative.Statx status)
    {
        if ((status.Mode & 0xF000) != 0x8000 || (status.Mode & 0xFFF) != 0x180 || status.Links != 1 ||
            status.Uid != QuotaNative.GetEffectiveUid()) throw Rejected("RATE_JOURNAL_FILE_UNSAFE");
    }

    private static void ValidateMarker(FileStream marker)
    {
        if (marker.Length != 1) throw Rejected("RATE_JOURNAL_MARKER_LENGTH_INVALID");
        if (QuotaNative.ReadAt(marker.SafeFileHandle, out var value, 1, 0) != 1) throw Rejected("RATE_JOURNAL_MARKER_READ_FAILED");
        if (value != 1) throw Rejected("RATE_JOURNAL_POISONED");
    }

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "FileStream owns the supplied SafeFileHandle; construction failure explicitly disposes it.")]
    private static FileStream OpenStream(SafeFileHandle directory, string name, int flags, FileAccess access)
    {
        var handle = OpenFile(directory, name, flags);
        try { return new FileStream(handle, access, 1, isAsync: false); }
        catch { handle.Dispose(); throw; }
    }

    private static QuotaNative.Statx Inspect(SafeFileHandle handle)
    {
        if (QuotaNative.Stat(handle, string.Empty, 0x1000 | 0x100, 0x7FF, out var status) != 0 || (status.Mask & 0x20F) != 0x20F)
            throw Rejected("RATE_JOURNAL_STAT_UNAVAILABLE");
        return status;
    }

    private static SafeFileHandle Handle(int value) => value < 0 ? throw Rejected("RATE_JOURNAL_NOFOLLOW_OPEN_FAILED") : new((IntPtr)value, true);
    private static void Sync(SafeFileHandle handle)
    { if (QuotaNative.Sync(handle) != 0) throw Rejected("RATE_JOURNAL_SYNC_FAILED"); }
    private static BinanceRequestBudgetRejectedException Rejected(string code) => new(code);
}

internal static partial class QuotaNative
{
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    internal struct Statx
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(16)] public uint Links;
        [FieldOffset(20)] public uint Uid;
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(40)] public ulong Size;
    }
    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int Open(string path, int flags, int mode);
    [LibraryImport("libc", EntryPoint = "openat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int OpenAt(SafeFileHandle directory, string name, int flags, int mode);
    [LibraryImport("libc", EntryPoint = "mkdirat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int MkdirAt(SafeFileHandle directory, string name, int mode);
    [LibraryImport("libc", EntryPoint = "renameat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int RenameAt(SafeFileHandle oldDirectory, string oldName, SafeFileHandle newDirectory, string newName);
    [LibraryImport("libc", EntryPoint = "unlinkat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int UnlinkAt(SafeFileHandle directory, string name, int flags);
    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int Stat(SafeFileHandle descriptor, string name, int flags, uint mask, out Statx status);
    [LibraryImport("libc", EntryPoint = "flock", SetLastError = true)]
    internal static partial int Flock(SafeFileHandle handle, int operation);
    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    internal static partial int Sync(SafeFileHandle handle);
    [LibraryImport("libc", EntryPoint = "geteuid")]
    internal static partial uint GetEffectiveUid();
    [LibraryImport("libc", EntryPoint = "pread", SetLastError = true)]
    internal static partial nint ReadAt(SafeFileHandle handle, out byte destination, nuint length, long offset);
}
