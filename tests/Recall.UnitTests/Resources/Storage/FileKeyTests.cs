namespace Recall.UnitTests.Resources.Storage;

using Recall.Server.Exceptions;
using Recall.Server.Resources.Storage;
using Shouldly;

public class FileKeyTests
{
    [Test]
    [Arguments("")]
    [Arguments("..")]
    [Arguments(".")]
    [Arguments("../outside")]
    [Arguments("tenants/../../outside")]
    [Arguments("tenants/..")]
    [Arguments("tenants/./file")]
    [Arguments("/etc/passwd")]
    [Arguments("/")]
    [Arguments("tenants//file")]
    [Arguments("tenants/")]
    [Arguments("tenants\\..\\outside")]
    [Arguments("C:\\outside")]
    [Arguments("\\\\server\\share")]
    [Arguments("tenants/fi\0le")]
    [Arguments(".tmp")]
    [Arguments(".TMP/file")]
    [Arguments("tenants/.tmp/file")]
    public void a_value_that_can_leave_the_root_or_name_a_temporary_file_cannot_be_a_key(string value)
    {
        // Act & Assert
        Should.Throw<InvalidFileKeyException>(() => FileKey.Of(value));
    }

    [Test]
    [Arguments("file")]
    [Arguments("tenants/ten_1/recordings/rec_1/00001.webm")]
    [Arguments("..file")]
    [Arguments(".hidden/file..")]
    [Arguments("a b/c")]
    public void a_relative_path_is_a_key(string value)
    {
        // Act & Assert
        FileKey.Of(value).Value.ShouldBe(value);
    }

    [Test]
    [Arguments(255, true)]
    [Arguments(256, false)]
    public void a_segment_can_be_as_long_as_a_file_name(int length, bool isKey)
    {
        // Arrange
        var value = $"tenants/{new string('a', length)}";

        // Act
        var act = () => FileKey.Of(value);

        // Assert
        if (isKey)
            act().Value.ShouldBe(value);
        else
            Should.Throw<InvalidFileKeyException>(act);
    }

    [Test]
    public void a_long_segment_is_measured_in_bytes_and_not_in_characters()
    {
        // Arrange
        var value = new string('é', 128);

        // Act & Assert
        Should.Throw<InvalidFileKeyException>(() => FileKey.Of(value));
    }

    [Test]
    public void the_key_of_a_tenant_is_its_folder_and_a_path_goes_under_it()
    {
        // Act & Assert
        FileKey.ForTenant("ten_1").ShouldBe(FileKey.Of("tenants/ten_1"));
        FileKey.ForTenant("ten_1", "recordings", "rec_1/00001.webm").ShouldBe(FileKey.Of("tenants/ten_1/recordings/rec_1/00001.webm"));
    }

    [Test]
    [Arguments("../ten_2")]
    [Arguments("")]
    public void a_tenant_id_cannot_leave_the_folder_of_the_tenants(string tenantId)
    {
        // Act & Assert
        Should.Throw<InvalidFileKeyException>(() => FileKey.ForTenant(tenantId));
    }

    [Test]
    public void a_path_cannot_leave_the_folder_of_the_tenant()
    {
        // Act & Assert
        Should.Throw<InvalidFileKeyException>(() => FileKey.ForTenant("ten_1", "..", "ten_2", "file"));
    }

    [Test]
    public void a_bad_key_is_a_validation_error_that_names_the_field()
    {
        // Act
        var error = DomainError.From(Should.Throw<InvalidFileKeyException>(() => FileKey.Of("../outside")));

        // Assert
        error.Kind.ShouldBe(DomainErrorKind.Validation);
        error.Errors.ShouldNotBeNull().Keys.ShouldBe(["fileKey"]);
    }
}
