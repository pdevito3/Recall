namespace Recall.UnitTests.Databases.Sqlite;

using Recall.Server.Databases.Sqlite;
using Shouldly;

public class NetworkMountCheckTests
{
    [Test]
    [Arguments("nfs")]
    [Arguments("nfs4")]
    [Arguments("cifs")]
    [Arguments("smbfs")]
    [Arguments("fuse.sshfs")]
    [Arguments("fuse.juicefs")]
    [Arguments("fuse.shfs")]
    [Arguments("shfs")]
    [Arguments("fuse.mergerfs")]
    [Arguments("fuse.nfs")]
    [Arguments("fuse.cephfs")]
    [Arguments("fuse.unknown-type")]
    [Arguments("FUSE.SSHFS")]
    [Arguments("fuse")]
    [Arguments("vboxsf")]
    [Arguments("virtiofs")]
    [Arguments("fakeowner")]
    [Arguments("macfuse")]
    [Arguments("osxfuse")]
    [Arguments("prl_fs")]
    [Arguments("vmhgfs")]
    [Arguments("gfs2")]
    [Arguments("orangefs")]
    [Arguments("pvfs2")]
    [Arguments("9p")]
    public void a_network_mount_is_refused_with_a_message_that_names_the_mount(string type)
    {
        // Arrange
        MountEntry[] mounts = [new("/", "ext4", "/dev/sda1"), new("/data", type, "nas:/volume1/recall")];

        // Act
        var refusal = NetworkMountCheck.Refusal("/data/recall.db", mounts);

        // Assert
        refusal.ShouldNotBeNull();
        refusal.ShouldContain("/data/recall.db");
        refusal.ShouldContain("/data");
        refusal.ShouldContain(type);
        refusal.ShouldContain("nas:/volume1/recall");
        refusal.ShouldContain("Move the data directory to a local disk.");
    }

    [Test]
    public void a_file_root_on_a_network_mount_is_refused_with_a_message_about_the_file_store()
    {
        // Arrange
        MountEntry[] mounts = [new("/data", "ext4", "/dev/sda1"), new("/data/files", "nfs4", "nas:/volume1/files")];

        // Act
        var refusal = NetworkMountCheck.FileRootRefusal("/data/files", mounts);

        // Assert
        refusal.ShouldNotBeNull();
        refusal.ShouldContain("The file store root /data/files is on the mount /data/files (nfs4 from nas:/volume1/files)");
        refusal.ShouldContain("Move the file store root to a local disk.");
        refusal.ShouldNotContain("SQLite");
        NetworkMountCheck.Refusal("/data/recall.db", mounts).ShouldBeNull();
    }

    [Test]
    public void a_file_root_on_a_local_mount_passes()
    {
        // Arrange
        MountEntry[] mounts = [new("/", "nfs4", "nas:/root"), new("/data", "ext4", "/dev/sda1")];

        // Act & Assert
        NetworkMountCheck.FileRootRefusal("/data/files", mounts).ShouldBeNull();
    }

    [Test]
    [Arguments("ext4")]
    [Arguments("btrfs")]
    [Arguments("xfs")]
    [Arguments("zfs")]
    [Arguments("overlay")]
    [Arguments("apfs")]
    [Arguments("fuseblk")]
    [Arguments("fuseblk.ntfs-3g")]
    public void a_local_mount_passes(string type)
    {
        // Arrange
        MountEntry[] mounts = [new("/", "ext4", "/dev/sda1"), new("/data", type, "/dev/md0")];

        // Act & Assert
        NetworkMountCheck.Refusal("/data/recall.db", mounts).ShouldBeNull();
    }

    [Test]
    public void the_longest_mount_point_of_the_path_decides()
    {
        // Arrange
        MountEntry[] mounts = [new("/data", "nfs4", "nas:/share"), new("/data/local", "ext4", "/dev/sdb1")];

        // Act & Assert
        NetworkMountCheck.Refusal("/data/local/recall.db", mounts).ShouldBeNull();
        NetworkMountCheck.Refusal("/data/recall.db", mounts).ShouldNotBeNull();
    }

