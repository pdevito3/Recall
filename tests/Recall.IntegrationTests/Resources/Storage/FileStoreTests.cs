namespace Recall.IntegrationTests.Resources.Storage;

using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Recall.Server.Exceptions;
using Recall.Server.Resources.Storage;
using Recall.SharedTestHelpers.Utilities;
using Shouldly;

public class FileStoreTests : IDisposable
{
    private readonly TemporaryFileRoot _root = new();
    private readonly string _outside = Path.Combine(Path.GetTempPath(), $"recall_test_outside_{Guid.NewGuid():N}");
    private readonly FileStore _store;

    public FileStoreTests()
    {
        _store = NewStore();
        Directory.CreateDirectory(_outside);
    }

    public void Dispose()
    {
        _root.Dispose();
        Directory.Delete(_outside, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Test]
    public async Task a_read_gives_the_bytes_of_the_write()
    {
        // Arrange
        var bytes = RandomNumberGenerator.GetBytes(1024 * 1024);

        // Act
        await WriteAsync("tenants/ten_1/file", bytes);

        // Assert
        (await ReadAsync("tenants/ten_1/file")).ShouldBe(bytes);
    }

    [Test]
    public async Task a_second_store_on_the_root_reads_the_file_after_a_restart()
    {
        // Arrange
        await WriteAsync("file", [1, 2, 3]);

        // Act
        var restarted = NewStore();
        restarted.Prepare();
        await using var stream = restarted.OpenRead(FileKey.Of("file"));

        // Assert
        stream.ReadByte().ShouldBe(1);
    }

    [Test]
    public async Task a_write_to_a_key_with_a_file_replaces_the_file()
    {
        // Arrange
        await WriteAsync("file", [1]);

        // Act
        await WriteAsync("file", [2, 3]);

        // Assert
        (await ReadAsync("file")).ShouldBe([2, 3]);
    }

    [Test]
    public async Task a_reader_of_a_replaced_file_keeps_the_old_bytes()
    {
        // Arrange
        await WriteAsync("file", [1, 2]);
        await using var reader = _store.OpenRead(FileKey.Of("file"));

        // Act
        await WriteAsync("file", [3, 4]);

        // Assert
        reader.ReadByte().ShouldBe(1);
        reader.ReadByte().ShouldBe(2);
    }

    [Test]
    public async Task a_write_that_fails_in_the_stream_leaves_no_file_and_no_temporary_file_and_a_retry_works()
    {
        // Arrange
        await using var failing = new FailingStream(bytesBeforeFailure: 128 * 1024);

        // Act
        await Should.ThrowAsync<IOException>(() => _store.WriteAsync(FileKey.Of("tenants/ten_1/file"), failing, CancellationToken.None));

        // Assert
        Should.Throw<NotFoundException>(() => _store.OpenRead(FileKey.Of("tenants/ten_1/file")));
        TemporaryItems().ShouldBeEmpty();
        await WriteAsync("tenants/ten_1/file", [1]);
        (await ReadAsync("tenants/ten_1/file")).ShouldBe([1]);
    }

    [Test]
    public async Task a_write_that_fails_keeps_the_old_file()
    {
        // Arrange
        await WriteAsync("file", [1]);
        await using var failing = new FailingStream(bytesBeforeFailure: 16);

        // Act
        await Should.ThrowAsync<IOException>(() => _store.WriteAsync(FileKey.Of("file"), failing, CancellationToken.None));

        // Assert
        (await ReadAsync("file")).ShouldBe([1]);
    }

    [Test]
    public async Task a_cancelled_write_leaves_no_file_and_no_temporary_file()
    {
        // Arrange
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        using var content = new MemoryStream([1]);

        // Act
        await Should.ThrowAsync<OperationCanceledException>(() => _store.WriteAsync(FileKey.Of("file"), content, cancelled.Token));

        // Assert
        Should.Throw<NotFoundException>(() => _store.OpenRead(FileKey.Of("file")));
        TemporaryItems().ShouldBeEmpty();
    }

    [Test]
    public async Task the_startup_removes_what_a_killed_process_left_in_the_temporary_folder()
    {
        // Arrange
        await WriteAsync("kept", [1]);
        var temporaryFolder = Path.Combine(_root.Path, FileKey.TemporaryFolder);
        await File.WriteAllBytesAsync(Path.Combine(temporaryFolder, "left-by-a-killed-write"), [1, 2]);
        Directory.CreateDirectory(Path.Combine(temporaryFolder, "left-by-a-killed-delete", "files"));

        // Act
        var removed = _store.Prepare();

        // Assert
        removed.ShouldBe(2);
        Directory.Exists(temporaryFolder).ShouldBeFalse();
        (await ReadAsync("kept")).ShouldBe([1]);
    }

    [Test]
    public void the_startup_makes_a_root_that_is_missing()
    {
        // Act
        var removed = _store.Prepare();

        // Assert
        removed.ShouldBe(0);
        Directory.Exists(_root.Path).ShouldBeTrue();
    }

    [Test]
    public async Task a_write_of_many_files_at_the_same_time_keeps_each_file_whole()
    {
        // Arrange
        var files = Enumerable.Range(0, 32).Select(i => (Key: $"tenants/ten_1/{i}", Bytes: RandomNumberGenerator.GetBytes(64 * 1024))).ToList();

        // Act
        await Task.WhenAll(files.Select(f => WriteAsync(f.Key, f.Bytes)));

        // Assert
        foreach (var (key, bytes) in files)
            (await ReadAsync(key)).ShouldBe(bytes);
        TemporaryItems().ShouldBeEmpty();
    }

    [Test]
    public void a_read_of_a_missing_file_is_not_found()
    {
        // Act & Assert
        Should.Throw<NotFoundException>(() => _store.OpenRead(FileKey.Of("tenants/ten_1/missing")));
    }

    [Test]
    public async Task a_read_of_a_key_that_names_a_folder_is_not_found()
    {
        // Arrange
        await WriteAsync("tenants/ten_1/file", [1]);

        // Act & Assert
        Should.Throw<NotFoundException>(() => _store.OpenRead(FileKey.Of("tenants/ten_1")));
    }

    [Test]
    public async Task a_read_of_a_key_below_a_file_is_not_found()
    {
        // Arrange
        await WriteAsync("file", [1]);

        // Act & Assert
        Should.Throw<NotFoundException>(() => _store.OpenRead(FileKey.Of("file/below")));
    }

    [Test]
    public async Task a_delete_removes_the_file_and_a_second_delete_is_a_no_op()
    {
        // Arrange
        await WriteAsync("tenants/ten_1/file", [1]);

        // Act
        _store.Delete(FileKey.Of("tenants/ten_1/file"));
        _store.Delete(FileKey.Of("tenants/ten_1/file"));
        _store.Delete(FileKey.Of("no/folder/with/this/file"));

        // Assert
        Should.Throw<NotFoundException>(() => _store.OpenRead(FileKey.Of("tenants/ten_1/file")));
    }

    [Test]
    public async Task a_delete_under_a_prefix_removes_each_file_under_it_and_keeps_a_folder_with_a_longer_name()
    {
        // Arrange
        await WriteAsync("tenants/ten_1/a", [1]);
        await WriteAsync("tenants/ten_1/deep/er/b", [2]);
        await WriteAsync("tenants/ten_11/c", [3]);

        // Act
        _store.DeleteUnder(FileKey.ForTenant("ten_1"));

        // Assert
        Should.Throw<NotFoundException>(() => _store.OpenRead(FileKey.Of("tenants/ten_1/a")));
        Should.Throw<NotFoundException>(() => _store.OpenRead(FileKey.Of("tenants/ten_1/deep/er/b")));
        (await ReadAsync("tenants/ten_11/c")).ShouldBe([3]);
        TemporaryItems().ShouldBeEmpty();
    }

    [Test]
    public async Task a_delete_under_a_prefix_with_no_folder_is_a_no_op()
    {
        // Arrange
        await WriteAsync("file", [1]);

        // Act
        _store.DeleteUnder(FileKey.ForTenant("ten_1"));
        _store.DeleteUnder(FileKey.Of("file"));

        // Assert
        (await ReadAsync("file")).ShouldBe([1]);
    }

    [Test]
    public async Task a_delete_under_a_prefix_removes_a_link_in_it_and_keeps_the_target_of_the_link()
    {
        // Arrange
        var target = Path.Combine(_outside, "kept");
        await File.WriteAllBytesAsync(target, [1]);
        await WriteAsync("tenants/ten_1/file", [2]);
        Directory.CreateSymbolicLink(Path.Combine(_root.Path, "tenants", "ten_1", "link"), _outside);

        // Act
        _store.DeleteUnder(FileKey.ForTenant("ten_1"));

        // Assert
        (await File.ReadAllBytesAsync(target)).ShouldBe([1]);
        Directory.Exists(Path.Combine(_root.Path, "tenants", "ten_1")).ShouldBeFalse();
    }

    [Test]
    public async Task a_symlink_folder_that_leaves_the_root_is_refused_by_each_method()
    {
        // Arrange
        var outsideFile = Path.Combine(_outside, "secret");
        await File.WriteAllBytesAsync(outsideFile, [1]);
        Directory.CreateDirectory(_root.Path);
        Directory.CreateSymbolicLink(Path.Combine(_root.Path, "link"), _outside);
        var key = FileKey.Of("link/secret");

        // Act & Assert
        Should.Throw<IOException>(() => _store.OpenRead(key)).Message.ShouldContain("symlink");
        await Should.ThrowAsync<IOException>(() => WriteAsync("link/secret", [2]));
        await Should.ThrowAsync<IOException>(() => WriteAsync("link/new", [2]));
        Should.Throw<IOException>(() => _store.Delete(key));
        Should.Throw<IOException>(() => _store.DeleteUnder(FileKey.Of("link")));
        (await File.ReadAllBytesAsync(outsideFile)).ShouldBe([1]);
        Directory.GetFileSystemEntries(_outside).ShouldBe([outsideFile]);
    }

    [Test]
    public async Task a_symlink_file_that_leaves_the_root_is_refused()
    {
        // Arrange
        var outsideFile = Path.Combine(_outside, "secret");
        await File.WriteAllBytesAsync(outsideFile, [1]);
        Directory.CreateDirectory(_root.Path);
        File.CreateSymbolicLink(Path.Combine(_root.Path, "link"), outsideFile);

        // Act & Assert
        Should.Throw<IOException>(() => _store.OpenRead(FileKey.Of("link")));
        await Should.ThrowAsync<IOException>(() => WriteAsync("link", [2]));
        (await File.ReadAllBytesAsync(outsideFile)).ShouldBe([1]);
    }

    [Test]
    public async Task a_temporary_folder_that_is_a_symlink_is_refused_and_the_startup_removes_only_the_link()
    {
        // Arrange
        var outsideFile = Path.Combine(_outside, "kept");
        await File.WriteAllBytesAsync(outsideFile, [1]);
        Directory.CreateDirectory(_root.Path);
        Directory.CreateSymbolicLink(Path.Combine(_root.Path, FileKey.TemporaryFolder), _outside);

        // Act & Assert
        await Should.ThrowAsync<IOException>(() => WriteAsync("file", [2]));
        _store.Prepare();
        (await File.ReadAllBytesAsync(outsideFile)).ShouldBe([1]);
        Directory.Exists(Path.Combine(_root.Path, FileKey.TemporaryFolder)).ShouldBeFalse();
    }

    [Test]
    public async Task a_root_that_is_itself_a_symlink_works()
    {
        // Arrange
        Directory.CreateSymbolicLink(_root.Path, _outside);

        // Act
        await WriteAsync("file", [1]);

        // Assert
        (await File.ReadAllBytesAsync(Path.Combine(_outside, "file"))).ShouldBe([1]);
    }

    private FileStore NewStore() => new(Options.Create(new FileStoreOptions { Root = _root.Path }));

    private string[] TemporaryItems()
    {
        var folder = Path.Combine(_root.Path, FileKey.TemporaryFolder);
        return Directory.Exists(folder) ? Directory.GetFileSystemEntries(folder) : [];
    }

    private async Task WriteAsync(string key, byte[] bytes)
    {
        using var content = new MemoryStream(bytes);
        await _store.WriteAsync(FileKey.Of(key), content, CancellationToken.None);
    }

    private async Task<byte[]> ReadAsync(string key)
    {
        await using var stream = _store.OpenRead(FileKey.Of(key));
        using var bytes = new MemoryStream();
        await stream.CopyToAsync(bytes);
        return bytes.ToArray();
    }

    private sealed class FailingStream(int bytesBeforeFailure) : MemoryStream
    {
        public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            await destination.WriteAsync(new byte[bytesBeforeFailure], cancellationToken);
            throw new IOException("The connection dropped.");
        }
    }
}
