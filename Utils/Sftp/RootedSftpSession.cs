using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using FeatherQuilld.Plugins.Events;
using FeatherQuilld.Utils.IO;
using FxSsh.Services;

namespace FeatherQuilld.Utils.Sftp;

/// <summary>
/// In-process SFTP protocol version 3 subsystem rooted at a filesystem path.
/// Speaks over an <see cref="ISftpTransportChannel"/> (length-prefixed packets).
/// </summary>
public sealed class RootedSftpSession : IDisposable
{
    private const uint SftpVersion = 3;

    private const byte FxpInit = 1;
    private const byte FxpVersion = 2;
    private const byte FxpOpen = 3;
    private const byte FxpClose = 4;
    private const byte FxpRead = 5;
    private const byte FxpWrite = 6;
    private const byte FxpLstat = 7;
    private const byte FxpFstat = 8;
    private const byte FxpSetstat = 9;
    private const byte FxpFsetstat = 10;
    private const byte FxpOpendir = 11;
    private const byte FxpReaddir = 12;
    private const byte FxpRemove = 13;
    private const byte FxpMkdir = 14;
    private const byte FxpRmdir = 15;
    private const byte FxpRealpath = 16;
    private const byte FxpStat = 17;
    private const byte FxpRename = 18;
    private const byte FxpStatus = 101;
    private const byte FxpHandle = 102;
    private const byte FxpData = 103;
    private const byte FxpName = 104;
    private const byte FxpAttrs = 105;

    private const uint FxOk = 0;
    private const uint FxEof = 1;
    private const uint FxNoSuchFile = 2;
    private const uint FxPermissionDenied = 3;
    private const uint FxFailure = 4;
    private const uint FxBadMessage = 5;
    private const uint FxOpUnsupported = 8;

    private const uint AttrSize = 0x00000001;
    private const uint AttrUidGid = 0x00000002;
    private const uint AttrPermissions = 0x00000004;
    private const uint AttrAcModTime = 0x00000008;

    private const uint FxfRead = 0x00000001;
    private const uint FxfWrite = 0x00000002;
    private const uint FxfAppend = 0x00000004;
    private const uint FxfCreat = 0x00000008;
    private const uint FxfTrunc = 0x00000010;
    private const uint FxfExcl = 0x00000020;

    private readonly ISftpTransportChannel _channel;
    private readonly string _root;
    private readonly bool _readOnly;
    private readonly Guid _webSpaceUuid;
    private readonly string _username;
    private readonly IEventBus _events;
    private readonly object _ioLock = new();
    private readonly List<byte> _recv = new(16 * 1024);
    private readonly ConcurrentDictionary<string, OpenHandle> _handles = new();
    private int _handleSeq;
    private bool _disposed;

    public RootedSftpSession(SessionChannel channel, string rootPath, bool readOnly)
        : this(new FxSshTransportChannel(channel), rootPath, readOnly)
    {
    }

    public RootedSftpSession(
        SessionChannel channel,
        string rootPath,
        bool readOnly,
        Guid webSpaceUuid,
        string username,
        IEventBus? events)
        : this(new FxSshTransportChannel(channel), rootPath, readOnly, webSpaceUuid, username, events)
    {
    }

    public RootedSftpSession(ISftpTransportChannel channel, string rootPath, bool readOnly)
        : this(channel, rootPath, readOnly, Guid.Empty, "", null)
    {
    }

    public RootedSftpSession(
        ISftpTransportChannel channel,
        string rootPath,
        bool readOnly,
        Guid webSpaceUuid,
        string username,
        IEventBus? events)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        if (string.IsNullOrWhiteSpace(rootPath))
            throw new ArgumentException("Root path is required.", nameof(rootPath));

        Directory.CreateDirectory(rootPath);
        _root = RootedPath.CanonicalizeRoot(RootedPath.ResolveExisting(Path.GetFullPath(rootPath)));
        _readOnly = readOnly;
        _webSpaceUuid = webSpaceUuid;
        _username = username ?? "";
        _events = events.OrNoOp();

        Directory.CreateDirectory(_root);

        _channel.DataReceived += OnDataReceived;
        _channel.Closed += OnChannelClosed;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        try { _channel.DataReceived -= OnDataReceived; } catch { /* ignore */ }
        try { _channel.Closed -= OnChannelClosed; } catch { /* ignore */ }

        foreach (var h in _handles.Values)
            h.Dispose();
        _handles.Clear();

