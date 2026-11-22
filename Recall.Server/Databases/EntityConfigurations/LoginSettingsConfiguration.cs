namespace Recall.Server.Databases.EntityConfigurations;

using Domain.LoginProviders;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class LoginSettingsConfiguration : IEntityTypeConfiguration<LoginSettings>
{
    public void Configure(EntityTypeBuilder<LoginSettings> builder)
    {
        // One row, with the key 1, so two first writes cannot both insert one. The migration inserts it.
        builder.ToTable("auth_login_settings", table =>
        {
            table.HasCheckConstraint("ck_auth_login_settings_one_row", $"id = {LoginSettings.RowId}");
            table.HasCheckConstraint("ck_auth_login_settings_password_login", "password_login IN ('On', 'Off')");
        });
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.PasswordLogin).HasConversion<string>().HasMaxLength(10);
        builder.Property(e => e.Version).IsConcurrencyToken();
        builder.HasData(LoginSettings.Seed());
    }
}
