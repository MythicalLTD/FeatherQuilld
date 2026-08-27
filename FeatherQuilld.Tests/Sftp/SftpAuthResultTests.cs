using FeatherQuilld.Utils.Sftp;

namespace FeatherQuilld.Tests.Sftp;

public class SftpAuthResultTests
{
    [Fact]
    public void IsReadOnly_EmptyPermissions_IsWritable()
    {
        Assert.False(new SftpAuthResult().IsReadOnly);
    }

    [Fact]
    public void IsReadOnly_Star_IsWritable()
    {
        Assert.False(new SftpAuthResult { Permissions = ["*"] }.IsReadOnly);
    }

    [Fact]
    public void IsReadOnly_FileReadOnly_IsReadOnly()
    {
        Assert.True(new SftpAuthResult { Permissions = ["file.read", "file.sftp"] }.IsReadOnly);
    }

    [Fact]
    public void IsReadOnly_FileCreate_IsWritable()
    {
        Assert.False(new SftpAuthResult { Permissions = ["file.read", "file.create"] }.IsReadOnly);
    }

    [Fact]
    public void IsReadOnly_WriteSubstring_IsWritable()
    {
        Assert.False(new SftpAuthResult { Permissions = ["file.write"] }.IsReadOnly);
    }
}