        if (_webSpaceUuid != Guid.Empty)
        {
            _ = _events.Emit(new SftpSessionCloseAfterEvent
            {
                WebSpaceUuid = _webSpaceUuid,
                Username = _username,
            });
        }
    }

    private bool EmitMutatingBefore<TBefore, TAfter>(
        TBefore before,
        Func<Exception?, TAfter> afterFactory,
        Action action)
        where TBefore : class
        where TAfter : class
    {
        try
        {
            _events.WithHooks(before, afterFactory, action);
            return true;
        }
        catch (PluginHookCancelledException)
        {
            return false;
        }
    }

    private void OnChannelClosed(object? sender, EventArgs e) => Dispose();

    private void OnDataReceived(object? sender, byte[] data)
    {
        if (_disposed || data is null || data.Length == 0)
            return;

        lock (_ioLock)
        {
            _recv.AddRange(data);
            while (TryTakePacket(out var type, out var payload))
            {
                try
                {
                    HandlePacket(type, payload);
                }
                catch
                {
                    if (payload.Length >= 4)
                    {
                        var id = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(0, 4));
                        SendStatus(id, FxFailure, "internal error");
                    }
                }
            }
        }
    }

    private bool TryTakePacket(out byte type, out byte[] payload)
    {
        type = 0;
        payload = [];
        if (_recv.Count < 5)
            return false;

        var len = BinaryPrimitives.ReadUInt32BigEndian(CollectionsMarshalSpan(_recv, 0, 4));
        if (len < 1 || len > 16 * 1024 * 1024)
        {
            _recv.Clear();
            return false;
        }

        var total = 4 + (int)len;
        if (_recv.Count < total)
            return false;

        type = _recv[4];
        payload = _recv.GetRange(5, (int)len - 1).ToArray();
        _recv.RemoveRange(0, total);
        return true;
    }

    private static Span<byte> CollectionsMarshalSpan(List<byte> list, int offset, int count)
    {
        var tmp = new byte[count];
        list.CopyTo(offset, tmp, 0, count);
        return tmp;
    }

    private void HandlePacket(byte type, byte[] payload)
    {
        switch (type)
        {
            case FxpInit:
                SendVersion();
                return;
            case FxpRealpath:
                HandleRealpath(payload);
                return;
            case FxpStat:
            case FxpLstat:
                HandleStat(payload, followLinks: type == FxpStat);
                return;
            case FxpFstat:
                HandleFstat(payload);
                return;
            case FxpOpendir:
                HandleOpendir(payload);
                return;
            case FxpReaddir:
                HandleReaddir(payload);
                return;
            case FxpClose:
                HandleClose(payload);
                return;
            case FxpOpen:
                HandleOpen(payload);
                return;
            case FxpRead:
                HandleRead(payload);
                return;
            case FxpWrite:
                HandleWrite(payload);
                return;
            case FxpMkdir:
                HandleMkdir(payload);
                return;
            case FxpRmdir:
                HandleRmdir(payload);
                return;
            case FxpRemove:
                HandleRemove(payload);
                return;
            case FxpRename:
                HandleRename(payload);
                return;
            case FxpSetstat:
                HandleSetstat(payload);
                return;
            case FxpFsetstat:
                HandleFsetstat(payload);
                return;
            default:
                if (payload.Length >= 4)
                {
                    var id = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(0, 4));
                    SendStatus(id, FxOpUnsupported, "unsupported");
                }
                return;
        }
    }

    private void HandleRealpath(byte[] payload)
    {
        var r = new PacketReader(payload);
        var id = r.ReadUInt32();
        var path = r.ReadString();
        if (!TryMapPath(path, out var full, out var virt))
        {
            SendStatus(id, FxNoSuchFile, "invalid path");
            return;
        }

        SendName(id, [(virt, AttrsFromPath(full), LongName(virt, full))]);
    }

    private void HandleStat(byte[] payload, bool followLinks)
    {
        var r = new PacketReader(payload);
        var id = r.ReadUInt32();
        var path = r.ReadString();
        if (!TryMapPath(path, out var full, out _))
        {
            SendStatus(id, FxNoSuchFile, "invalid path");
            return;
        }

        if (!Exists(full, followLinks))
        {
            SendStatus(id, FxNoSuchFile, "no such file");
            return;
        }

        SendAttrs(id, AttrsFromPath(full, followLinks));
    }

    private void HandleFstat(byte[] payload)
    {
        var r = new PacketReader(payload);
        var id = r.ReadUInt32();
        var handle = r.ReadString();
        if (!_handles.TryGetValue(handle, out var h))
        {
            SendStatus(id, FxFailure, "invalid handle");
            return;
        }

        SendAttrs(id, AttrsFromPath(h.Path));
    }

    private void HandleOpendir(byte[] payload)
    {
        var r = new PacketReader(payload);
        var id = r.ReadUInt32();
        var path = r.ReadString();
        if (!TryMapPath(path, out var full, out _))
        {
            SendStatus(id, FxNoSuchFile, "invalid path");
            return;
        }

        if (!Directory.Exists(full))
        {
            SendStatus(id, FxNoSuchFile, "not a directory");
            return;
        }

        string[] entries;
        try
        {
            var children = Directory.GetFileSystemEntries(full);
            entries = new string[children.Length + 2];
            entries[0] = full; // "."
            entries[1] = Directory.GetParent(full)?.FullName ?? full; // ".."
            Array.Copy(children, 0, entries, 2, children.Length);
        }
        catch (UnauthorizedAccessException)
        {
            SendStatus(id, FxPermissionDenied, "permission denied");
            return;
        }
        catch
        {
            SendStatus(id, FxFailure, "readdir failed");
            return;
        }

        var handle = NextHandle();
        _handles[handle] = OpenHandle.ForDirectory(full, entries);
        SendHandle(id, handle);
    }

    private void HandleReaddir(byte[] payload)
    {
        var r = new PacketReader(payload);
        var id = r.ReadUInt32();
        var handle = r.ReadString();
        if (!_handles.TryGetValue(handle, out var h) || h.Kind != HandleKind.Directory)
        {
            SendStatus(id, FxFailure, "invalid handle");
            return;
        }

        if (h.DirIndex >= h.DirEntries!.Length)
        {
            SendStatus(id, FxEof, "EOF");
            return;
        }

        const int batch = 64;
        var names = new List<(string Name, FileAttrs Attrs, string LongName)>();
        while (h.DirIndex < h.DirEntries.Length && names.Count < batch)
        {
            var idx = h.DirIndex++;
            var entryPath = h.DirEntries[idx];
            string name;
            if (idx == 0)
                name = ".";
            else if (idx == 1)
                name = "..";
            else
                name = Path.GetFileName(entryPath);
            if (string.IsNullOrEmpty(name))
                continue;

            // Keep ".." from escaping the virtual root in listings.
            if (name == ".." && !RootedPath.IsUnderRoot(_root, entryPath) && !string.Equals(entryPath, _root, StringComparison.Ordinal))
                entryPath = h.Path;

            names.Add((name, AttrsFromPath(entryPath), LongName(name, entryPath)));
        }

        if (names.Count == 0)
        {
            SendStatus(id, FxEof, "EOF");
            return;
        }

        SendName(id, names);
    }

    private void HandleClose(byte[] payload)
    {
        var r = new PacketReader(payload);
        var id = r.ReadUInt32();
        var handle = r.ReadString();
        if (_handles.TryRemove(handle, out var h))
        {
            h.Dispose();
            SendStatus(id, FxOk, "OK");
        }
        else
        {
            SendStatus(id, FxFailure, "invalid handle");
        }
    }

    private void HandleOpen(byte[] payload)
    {
        var r = new PacketReader(payload);
        var id = r.ReadUInt32();
        var path = r.ReadString();
        var pflags = r.ReadUInt32();
        _ = r.ReadAttrs(); // desired attrs — ignored on open

        var wantsWrite = (pflags & (FxfWrite | FxfAppend | FxfCreat | FxfTrunc | FxfExcl)) != 0;
        if (_readOnly && wantsWrite)
        {
            SendStatus(id, FxPermissionDenied, "read-only");
            return;
        }

        if (!TryMapPath(path, out var full, out _))
        {
            SendStatus(id, FxNoSuchFile, "invalid path");
            return;
        }

        try
        {
            FileMode mode;
            FileAccess access;

            var read = (pflags & FxfRead) != 0;
            var write = (pflags & FxfWrite) != 0 || (pflags & FxfAppend) != 0;
            if (read && write)
                access = FileAccess.ReadWrite;
            else if (write)
                access = FileAccess.Write;
            else
                access = FileAccess.Read;

            if ((pflags & FxfCreat) != 0)
            {
                if ((pflags & FxfExcl) != 0)
                    mode = FileMode.CreateNew;
                else if ((pflags & FxfTrunc) != 0)
                    mode = FileMode.Create;
                else
                    mode = FileMode.OpenOrCreate;
            }
            else if ((pflags & FxfTrunc) != 0)
            {
                mode = FileMode.Truncate;
            }
            else
            {
                mode = FileMode.Open;
            }

            var stream = new FileStream(full, mode, access, FileShare.ReadWrite);
            if ((pflags & FxfAppend) != 0)
                stream.Seek(0, SeekOrigin.End);

            var handle = NextHandle();
            _handles[handle] = OpenHandle.ForFile(full, stream);
            SendHandle(id, handle);
        }
        catch (FileNotFoundException)
        {
            SendStatus(id, FxNoSuchFile, "no such file");
        }
        catch (DirectoryNotFoundException)
        {
            SendStatus(id, FxNoSuchFile, "no such file");
        }
        catch (IOException ex) when (ex.GetType().Name.Contains("IOException", StringComparison.Ordinal)
                                     && (pflags & FxfExcl) != 0)
        {
            SendStatus(id, FxFailure, "file exists");
        }
        catch (UnauthorizedAccessException)
        {
            SendStatus(id, FxPermissionDenied, "permission denied");
        }
        catch
        {
            SendStatus(id, FxFailure, "open failed");
        }
    }

    private void HandleRead(byte[] payload)
    {
        var r = new PacketReader(payload);
        var id = r.ReadUInt32();
        var handle = r.ReadString();
        var offset = r.ReadUInt64();
        var len = r.ReadUInt32();

        if (!_handles.TryGetValue(handle, out var h) || h.Stream is null)
        {
            SendStatus(id, FxFailure, "invalid handle");
            return;
        }

        try
        {
            if (offset >= (ulong)h.Stream.Length)
            {
                SendStatus(id, FxEof, "EOF");
                return;
            }

            var toRead = (int)Math.Min(len, 256 * 1024);
            var buf = new byte[toRead];
            h.Stream.Seek((long)offset, SeekOrigin.Begin);
            var n = h.Stream.Read(buf, 0, toRead);
            if (n <= 0)
            {
                SendStatus(id, FxEof, "EOF");
                return;
            }

            if (n < buf.Length)
                Array.Resize(ref buf, n);
            SendData(id, buf);
        }
        catch
        {
            SendStatus(id, FxFailure, "read failed");
        }
    }

    private void HandleWrite(byte[] payload)
    {
        var r = new PacketReader(payload);
        var id = r.ReadUInt32();
        if (_readOnly)
        {
            SendStatus(id, FxPermissionDenied, "read-only");
            return;
        }

        var handle = r.ReadString();
        var offset = r.ReadUInt64();
        var data = r.ReadBytes();

        if (!_handles.TryGetValue(handle, out var h) || h.Stream is null)
        {
            SendStatus(id, FxFailure, "invalid handle");
            return;
        }

        var virt = RootedPath.ToVirtual(_root, h.Path);
        try
        {
            if (!EmitMutatingBefore(
                    new SftpWriteBeforeEvent { WebSpaceUuid = _webSpaceUuid, Path = virt },
                    err => new SftpWriteAfterEvent { WebSpaceUuid = _webSpaceUuid, Path = virt, Error = err },
                    () =>
                    {
                        h.Stream.Seek((long)offset, SeekOrigin.Begin);
                        h.Stream.Write(data, 0, data.Length);
                        h.Stream.Flush();
                    }))
            {
                SendStatus(id, FxPermissionDenied, "cancelled by plugin");
                return;
            }

            SendStatus(id, FxOk, "OK");
        }
        catch
        {
            SendStatus(id, FxFailure, "write failed");
        }
    }

    private void HandleMkdir(byte[] payload)
    {
        var r = new PacketReader(payload);
        var id = r.ReadUInt32();
        if (_readOnly)
        {
            SendStatus(id, FxPermissionDenied, "read-only");
            return;
        }

        var path = r.ReadString();
        _ = r.ReadAttrs();
        if (!TryMapPath(path, out var full, out var virt))
        {
            SendStatus(id, FxNoSuchFile, "invalid path");
            return;
        }

        try
        {
            if (!EmitMutatingBefore(
                    new SftpMkdirBeforeEvent { WebSpaceUuid = _webSpaceUuid, Path = virt },
                    err => new SftpMkdirAfterEvent { WebSpaceUuid = _webSpaceUuid, Path = virt, Error = err },
                    () =>
                    {
                        if (Directory.Exists(full))
                            throw new IOException("already exists");
                        Directory.CreateDirectory(full);
                    }))
            {
                SendStatus(id, FxPermissionDenied, "cancelled by plugin");
                return;
            }

            SendStatus(id, FxOk, "OK");
        }
        catch (IOException)
        {
            SendStatus(id, FxFailure, "already exists");
        }
        catch (UnauthorizedAccessException)
        {
            SendStatus(id, FxPermissionDenied, "permission denied");
        }
        catch
        {
            SendStatus(id, FxFailure, "mkdir failed");
        }
    }

    private void HandleRmdir(byte[] payload)
    {
        var r = new PacketReader(payload);
        var id = r.ReadUInt32();
        if (_readOnly)
        {
            SendStatus(id, FxPermissionDenied, "read-only");
            return;
        }

        var path = r.ReadString();
        if (!TryMapPath(path, out var full, out var virt))
        {
            SendStatus(id, FxNoSuchFile, "invalid path");
            return;
        }

        try
        {
            if (!Directory.Exists(full))
            {
                SendStatus(id, FxNoSuchFile, "no such file");
                return;
            }

            if (!EmitMutatingBefore(
                    new SftpRmdirBeforeEvent { WebSpaceUuid = _webSpaceUuid, Path = virt },
                    err => new SftpRmdirAfterEvent { WebSpaceUuid = _webSpaceUuid, Path = virt, Error = err },
                    () => Directory.Delete(full, recursive: false)))
            {
                SendStatus(id, FxPermissionDenied, "cancelled by plugin");
                return;
            }

            SendStatus(id, FxOk, "OK");
        }
        catch (IOException)
        {
            SendStatus(id, FxFailure, "directory not empty");
        }
        catch (UnauthorizedAccessException)
        {
            SendStatus(id, FxPermissionDenied, "permission denied");
        }
        catch
        {
            SendStatus(id, FxFailure, "rmdir failed");
        }
    }

    private void HandleRemove(byte[] payload)
    {
        var r = new PacketReader(payload);
        var id = r.ReadUInt32();
        if (_readOnly)
        {
            SendStatus(id, FxPermissionDenied, "read-only");
            return;
        }

        var path = r.ReadString();
        if (!TryMapPath(path, out var full, out var virt))
        {
            SendStatus(id, FxNoSuchFile, "invalid path");
            return;
        }

        try
        {
            if (!File.Exists(full))
            {
                SendStatus(id, FxNoSuchFile, "no such file");
                return;
            }

            if (!EmitMutatingBefore(
                    new SftpRemoveBeforeEvent { WebSpaceUuid = _webSpaceUuid, Path = virt },
                    err => new SftpRemoveAfterEvent { WebSpaceUuid = _webSpaceUuid, Path = virt, Error = err },
                    () => File.Delete(full)))
            {
                SendStatus(id, FxPermissionDenied, "cancelled by plugin");
                return;
            }

            SendStatus(id, FxOk, "OK");
        }
        catch (UnauthorizedAccessException)
        {
            SendStatus(id, FxPermissionDenied, "permission denied");
        }
        catch
        {
            SendStatus(id, FxFailure, "remove failed");
        }
    }

    private void HandleRename(byte[] payload)
    {
        var r = new PacketReader(payload);
        var id = r.ReadUInt32();
        if (_readOnly)
        {
            SendStatus(id, FxPermissionDenied, "read-only");
            return;
        }

        var oldPath = r.ReadString();
        var newPath = r.ReadString();
        if (!TryMapPath(oldPath, out var oldFull, out var oldVirt) || !TryMapPath(newPath, out var newFull, out var newVirt))
        {
            SendStatus(id, FxNoSuchFile, "invalid path");
            return;
        }

        try
        {
            if (!File.Exists(oldFull) && !Directory.Exists(oldFull))
            {
                SendStatus(id, FxNoSuchFile, "no such file");
                return;
            }

            if (!EmitMutatingBefore(
                    new SftpRenameBeforeEvent { WebSpaceUuid = _webSpaceUuid, From = oldVirt, To = newVirt },
                    err => new SftpRenameAfterEvent
                    {
                        WebSpaceUuid = _webSpaceUuid,
                        From = oldVirt,
                        To = newVirt,
                        Error = err,
                    },
                    () =>
                    {
                        if (File.Exists(oldFull))
                            File.Move(oldFull, newFull);
                        else
                            Directory.Move(oldFull, newFull);
                    }))
            {
                SendStatus(id, FxPermissionDenied, "cancelled by plugin");
                return;
            }

            SendStatus(id, FxOk, "OK");
        }
        catch (UnauthorizedAccessException)
        {
            SendStatus(id, FxPermissionDenied, "permission denied");
        }
        catch
        {
            SendStatus(id, FxFailure, "rename failed");
        }
    }

    private void HandleSetstat(byte[] payload)
    {
        var r = new PacketReader(payload);
        var id = r.ReadUInt32();
        if (_readOnly)
        {
            SendStatus(id, FxPermissionDenied, "read-only");
            return;
        }

        var path = r.ReadString();
        var attrs = r.ReadAttrs();
        if (!TryMapPath(path, out var full, out var virt))
        {
            SendStatus(id, FxNoSuchFile, "invalid path");
            return;
        }

        if (!EmitMutatingBefore(
                new SftpSetstatBeforeEvent { WebSpaceUuid = _webSpaceUuid, Path = virt },
                err => new SftpSetstatAfterEvent { WebSpaceUuid = _webSpaceUuid, Path = virt, Error = err },
                () => ApplyAttrsPartial(full, attrs)))
        {
            SendStatus(id, FxPermissionDenied, "cancelled by plugin");
            return;
        }

        SendStatus(id, FxOk, "OK");
    }

    private void HandleFsetstat(byte[] payload)
    {
        var r = new PacketReader(payload);
        var id = r.ReadUInt32();
        if (_readOnly)
        {
            SendStatus(id, FxPermissionDenied, "read-only");
            return;
        }

        var handle = r.ReadString();
        var attrs = r.ReadAttrs();
        if (!_handles.TryGetValue(handle, out var h))
        {
            SendStatus(id, FxFailure, "invalid handle");
            return;
        }

        var virt = RootedPath.ToVirtual(_root, h.Path);
        if (!EmitMutatingBefore(
                new SftpSetstatBeforeEvent { WebSpaceUuid = _webSpaceUuid, Path = virt },
                err => new SftpSetstatAfterEvent { WebSpaceUuid = _webSpaceUuid, Path = virt, Error = err },
                () => ApplyAttrsPartial(h.Path, attrs)))
        {
            SendStatus(id, FxPermissionDenied, "cancelled by plugin");
            return;
        }

        SendStatus(id, FxOk, "OK");
    }

    private static void ApplyAttrsPartial(string path, FileAttrs attrs)
    {
        try
        {
            if ((attrs.Flags & AttrSize) != 0 && File.Exists(path))
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                fs.SetLength((long)attrs.Size);
            }

            if ((attrs.Flags & AttrAcModTime) != 0)
            {
                var mtime = DateTimeOffset.FromUnixTimeSeconds(attrs.Mtime).UtcDateTime;
                var atime = DateTimeOffset.FromUnixTimeSeconds(attrs.Atime).UtcDateTime;
                if (File.Exists(path))
                {
                    File.SetLastWriteTimeUtc(path, mtime);
                    File.SetLastAccessTimeUtc(path, atime);
                }
                else if (Directory.Exists(path))
                {
                    Directory.SetLastWriteTimeUtc(path, mtime);
                    Directory.SetLastAccessTimeUtc(path, atime);
                }
            }

            // Permissions / uid/gid: best-effort no-op on platforms without chmod — still OK (partial).
            if ((attrs.Flags & AttrPermissions) != 0 && OperatingSystem.IsLinux())
            {
                try
                {
                    File.SetUnixFileMode(path, (UnixFileMode)(attrs.Permissions & 0xFFF));
                }
                catch
                {
                    /* partial OK */
                }
            }
        }
        catch
        {
            /* partial OK */
        }
    }

    private bool TryMapPath(string requestPath, out string fullPath, out string virtualPath)
    {
        fullPath = _root;
        virtualPath = "/";

        try
        {
            var allowMissing = true;
            fullPath = RootedPath.Resolve(
                _root,
                requestPath,
                allowMissing: allowMissing,
                followExistingLinks: true);
            virtualPath = RootedPath.ToVirtual(_root, fullPath);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool Exists(string path, bool followLinks)
    {
        if (followLinks)
            return File.Exists(path) || Directory.Exists(path);
        return File.Exists(path) || Directory.Exists(path) || IsSymlink(path);
    }

    private static bool IsSymlink(string path)
    {
        try
        {
            if (File.Exists(path))
                return new FileInfo(path).LinkTarget != null;
            if (Directory.Exists(path))
                return new DirectoryInfo(path).LinkTarget != null;
            // dangling symlink: File.Exists is false but GetFileAttributes may still see ReparsePoint
            var attrs = File.GetAttributes(path);
            return attrs.HasFlag(FileAttributes.ReparsePoint);
        }
        catch
        {
            return false;
        }
    }

    private static FileAttrs AttrsFromPath(string path, bool followLinks = true)
    {
        try
        {
            if (Directory.Exists(path))
            {
                var di = new DirectoryInfo(path);
                return new FileAttrs
                {
                    Flags = AttrSize | AttrUidGid | AttrPermissions | AttrAcModTime,
                    Size = 0,
                    Uid = 0,
                    Gid = 0,
                    Permissions = 0x4000 | 0x1ED, // S_IFDIR | 0755
                    Atime = (uint)new DateTimeOffset(di.LastAccessTimeUtc).ToUnixTimeSeconds(),
                    Mtime = (uint)new DateTimeOffset(di.LastWriteTimeUtc).ToUnixTimeSeconds(),
                };
            }

            if (File.Exists(path) || (!followLinks && IsSymlink(path)))
            {
                var fi = new FileInfo(path);
                var isLink = fi.LinkTarget != null;
                uint mode = isLink && !followLinks
                    ? 0xA000u | 0x1FFu // S_IFLNK | 0777
                    : 0x8000u | 0x1A4u; // S_IFREG | 0644
                return new FileAttrs
                {
                    Flags = AttrSize | AttrUidGid | AttrPermissions | AttrAcModTime,
                    Size = fi.Exists ? (ulong)Math.Max(0, fi.Length) : 0,
                    Uid = 0,
                    Gid = 0,
                    Permissions = mode,
                    Atime = (uint)new DateTimeOffset(fi.LastAccessTimeUtc).ToUnixTimeSeconds(),
                    Mtime = (uint)new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds(),
                };
            }
        }
        catch
        {
            /* fall through */
        }

        return new FileAttrs { Flags = 0 };
    }

    private static string LongName(string name, string fullPath)
    {
        var attrs = AttrsFromPath(fullPath);
        var isDir = (attrs.Permissions & 0x4000) != 0;
        var isLink = (attrs.Permissions & 0xA000) == 0xA000;
        var type = isDir ? 'd' : isLink ? 'l' : '-';
        var mode = attrs.Permissions & 0x1FF;
        string Perm(int shift) =>
            ((((mode >> shift) & 4) != 0) ? "r" : "-")
            + ((((mode >> shift) & 2) != 0) ? "w" : "-")
            + ((((mode >> shift) & 1) != 0) ? "x" : "-");
        var perms = $"{type}{Perm(6)}{Perm(3)}{Perm(0)}";
        var mtime = DateTimeOffset.FromUnixTimeSeconds(attrs.Mtime).UtcDateTime.ToString("MMM dd HH:mm");
        return $"{perms} 1 owner group {attrs.Size,8} {mtime} {name}";
    }

    private string NextHandle()
    {
        var n = Interlocked.Increment(ref _handleSeq);
        return $"h{n}";
    }

    private void SendVersion()
    {
        var w = new PacketWriter();
        w.WriteByte(FxpVersion);
        w.WriteUInt32(SftpVersion);
        SendPacket(w.ToArray());
    }

    private void SendStatus(uint id, uint code, string message)
    {
        var w = new PacketWriter();
        w.WriteByte(FxpStatus);
        w.WriteUInt32(id);
        w.WriteUInt32(code);
        w.WriteString(message);
        w.WriteString("en");
        SendPacket(w.ToArray());
    }

    private void SendHandle(uint id, string handle)
    {
        var w = new PacketWriter();
        w.WriteByte(FxpHandle);
        w.WriteUInt32(id);
        w.WriteString(handle);
        SendPacket(w.ToArray());
    }

    private void SendData(uint id, byte[] data)
    {
        var w = new PacketWriter();
        w.WriteByte(FxpData);
        w.WriteUInt32(id);
        w.WriteBytes(data);
        SendPacket(w.ToArray());
    }

    private void SendAttrs(uint id, FileAttrs attrs)
    {
        var w = new PacketWriter();
        w.WriteByte(FxpAttrs);
        w.WriteUInt32(id);
        w.WriteAttrs(attrs);
        SendPacket(w.ToArray());
    }

    private void SendName(uint id, IReadOnlyList<(string Name, FileAttrs Attrs, string LongName)> names)
    {
        var w = new PacketWriter();
        w.WriteByte(FxpName);
        w.WriteUInt32(id);
        w.WriteUInt32((uint)names.Count);
        foreach (var n in names)
        {
            w.WriteString(n.Name);
            w.WriteString(n.LongName);
            w.WriteAttrs(n.Attrs);
        }

        SendPacket(w.ToArray());
    }

    private void SendPacket(byte[] typeAndPayload)
    {
        if (_disposed)
            return;
        var packet = new byte[4 + typeAndPayload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(0, 4), (uint)typeAndPayload.Length);
        Buffer.BlockCopy(typeAndPayload, 0, packet, 4, typeAndPayload.Length);
        try
        {
            _channel.SendData(packet);
        }
        catch
        {
            Dispose();
        }
    }

    private enum HandleKind { File, Directory }

    private sealed class OpenHandle : IDisposable
    {
        public HandleKind Kind { get; private init; }
        public string Path { get; private init; } = "";
        public FileStream? Stream { get; private init; }
        public string[]? DirEntries { get; private init; }
        public int DirIndex;

        public static OpenHandle ForFile(string path, FileStream stream) => new()
        {
            Kind = HandleKind.File,
            Path = path,
            Stream = stream,
        };

        public static OpenHandle ForDirectory(string path, string[] entries) => new()
        {
            Kind = HandleKind.Directory,
            Path = path,
            DirEntries = entries,
            DirIndex = 0,
        };

        public void Dispose()
        {
            try { Stream?.Dispose(); } catch { /* ignore */ }
        }
    }

    private struct FileAttrs
    {
        public uint Flags;
        public ulong Size;
        public uint Uid;
        public uint Gid;
        public uint Permissions;
        public uint Atime;
        public uint Mtime;
    }

    private sealed class PacketReader
    {
        private readonly byte[] _buf;
        private int _pos;

        public PacketReader(byte[] buf)
        {
            _buf = buf;
            _pos = 0;
        }

        public uint ReadUInt32()
        {
            Ensure(4);
            var v = BinaryPrimitives.ReadUInt32BigEndian(_buf.AsSpan(_pos, 4));
            _pos += 4;
            return v;
        }

        public ulong ReadUInt64()
        {
            Ensure(8);
            var v = BinaryPrimitives.ReadUInt64BigEndian(_buf.AsSpan(_pos, 8));
            _pos += 8;
            return v;
        }

        public string ReadString()
        {
            var data = ReadBytes();
            return Encoding.UTF8.GetString(data);
        }

        public byte[] ReadBytes()
        {
            var len = (int)ReadUInt32();
            if (len < 0 || len > _buf.Length - _pos)
                throw new InvalidOperationException("bad string length");
            var data = new byte[len];
            Buffer.BlockCopy(_buf, _pos, data, 0, len);
            _pos += len;
            return data;
        }

        public FileAttrs ReadAttrs()
        {
            var a = new FileAttrs { Flags = ReadUInt32() };
            if ((a.Flags & AttrSize) != 0)
                a.Size = ReadUInt64();
            if ((a.Flags & AttrUidGid) != 0)
            {
                a.Uid = ReadUInt32();
                a.Gid = ReadUInt32();
            }

            if ((a.Flags & AttrPermissions) != 0)
                a.Permissions = ReadUInt32();
            if ((a.Flags & AttrAcModTime) != 0)
            {
                a.Atime = ReadUInt32();
                a.Mtime = ReadUInt32();
            }

            // Skip extended attrs if present (flag 0x80000000).
            if ((a.Flags & 0x80000000) != 0)
            {
                var count = ReadUInt32();
                for (uint i = 0; i < count; i++)
                {
                    _ = ReadString();
                    _ = ReadString();
                }
            }

            return a;
        }

        private void Ensure(int n)
        {
            if (_pos + n > _buf.Length)
                throw new InvalidOperationException("truncated packet");
        }
    }

    private sealed class PacketWriter
    {
        private readonly MemoryStream _ms = new();

        public void WriteByte(byte v) => _ms.WriteByte(v);

        public void WriteUInt32(uint v)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(b, v);
            _ms.Write(b);
        }

        public void WriteUInt64(ulong v)
        {
            Span<byte> b = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(b, v);
            _ms.Write(b);
        }

        public void WriteString(string s) => WriteBytes(Encoding.UTF8.GetBytes(s ?? ""));

        public void WriteBytes(byte[] data)
        {
            WriteUInt32((uint)data.Length);
            _ms.Write(data, 0, data.Length);
        }

        public void WriteAttrs(FileAttrs a)
        {
            WriteUInt32(a.Flags);
            if ((a.Flags & AttrSize) != 0)
                WriteUInt64(a.Size);
            if ((a.Flags & AttrUidGid) != 0)
            {
                WriteUInt32(a.Uid);
                WriteUInt32(a.Gid);
            }

            if ((a.Flags & AttrPermissions) != 0)
                WriteUInt32(a.Permissions);
            if ((a.Flags & AttrAcModTime) != 0)
            {
                WriteUInt32(a.Atime);
                WriteUInt32(a.Mtime);
            }
        }

        public byte[] ToArray() => _ms.ToArray();
    }
}
