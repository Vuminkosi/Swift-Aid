using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SwiftAid.Infrastructure.Persistence.Notifications;

public sealed class PushDeviceEntityConfiguration : IEntityTypeConfiguration<PushDeviceEntity>
{
    public void Configure(EntityTypeBuilder<PushDeviceEntity> builder)
    {
        builder.ToTable("push_devices", "swiftaid_notifications", table =>
        {
            table.HasCheckConstraint(
                "ck_push_devices_platform",
                "platform IN ('Android', 'Ios', 'Web')");
            table.HasCheckConstraint(
                "ck_push_devices_token_material",
                "length(protected_token) > 0 AND octet_length(token_fingerprint) = 32");
            table.HasCheckConstraint(
                "ck_push_devices_actor_nonempty",
                "length(btrim(actor_id)) > 0");
            table.HasCheckConstraint(
                "ck_push_devices_installation_nonempty",
                "installation_id <> '00000000-0000-0000-0000-000000000000'::uuid");
        });

        builder.HasKey(device => device.DeviceId)
            .HasName("pk_push_devices");
        builder.Property(device => device.DeviceId)
            .HasColumnName("device_id")
            .HasColumnType("uuid");
        builder.Property(device => device.ActorId)
            .HasColumnName("actor_id")
            .HasColumnType("text")
            .IsRequired();
        builder.Property(device => device.InstallationId)
            .HasColumnName("installation_id")
            .HasColumnType("uuid")
            .IsRequired();
        builder.Property(device => device.Platform)
            .HasColumnName("platform")
            .HasConversion<string>()
            .HasColumnType("text")
            .IsRequired();
        builder.Property(device => device.SoundEnabled)
            .HasColumnName("sound_enabled")
            .HasDefaultValue(false)
            .IsRequired();
        builder.Property(device => device.ProtectedToken)
            .HasColumnName("protected_token")
            .HasColumnType("bytea")
            .IsRequired();
        builder.Property(device => device.TokenFingerprint)
            .HasColumnName("token_fingerprint")
            .HasColumnType("bytea")
            .IsRequired();
        builder.Property(device => device.CreatedAtUtc)
            .HasColumnName("created_at_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();
        builder.Property(device => device.UpdatedAtUtc)
            .HasColumnName("updated_at_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.HasIndex(device => new { device.ActorId, device.InstallationId })
            .IsUnique()
            .HasDatabaseName("ux_push_devices_actor_installation");
        builder.HasIndex(device => device.TokenFingerprint)
            .IsUnique()
            .HasDatabaseName("ux_push_devices_token_fingerprint");
        builder.HasOne<ActorEntity>()
            .WithMany()
            .HasForeignKey(device => device.ActorId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_push_devices_actor");
    }
}