    [Test]
    public void a_mount_that_covers_another_at_the_same_point_decides()
    {
        // Arrange
        MountEntry nfs = new("/data", "nfs", "nas:/share");
        MountEntry local = new("/data", "ext4", "/dev/sdb1");

        // Act & Assert
        NetworkMountCheck.Refusal("/data/recall.db", [nfs, local]).ShouldBeNull();
        NetworkMountCheck.Refusal("/data/recall.db", [local, nfs]).ShouldNotBeNull();
    }

    [Test]
    public void a_mount_point_that_only_shares_a_prefix_with_the_path_does_not_decide()
    {
        // Arrange
        MountEntry[] mounts = [new("/", "ext4", "/dev/sda1"), new("/data-remote", "nfs", "nas:/share")];

        // Act & Assert
        NetworkMountCheck.Refusal("/data/recall.db", mounts).ShouldBeNull();
    }

    [Test]
    public void a_path_with_no_mount_passes()
    {
        // Act & Assert
        NetworkMountCheck.Refusal("/data/recall.db", []).ShouldBeNull();
    }

    [Test]
    public void mountinfo_gives_the_point_the_type_and_the_source_of_each_line()
    {
        // Arrange
        const string text = """
            22 1 8:1 / / rw,relatime shared:1 - ext4 /dev/sda1 rw
            36 22 0:40 /volume1 /mnt/my\040data rw,relatime shared:2 master:3 - nfs4 nas:/volume\0401 rw,vers=4.1
            37 22 0:41 / /weird rw
            """;

        // Act
        var entries = NetworkMountCheck.ParseMountInfo(text);

        // Assert
        entries.ShouldBe([
            new MountEntry("/", "ext4", "/dev/sda1"),
            new MountEntry("/mnt/my data", "nfs4", "nas:/volume 1"),
        ]);
    }

    [Test]
    [Arguments(":/", "rw,vers=4.2,clientaddr=172.27.0.1,local_lock=none,addr=172.27.0.2", "172.27.0.2:/")]
    [Arguments(":/export", "rw,vers=3,mountaddr=172.27.0.9,addr=172.27.0.2", "172.27.0.2:/export")]
    [Arguments(":/", "rw,vers=4.2,clientaddr=172.27.0.1", ":/")]
    [Arguments("nas:/share", "rw,vers=4.2,addr=172.27.0.2", "nas:/share")]
    [Arguments("//nas/share", "rw,vers=3.1.1,addr=172.27.0.2", "//nas/share")]
    public void a_source_with_no_host_takes_the_server_from_the_super_options(string source, string superOptions, string expected)
    {
        // Arrange
        var line = $"411 400 0:87 / /data rw,relatime master:11 - nfs4 {source} {superOptions}";

        // Act
        var entry = NetworkMountCheck.ParseMountInfo(line).ShouldHaveSingleItem();

        // Assert
        entry.Source.ShouldBe(expected);
        NetworkMountCheck.Refusal("/data/recall.db", [entry]).ShouldNotBeNull().ShouldContain($"from {expected})");
    }

    [Test]
    public void mount_output_gives_the_point_the_type_and_the_source_of_each_line()
    {
        // Arrange
        const string text = """
            /dev/disk3s1s1 on / (apfs, sealed, local, read-only, journaled)
            //me@nas/My Share on /Volumes/My Share (smbfs, nodev, nosuid, mounted by me)
            this line has another shape
            """;

        // Act
        var entries = NetworkMountCheck.ParseMountOutput(text);

        // Assert
        entries.ShouldBe([
            new MountEntry("/", "apfs", "/dev/disk3s1s1"),
            new MountEntry("/Volumes/My Share", "smbfs", "//me@nas/My Share"),
        ]);
    }
}
