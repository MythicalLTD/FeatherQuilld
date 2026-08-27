using FeatherQuilld.Utils.Config.System;

namespace FeatherQuilld.Utils.WebSpaces.Backups;

internal static class BackupTempPaths
{
    internal static string Root(SystemConfig system)
    {
        var root = string.IsNullOrWhiteSpace(system.TmpDirectory)
            ? Path.GetTempPath()
            : system.TmpDirectory.Trim();
        global::System.IO.Directory.CreateDirectory(root);
        return root;
    }

    internal static string File(SystemConfig system, string prefix, Guid backupUuid, string extension = ".tar.gz") =>
        Path.Combine(Root(system), $"{prefix}-{backupUuid:N}{extension}");

    internal static string Directory(SystemConfig system, string prefix, Guid backupUuid) =>
        Path.Combine(Root(system), $"{prefix}-{backupUuid:N}");
}
