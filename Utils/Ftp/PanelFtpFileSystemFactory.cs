using FubarDev.FtpServer;
using FubarDev.FtpServer.BackgroundTransfer;
using FubarDev.FtpServer.FileSystem;
using FubarDev.FtpServer.FileSystem.DotNet;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FeatherQuilld.Utils.Ftp;

internal sealed class PanelFtpFileSystemFactory : IFileSystemClassFactory
{
    private readonly DotNetFileSystemProvider _inner;

    public PanelFtpFileSystemFactory(
        IOptions<DotNetFileSystemOptions> options,
        IAccountDirectoryQuery accountDirectoryQuery,
        ILogger<DotNetFileSystemProvider>? logger = null)
    {
        _inner = new DotNetFileSystemProvider(options, accountDirectoryQuery, logger);
    }

    public async Task<IUnixFileSystem> Create(IAccountInformation accountInformation)
    {
        var fs = await _inner.Create(accountInformation).ConfigureAwait(false);
        var username = accountInformation.FtpUser.Identity.Name ?? string.Empty;
        if (!FtpSessionStore.TryGet(username, out var session))
            return fs;
        return session.ReadOnly ? new ReadOnlyUnixFileSystem(fs) : fs;
    }
}

internal sealed class ReadOnlyUnixFileSystem : IUnixFileSystem
{
    private readonly IUnixFileSystem _inner;

    public ReadOnlyUnixFileSystem(IUnixFileSystem inner) => _inner = inner;

    public bool SupportsAppend => false;

    public bool SupportsNonEmptyDirectoryDelete => _inner.SupportsNonEmptyDirectoryDelete;

    public StringComparer FileSystemEntryComparer => _inner.FileSystemEntryComparer;

    public IUnixDirectoryEntry Root => _inner.Root;

    public Task<IReadOnlyList<IUnixFileSystemEntry>> GetEntriesAsync(
        IUnixDirectoryEntry directoryEntry,
        CancellationToken cancellationToken) =>
        _inner.GetEntriesAsync(directoryEntry, cancellationToken);

    public Task<IUnixFileSystemEntry?> GetEntryByNameAsync(
        IUnixDirectoryEntry directoryEntry,
        string name,
        CancellationToken cancellationToken) =>
        _inner.GetEntryByNameAsync(directoryEntry, name, cancellationToken);

    public Task<IUnixFileSystemEntry> MoveAsync(
        IUnixDirectoryEntry parent,
        IUnixFileSystemEntry source,
        IUnixDirectoryEntry target,
        string fileName,
        CancellationToken cancellationToken) =>
        throw new UnauthorizedAccessException("FTP account is read-only.");

    public Task UnlinkAsync(IUnixFileSystemEntry entry, CancellationToken cancellationToken) =>
        throw new UnauthorizedAccessException("FTP account is read-only.");

    public Task<IUnixDirectoryEntry> CreateDirectoryAsync(
        IUnixDirectoryEntry parent,
        string name,
        CancellationToken cancellationToken) =>
        throw new UnauthorizedAccessException("FTP account is read-only.");

    public Task<Stream> OpenReadAsync(
        IUnixFileEntry fileEntry,
        long startPosition,
        CancellationToken cancellationToken) =>
        _inner.OpenReadAsync(fileEntry, startPosition, cancellationToken);

    public Task<IBackgroundTransfer?> AppendAsync(
        IUnixFileEntry fileEntry,
        long? startPosition,
        Stream content,
        CancellationToken cancellationToken) =>
        throw new UnauthorizedAccessException("FTP account is read-only.");

    public Task<IBackgroundTransfer?> CreateAsync(
        IUnixDirectoryEntry parent,
        string name,
        Stream content,
        CancellationToken cancellationToken) =>
        throw new UnauthorizedAccessException("FTP account is read-only.");

    public Task<IBackgroundTransfer?> ReplaceAsync(
        IUnixFileEntry fileEntry,
        Stream content,
        CancellationToken cancellationToken) =>
        throw new UnauthorizedAccessException("FTP account is read-only.");

    public Task<IUnixFileSystemEntry> SetMacTimeAsync(
        IUnixFileSystemEntry entry,
        DateTimeOffset? modify,
        DateTimeOffset? access,
        DateTimeOffset? create,
        CancellationToken cancellationToken) =>
        _inner.SetMacTimeAsync(entry, modify, access, create, cancellationToken);
}
