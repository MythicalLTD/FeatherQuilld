namespace FeatherQuilld.Utils.WebSpaces.Backups;

/// <summary>
/// File stream that deletes its underlying path when disposed (restic/PBS restore temp archives).
/// </summary>
internal sealed class TempFileStream : FileStream
{
    public TempFileStream(string path)
        : base(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.DeleteOnClose)
    {
    }
}
