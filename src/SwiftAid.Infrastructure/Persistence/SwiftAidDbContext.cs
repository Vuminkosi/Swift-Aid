using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using SwiftAid.Domain.Cases;
using SwiftAid.Domain.Notifications;
using SwiftAid.Infrastructure.Identity;
using SwiftAid.Infrastructure.Persistence.Entities.Cases;
using SwiftAid.Infrastructure.Persistence.Entities.Intake;
using SwiftAid.Infrastructure.Persistence.Notifications;
using SwiftAid.Infrastructure.Persistence.Sessions;

namespace SwiftAid.Infrastructure.Persistence;

/// <summary>
/// The sole PostgreSQL persistence context for identity references, access grants,
/// managed-case projections, event metadata, receipts, audit, and outbox records.
/// </summary>
public sealed class SwiftAidDbContext(DbContextOptions<SwiftAidDbContext> options)
    : IdentityUserContext<SwiftAidUser>(options)
{
    public DbSet<ExternalIdentityMappingEntity> ExternalIdentityMappings => Set<ExternalIdentityMappingEntity>();
    public DbSet<EmailVerificationEntity> EmailVerifications => Set<EmailVerificationEntity>();
    public DbSet<UserProfileEntity> UserProfiles => Set<UserProfileEntity>();
    public DbSet<MembershipRequestEntity> MembershipRequests => Set<MembershipRequestEntity>();

    public DbSet<OrganizationInvitationEntity> OrganizationInvitations => Set<OrganizationInvitationEntity>();
    public DbSet<NotificationPreferenceEntity> NotificationPreferences => Set<NotificationPreferenceEntity>();
    public DbSet<ActorEntity> Actors => Set<ActorEntity>();
    public DbSet<ActorAccessStateEntity> ActorAccessStates => Set<ActorAccessStateEntity>();
    public DbSet<OrganizationEntity> Organizations => Set<OrganizationEntity>();
    public DbSet<WorkspaceEntity> Workspaces => Set<WorkspaceEntity>();
    public DbSet<WorkspaceOrganizationEntity> WorkspaceOrganizations => Set<WorkspaceOrganizationEntity>();
    public DbSet<OrganizationMembershipEntity> OrganizationMemberships => Set<OrganizationMembershipEntity>();
    public DbSet<AccessGrantGroupEntity> AccessGrantGroups => Set<AccessGrantGroupEntity>();
    public DbSet<AccessGrantRegionEntity> AccessGrantRegions => Set<AccessGrantRegionEntity>();
    public DbSet<AccessGrantOperationEntity> AccessGrantOperations => Set<AccessGrantOperationEntity>();
    public DbSet<ManagedCaseEntity> ManagedCases => Set<ManagedCaseEntity>();
    public DbSet<ManagedCaseRegionEntity> ManagedCaseRegions => Set<ManagedCaseRegionEntity>();
    public DbSet<CaseShareEntity> CaseShares => Set<CaseShareEntity>();
    public DbSet<ProtectedEventPayloadEntity> ProtectedEventPayloads => Set<ProtectedEventPayloadEntity>();
    public DbSet<CaseEventEntity> CaseEvents => Set<CaseEventEntity>();
    public DbSet<CommandReceiptEntity> CommandReceipts => Set<CommandReceiptEntity>();
    public DbSet<DomainOutboxEntity> DomainOutbox => Set<DomainOutboxEntity>();
    public DbSet<SecurityAuditEntryEntity> SecurityAuditEntries => Set<SecurityAuditEntryEntity>();
    public DbSet<NotificationItem> NotificationItems => Set<NotificationItem>();
    public DbSet<NotificationDeliveryOutboxEntity> NotificationDeliveryOutbox => Set<NotificationDeliveryOutboxEntity>();
    public DbSet<PushDeviceEntity> PushDevices => Set<PushDeviceEntity>();
    public DbSet<IntakeSessionEntity> IntakeSessions => Set<IntakeSessionEntity>();
    public DbSet<IntakeCapabilityEntity> IntakeCapabilities => Set<IntakeCapabilityEntity>();
    public DbSet<IntakeReportEntity> IntakeReports => Set<IntakeReportEntity>();
    public DbSet<IntakeRouteEntity> IntakeRoutes => Set<IntakeRouteEntity>();
    public DbSet<IntakeAttachmentEntity> IntakeAttachments => Set<IntakeAttachmentEntity>();
    public DbSet<IntakeOutboxEntity> IntakeOutbox => Set<IntakeOutboxEntity>();
    public DbSet<IntakePromotionEntity> IntakePromotions => Set<IntakePromotionEntity>();
    public DbSet<IntakeEvidenceLinkEntity> IntakeEvidenceLinks => Set<IntakeEvidenceLinkEntity>();
    public DbSet<IntakeCoordinationActionEntity> IntakeCoordinationActions => Set<IntakeCoordinationActionEntity>();
    public DbSet<ManagedCaseObservationEntity> ManagedCaseObservations => Set<ManagedCaseObservationEntity>();
    public DbSet<GeographicLocationEntity> GeographicLocations => Set<GeographicLocationEntity>();
    public DbSet<ManagedCaseFollowUpEntity> ManagedCaseFollowUps => Set<ManagedCaseFollowUpEntity>();
    public DbSet<ManagedCaseEscalationEntity> ManagedCaseEscalations => Set<ManagedCaseEscalationEntity>();
    public DbSet<CommunitySessionEntity> CommunitySessions => Set<CommunitySessionEntity>();
    public DbSet<CommunitySessionRevisionEntity> CommunitySessionRevisions => Set<CommunitySessionRevisionEntity>();
    public DbSet<SessionReminderOccurrenceEntity> SessionReminderOccurrences => Set<SessionReminderOccurrenceEntity>();
    public DbSet<Ellai.EllaiSessionEntity> EllaiSessions => Set<Ellai.EllaiSessionEntity>();
    public DbSet<Ellai.EllaiCheckpointEntity> EllaiCheckpoints => Set<Ellai.EllaiCheckpointEntity>();
    public DbSet<Ellai.EllaiUtteranceEntity> EllaiUtterances => Set<Ellai.EllaiUtteranceEntity>();
    public DbSet<Ellai.EllaiPlanApprovalEntity> EllaiPlanApprovals => Set<Ellai.EllaiPlanApprovalEntity>();
    public DbSet<Ellai.EllaiTopicEntity> EllaiTopics => Set<Ellai.EllaiTopicEntity>();
    public DbSet<Ellai.EllaiConsentGrantEntity> EllaiConsentGrants => Set<Ellai.EllaiConsentGrantEntity>();
    public DbSet<Ellai.EllaiUsageSampleEntity> EllaiUsageSamples => Set<Ellai.EllaiUsageSampleEntity>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        AddInitialWorkspaceOrganizations();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        AddInitialWorkspaceOrganizations();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void AddInitialWorkspaceOrganizations()
    {
        // Keep existing workspace-creation callers compatible. Additional organizations are explicit links.
        foreach (var entry in ChangeTracker.Entries<WorkspaceEntity>().Where(entry => entry.State == EntityState.Added).ToArray())
        {
            var workspace = entry.Entity;
            if (ChangeTracker.Entries<WorkspaceOrganizationEntity>().Any(link => link.Entity.WorkspaceId == workspace.WorkspaceId &&
                link.Entity.OrganizationId == workspace.OrganizationId)) continue;
            WorkspaceOrganizations.Add(new WorkspaceOrganizationEntity { WorkspaceId = workspace.WorkspaceId,
                OrganizationId = workspace.OrganizationId, Status = workspace.Status,
                CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        ConfigureOpenIddictSchema(modelBuilder);
        ConfigureIdentity(modelBuilder);
        ConfigureActors(modelBuilder);
        ConfigureOrganizationsAndAccess(modelBuilder);
        ConfigureCases(modelBuilder);
        ConfigureEventsAndCommands(modelBuilder);
        ConfigureAudit(modelBuilder);
        Classification.ClassificationModel.Configure(modelBuilder);
        Classification.TranslationModel.Configure(modelBuilder);
        Classification.ClassificationBackfillModel.Configure(modelBuilder);
        Classification.ReportQualityModel.Configure(modelBuilder);
        Surveillance.SurveillanceModel.Configure(modelBuilder);
        modelBuilder.ApplyConfiguration(new NotificationItemEntityConfiguration());
        modelBuilder.ApplyConfiguration(new NotificationDeliveryOutboxEntityConfiguration());
        modelBuilder.ApplyConfiguration(new PushDeviceEntityConfiguration());
        modelBuilder.ApplyConfiguration(new IntakeSessionEntityConfiguration());
        modelBuilder.ApplyConfiguration(new IntakeCapabilityEntityConfiguration());
        modelBuilder.ApplyConfiguration(new IntakeReportEntityConfiguration());
        modelBuilder.ApplyConfiguration(new IntakeRouteEntityConfiguration());
        modelBuilder.ApplyConfiguration(new IntakeAttachmentEntityConfiguration());
        modelBuilder.ApplyConfiguration(new IntakeOutboxEntityConfiguration());
        modelBuilder.ApplyConfiguration(new IntakeImports.CaseImportConfiguration());
        modelBuilder.ApplyConfiguration(new IntakeImports.CaseImportRowConfiguration());
        modelBuilder.ApplyConfiguration(new IntakePromotionEntityConfiguration());
        modelBuilder.ApplyConfiguration(new IntakeEvidenceLinkEntityConfiguration());
        modelBuilder.ApplyConfiguration(new IntakeCoordinationActionEntityConfiguration());
        modelBuilder.ApplyConfiguration(new GeographicLocationEntityConfiguration());
        modelBuilder.ApplyConfiguration(new ManagedCaseObservationEntityConfiguration());
        modelBuilder.ApplyConfiguration(new ManagedCaseFollowUpEntityConfiguration());
        modelBuilder.ApplyConfiguration(new ManagedCaseEscalationEntityConfiguration());
        modelBuilder.ApplyConfiguration(new CommunitySessionEntityConfiguration());
        modelBuilder.ApplyConfiguration(new CommunitySessionRevisionEntityConfiguration());
        modelBuilder.ApplyConfiguration(new SessionReminderOccurrenceEntityConfiguration());
        modelBuilder.ApplyConfiguration(new Ellai.EllaiSessionEntityConfiguration());
        modelBuilder.ApplyConfiguration(new Ellai.EllaiCheckpointEntityConfiguration());
        modelBuilder.ApplyConfiguration(new Ellai.EllaiUtteranceEntityConfiguration());
        modelBuilder.ApplyConfiguration(new Ellai.EllaiPlanApprovalEntityConfiguration());
        modelBuilder.ApplyConfiguration(new Ellai.EllaiTopicEntityConfiguration());
        modelBuilder.ApplyConfiguration(new Ellai.EllaiConsentGrantEntityConfiguration());
        modelBuilder.ApplyConfiguration(new Ellai.EllaiUsageSampleEntityConfiguration());
    }

    private static void ConfigureOpenIddictSchema(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes()
                     .Where(entityType => entityType.Name.StartsWith(
                         "OpenIddict.EntityFrameworkCore.Models.",
                         StringComparison.Ordinal)))
        {
            entityType.SetSchema("swiftaid_identity");
        }
    }

    private static void ConfigureIdentity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SwiftAidUser>().ToTable("users", "swiftaid_identity");
        modelBuilder.Entity<IdentityUserClaim<string>>().ToTable("user_claims", "swiftaid_identity");
        modelBuilder.Entity<IdentityUserLogin<string>>().ToTable("user_logins", "swiftaid_identity");
        modelBuilder.Entity<IdentityUserToken<string>>().ToTable("user_tokens", "swiftaid_identity");
        modelBuilder.Entity<IdentityUserPasskey<string>>(passkey =>
        {
            passkey.HasKey(entity => entity.CredentialId);
            passkey.ToTable("user_passkeys", "swiftaid_identity");
            passkey.Property(entity => entity.CredentialId).HasMaxLength(1024);
            passkey.Property(entity => entity.UserId).IsRequired();
            passkey.HasOne<SwiftAidUser>()
                .WithMany()
                .HasForeignKey(entity => entity.UserId)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("fk_user_passkeys_user");
            passkey.OwnsOne(entity => entity.Data).ToJson();
        });

        modelBuilder.Entity<SwiftAidUser>()
            .HasOne<ActorEntity>()
            .WithOne()
            .HasForeignKey<SwiftAidUser>(user => user.Id)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_identity_users_actor");

        var mapping = modelBuilder.Entity<ExternalIdentityMappingEntity>();
        mapping.ToTable("external_identities", "swiftaid_identity", table =>
        {
            table.HasCheckConstraint(
                "ck_external_identities_provider_length",
                "length(btrim(provider)) BETWEEN 1 AND 128");
            table.HasCheckConstraint(
                "ck_external_identities_issuer_length",
                "length(btrim(issuer)) BETWEEN 1 AND 2048");
            table.HasCheckConstraint(
                "ck_external_identities_subject_length",
                "length(btrim(subject)) BETWEEN 1 AND 1024");
        });
        mapping.HasKey(entity => entity.MappingId).HasName("pk_external_identities");
        mapping.Property(entity => entity.MappingId).HasColumnName("mapping_id").HasColumnType("uuid");
        mapping.Property(entity => entity.ActorId).HasColumnName("actor_id").HasColumnType("text");
        mapping.Property(entity => entity.Provider).HasColumnName("provider").HasColumnType("text");
        mapping.Property(entity => entity.Issuer).HasColumnName("issuer").HasColumnType("text");
        mapping.Property(entity => entity.Subject).HasColumnName("subject").HasColumnType("text");
        mapping.Property(entity => entity.CreatedAtUtc).HasColumnName("created_at_utc")
            .HasColumnType("timestamp with time zone");
        mapping.HasIndex(entity => new { entity.Provider, entity.Issuer, entity.Subject })
            .IsUnique().HasDatabaseName("ux_external_identities_provider_issuer_subject");
        mapping.HasOne<ActorEntity>().WithMany().HasForeignKey(entity => entity.ActorId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_external_identities_actor");

        var verification = modelBuilder.Entity<EmailVerificationEntity>();
        verification.ToTable("email_verifications", "swiftaid_identity", table =>
        {
            table.HasCheckConstraint(
                "ck_email_verifications_attempts_nonnegative",
                "attempt_count >= 0");
        });
        verification.HasKey(entity => entity.UserId).HasName("pk_email_verifications");
        verification.Property(entity => entity.UserId).HasColumnName("user_id").HasColumnType("text");
        verification.Property(entity => entity.CodeHash).HasColumnName("code_hash").HasColumnType("text");
        verification.Property(entity => entity.PendingEmail).HasColumnName("pending_email").HasColumnType("text");
        verification.Property(entity => entity.ExpiresAtUtc).HasColumnName("expires_at_utc")
            .HasColumnType("timestamp with time zone");
        verification.Property(entity => entity.AttemptCount).HasColumnName("attempt_count");
        verification.Property(entity => entity.LastSentAtUtc).HasColumnName("last_sent_at_utc")
            .HasColumnType("timestamp with time zone");
        verification.Property(entity => entity.CreatedAtUtc).HasColumnName("created_at_utc")
            .HasColumnType("timestamp with time zone");
        verification.HasOne<SwiftAidUser>().WithMany().HasForeignKey(entity => entity.UserId)
            .OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_email_verifications_user");

        var profile = modelBuilder.Entity<UserProfileEntity>();
        profile.ToTable("user_profiles", "swiftaid_identity");
        profile.HasKey(entity => entity.UserId).HasName("pk_user_profiles");
        profile.Property(entity => entity.UserId).HasColumnName("user_id").HasColumnType("text");
        profile.Property(entity => entity.DisplayName).HasColumnName("display_name").HasColumnType("text");
        profile.Property(entity => entity.Phone).HasColumnName("phone").HasColumnType("text");
        profile.Property(entity => entity.Country).HasColumnName("country").HasColumnType("text");
        profile.Property(entity => entity.Region).HasColumnName("region").HasColumnType("text");
        profile.Property(entity => entity.Locality).HasColumnName("locality").HasColumnType("text");
        profile.Property(entity => entity.AvatarObjectId).HasColumnName("avatar_object_id").HasColumnType("uuid");
        profile.Property(entity => entity.AvatarContentType).HasColumnName("avatar_content_type").HasColumnType("text");
        profile.Property(entity => entity.UpdatedAtUtc).HasColumnName("updated_at_utc")
            .HasColumnType("timestamp with time zone");
        profile.HasOne<SwiftAidUser>().WithMany().HasForeignKey(entity => entity.UserId)
            .OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_user_profiles_user");
    }

    private static void ConfigureActors(ModelBuilder modelBuilder)
    {
        var actor = modelBuilder.Entity<ActorEntity>();
        actor.ToTable("actors", table =>
        {
            table.HasCheckConstraint("ck_actors_status", "status IN ('Active', 'Suspended', 'Revoked')");
            table.HasCheckConstraint("ck_actors_id_nonempty", "length(btrim(actor_id)) > 0");
            table.HasCheckConstraint("ck_actors_expiry", "expires_at_utc IS NULL OR expires_at_utc > created_at_utc");
            table.HasCheckConstraint("ck_actors_lock_token_zero", "lock_token = 0");
        });
        ConfigureLockToken(actor);
        actor.HasKey(entity => entity.ActorId).HasName("pk_actors");
        actor.Property(entity => entity.ActorId).HasColumnName("actor_id").HasColumnType("text");
        actor.Property(entity => entity.Status).HasColumnName("status").HasConversion<string>().HasColumnType("text");
        actor.Property(entity => entity.ExpiresAtUtc).HasColumnName("expires_at_utc").HasColumnType("timestamp with time zone");
        actor.Property(entity => entity.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("timestamp with time zone");
        actor.Property(entity => entity.UpdatedAtUtc).HasColumnName("updated_at_utc").HasColumnType("timestamp with time zone");
        var accessState = modelBuilder.Entity<ActorAccessStateEntity>();
        accessState.ToTable("actor_access_states", table =>
        {
            table.HasCheckConstraint("ck_actor_access_states_epoch", "access_epoch >= 0");
            table.HasCheckConstraint("ck_actor_access_states_version", "version > 0");
            table.HasCheckConstraint("ck_actor_access_states_lock_token_zero", "lock_token = 0");
        });
        ConfigureLockToken(accessState);
        accessState.HasKey(entity => entity.ActorId).HasName("pk_actor_access_states");
        accessState.Property(entity => entity.ActorId).HasColumnName("actor_id").HasColumnType("text");
        accessState.Property(entity => entity.AccessEpoch).HasColumnName("access_epoch");
        accessState.Property(entity => entity.Version).HasColumnName("version").IsConcurrencyToken();
        accessState.Property(entity => entity.UpdatedAtUtc).HasColumnName("updated_at_utc").HasColumnType("timestamp with time zone");
        accessState.HasOne<ActorEntity>().WithOne().HasForeignKey<ActorAccessStateEntity>(entity => entity.ActorId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_actor_access_states_actor");

        var organizations = modelBuilder.Entity<OrganizationEntity>();
        organizations.ToTable("organizations", table =>
        {
            table.HasCheckConstraint("ck_organizations_status", "status IN ('Active', 'Suspended', 'Revoked')");
            table.HasCheckConstraint("ck_organizations_id_nonempty", "length(btrim(organization_id)) > 0");
            table.HasCheckConstraint("ck_organizations_lock_token_zero", "lock_token = 0");
        });
        ConfigureLockToken(organizations);
        organizations.HasKey(entity => entity.OrganizationId).HasName("pk_organizations");
        organizations.Property(entity => entity.OrganizationId).HasColumnName("organization_id").HasColumnType("text");
        organizations.Property(entity => entity.Status).HasColumnName("status").HasConversion<string>().HasColumnType("text");
        organizations.Property(entity => entity.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("timestamp with time zone");
        organizations.Property(entity => entity.UpdatedAtUtc).HasColumnName("updated_at_utc").HasColumnType("timestamp with time zone");
        var workspaces = modelBuilder.Entity<WorkspaceEntity>();
        workspaces.ToTable("workspaces", table =>
        {
            table.HasCheckConstraint("ck_workspaces_status", "status IN ('Active', 'Suspended', 'Revoked')");
            table.HasCheckConstraint("ck_workspaces_ids_nonempty", "length(btrim(workspace_id)) > 0 AND length(btrim(organization_id)) > 0");
            table.HasCheckConstraint("ck_workspaces_lock_token_zero", "lock_token = 0");
        });
        ConfigureLockToken(workspaces);
        workspaces.HasKey(entity => entity.WorkspaceId).HasName("pk_workspaces");
        workspaces.HasAlternateKey(entity => new { entity.WorkspaceId, entity.OrganizationId })
            .HasName("ak_workspaces_workspace_organization");
        workspaces.Property(entity => entity.WorkspaceId).HasColumnName("workspace_id").HasColumnType("text");
        workspaces.Property(entity => entity.OrganizationId).HasColumnName("organization_id").HasColumnType("text");
        workspaces.Property(entity => entity.Status).HasColumnName("status").HasConversion<string>().HasColumnType("text");
        workspaces.HasOne<OrganizationEntity>().WithMany().HasForeignKey(entity => entity.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_workspaces_organization");

        var links = modelBuilder.Entity<WorkspaceOrganizationEntity>();
        links.ToTable("workspace_organizations", table =>
            table.HasCheckConstraint("ck_workspace_organizations_status", "status IN ('Active', 'Suspended', 'Revoked')"));
        links.HasKey(entity => new { entity.WorkspaceId, entity.OrganizationId }).HasName("pk_workspace_organizations");
        links.Property(entity => entity.WorkspaceId).HasColumnName("workspace_id").HasColumnType("text");
        links.Property(entity => entity.OrganizationId).HasColumnName("organization_id").HasColumnType("text");
        links.Property(entity => entity.Status).HasColumnName("status").HasConversion<string>().HasColumnType("text");
        links.Property(entity => entity.CreatedAtUtc).HasColumnName("created_at_utc");
        links.Property(entity => entity.UpdatedAtUtc).HasColumnName("updated_at_utc");
        links.HasIndex(entity => new { entity.OrganizationId, entity.WorkspaceId });
        links.HasOne<WorkspaceEntity>().WithMany().HasForeignKey(entity => entity.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        links.HasOne<OrganizationEntity>().WithMany().HasForeignKey(entity => entity.OrganizationId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureOrganizationsAndAccess(ModelBuilder modelBuilder)
    {
        var membership = modelBuilder.Entity<OrganizationMembershipEntity>();
        membership.ToTable("organization_memberships", table =>
        {
            table.HasCheckConstraint("ck_memberships_status", "status IN ('Active', 'Suspended', 'Revoked')");
            table.HasCheckConstraint("ck_memberships_validity", "expires_at_utc IS NULL OR expires_at_utc > valid_from_utc");
            table.HasCheckConstraint("ck_memberships_version", "version > 0");
            table.HasCheckConstraint("ck_memberships_ids_nonempty", "length(btrim(actor_id)) > 0 AND length(btrim(organization_id)) > 0 AND length(btrim(granted_by_actor_id)) > 0");
            table.HasCheckConstraint("ck_organization_memberships_lock_token_zero", "lock_token = 0");
        });
        ConfigureLockToken(membership);
        membership.HasKey(entity => entity.MembershipId).HasName("pk_organization_memberships");
        membership.HasAlternateKey(entity => new { entity.MembershipId, entity.ActorId, entity.OrganizationId })
            .HasName("ak_memberships_id_actor_organization");
        membership.HasIndex(entity => new { entity.ActorId, entity.OrganizationId }).IsUnique()
            .HasDatabaseName("ux_memberships_actor_organization");
        membership.HasIndex(entity => new { entity.ActorId, entity.Status, entity.ExpiresAtUtc })
            .HasDatabaseName("ix_memberships_actor_status_expiry");
        membership.Property(entity => entity.MembershipId).HasColumnName("membership_id");
        membership.Property(entity => entity.ActorId).HasColumnName("actor_id").HasColumnType("text");
        membership.Property(entity => entity.OrganizationId).HasColumnName("organization_id").HasColumnType("text");
        membership.Property(entity => entity.Status).HasColumnName("status").HasConversion<string>().HasColumnType("text");
        membership.Property(entity => entity.ValidFromUtc).HasColumnName("valid_from_utc").HasColumnType("timestamp with time zone");
        membership.Property(entity => entity.ExpiresAtUtc).HasColumnName("expires_at_utc").HasColumnType("timestamp with time zone");
        membership.Property(entity => entity.GrantedByActorId).HasColumnName("granted_by_actor_id").HasColumnType("text");
        membership.Property(entity => entity.RevokedByActorId).HasColumnName("revoked_by_actor_id").HasColumnType("text");
        membership.Property(entity => entity.ChangeReason).HasColumnName("change_reason").HasColumnType("text");
        membership.Property(entity => entity.Version).HasColumnName("version").IsConcurrencyToken();
        membership.HasOne<ActorEntity>().WithMany().HasForeignKey(entity => entity.ActorId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_memberships_actor");
        membership.HasOne<OrganizationEntity>().WithMany().HasForeignKey(entity => entity.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_memberships_organization");
        membership.HasOne<ActorEntity>().WithMany().HasForeignKey(entity => entity.GrantedByActorId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_memberships_grantor");
        membership.HasOne<ActorEntity>().WithMany().HasForeignKey(entity => entity.RevokedByActorId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_memberships_revoker");

        var joinRequest = modelBuilder.Entity<MembershipRequestEntity>();
        joinRequest.ToTable("membership_requests", table =>
        {
            table.HasCheckConstraint("ck_membership_requests_status", "status IN ('Pending', 'Approved', 'Declined')");
            table.HasCheckConstraint("ck_membership_requests_ids_nonempty", "length(btrim(actor_id)) > 0 AND length(btrim(organization_id)) > 0");
        });
        joinRequest.HasKey(entity => entity.RequestId).HasName("pk_membership_requests");
        joinRequest.HasIndex(entity => new { entity.ActorId, entity.OrganizationId, entity.Status })
            .HasDatabaseName("ix_membership_requests_actor_organization_status");
        joinRequest.Property(entity => entity.RequestId).HasColumnName("request_id");
        joinRequest.Property(entity => entity.ActorId).HasColumnName("actor_id").HasColumnType("text");
        joinRequest.Property(entity => entity.OrganizationId).HasColumnName("organization_id").HasColumnType("text");
        joinRequest.Property(entity => entity.Status).HasColumnName("status").HasConversion<string>().HasColumnType("text");
        joinRequest.Property(entity => entity.Reason).HasColumnName("reason").HasColumnType("text");
        joinRequest.Property(entity => entity.InvitationId).HasColumnName("invitation_id").HasColumnType("uuid");
        joinRequest.Property(entity => entity.CreatedAtUtc).HasColumnName("created_at_utc")
            .HasColumnType("timestamp with time zone");
        joinRequest.Property(entity => entity.UpdatedAtUtc).HasColumnName("updated_at_utc")
            .HasColumnType("timestamp with time zone");
        joinRequest.HasOne<ActorEntity>().WithMany().HasForeignKey(entity => entity.ActorId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_membership_requests_actor");
        joinRequest.HasOne<OrganizationEntity>().WithMany().HasForeignKey(entity => entity.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_membership_requests_organization");

        var invitation = modelBuilder.Entity<OrganizationInvitationEntity>();
        invitation.ToTable("organization_invitations", table =>
        {
            table.HasCheckConstraint("ck_invitations_status", "status IN ('Active', 'Revoked', 'Exhausted', 'Expired')");
            table.HasCheckConstraint("ck_invitations_ids_nonempty", "length(btrim(organization_id)) > 0 AND length(btrim(code_hash)) > 0");
            table.HasCheckConstraint("ck_invitations_uses_nonnegative", "use_count >= 0 AND max_uses > 0 AND use_count <= max_uses");
        });
        invitation.HasKey(entity => entity.InvitationId).HasName("pk_organization_invitations");
        invitation.HasIndex(entity => entity.CodeHash).IsUnique()
            .HasDatabaseName("ux_organization_invitations_code_hash");
        invitation.Property(entity => entity.InvitationId).HasColumnName("invitation_id");
        invitation.Property(entity => entity.OrganizationId).HasColumnName("organization_id").HasColumnType("text");
        invitation.Property(entity => entity.CodeHash).HasColumnName("code_hash").HasColumnType("text");
        invitation.Property(entity => entity.Status).HasColumnName("status").HasColumnType("text");
        invitation.Property(entity => entity.ExpiresAtUtc).HasColumnName("expires_at_utc")
            .HasColumnType("timestamp with time zone");
        invitation.Property(entity => entity.MaxUses).HasColumnName("max_uses");
        invitation.Property(entity => entity.UseCount).HasColumnName("use_count");
        invitation.Property(entity => entity.CreatedByActorId).HasColumnName("created_by_actor_id").HasColumnType("text");
        invitation.Property(entity => entity.CreatedAtUtc).HasColumnName("created_at_utc")
            .HasColumnType("timestamp with time zone");
        invitation.HasOne<OrganizationEntity>().WithMany().HasForeignKey(entity => entity.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_invitations_organization");

        var preferences = modelBuilder.Entity<NotificationPreferenceEntity>();
        preferences.ToTable("notification_preferences", "swiftaid_identity");
        preferences.HasKey(entity => entity.UserId).HasName("pk_notification_preferences");
        preferences.Property(entity => entity.UserId).HasColumnName("user_id").HasColumnType("text");
        preferences.Property(entity => entity.WorkReminders).HasColumnName("work_reminders");
        preferences.Property(entity => entity.SessionUpdates).HasColumnName("session_updates");
        preferences.Property(entity => entity.UpdatedAtUtc).HasColumnName("updated_at_utc")
            .HasColumnType("timestamp with time zone");
        preferences.HasOne<SwiftAidUser>().WithMany().HasForeignKey(entity => entity.UserId)
            .OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_notification_preferences_user");

        var grant = modelBuilder.Entity<AccessGrantGroupEntity>();
        grant.ToTable("access_grant_groups", table =>
        {
            table.HasCheckConstraint("ck_access_grant_groups_status", "status IN ('Active', 'Suspended', 'Revoked')");
            table.HasCheckConstraint("ck_access_grant_groups_sector", "sector IN ('HumanHealth', 'AnimalHealth')");
            table.HasCheckConstraint("ck_access_grant_groups_validity", "expires_at_utc > valid_from_utc");
            table.HasCheckConstraint("ck_access_grant_groups_version", "version > 0");
            table.HasCheckConstraint("ck_access_grant_groups_ids_nonempty", "length(btrim(grant_group_id)) > 0 AND length(btrim(actor_id)) > 0 AND length(btrim(organization_id)) > 0 AND length(btrim(workspace_id)) > 0 AND length(btrim(country_code)) > 0 AND length(btrim(sensitivity_code)) > 0 AND length(btrim(granted_by_actor_id)) > 0");
            table.HasCheckConstraint("ck_access_grant_groups_lock_token_zero", "lock_token = 0");
        });
        ConfigureLockToken(grant);
        grant.HasKey(entity => entity.GrantGroupId).HasName("pk_access_grant_groups");
        grant.HasIndex(entity => new { entity.ActorId, entity.Status, entity.ExpiresAtUtc })
            .HasDatabaseName("ix_access_grant_groups_actor_status_expiry");
        grant.HasIndex(entity => new { entity.MembershipId, entity.OrganizationId, entity.WorkspaceId })
            .HasDatabaseName("ix_access_grant_groups_membership_scope");
        grant.Property(entity => entity.GrantGroupId).HasColumnName("grant_group_id").HasColumnType("text");
        grant.Property(entity => entity.MembershipId).HasColumnName("membership_id");
        grant.Property(entity => entity.ActorId).HasColumnName("actor_id").HasColumnType("text");
        grant.Property(entity => entity.OrganizationId).HasColumnName("organization_id").HasColumnType("text");
        grant.Property(entity => entity.WorkspaceId).HasColumnName("workspace_id").HasColumnType("text");
        grant.Property(entity => entity.CountryCode).HasColumnName("country_code").HasColumnType("text");
        grant.Property(entity => entity.Sector).HasColumnName("sector").HasConversion<string>().HasColumnType("text");
        grant.Property(entity => entity.SensitivityCode).HasColumnName("sensitivity_code").HasColumnType("text");
        grant.Property(entity => entity.CanAccessJurisdictionalCases).HasColumnName("can_access_jurisdictional_cases");
        grant.Property(entity => entity.Status).HasColumnName("status").HasConversion<string>().HasColumnType("text");
        grant.Property(entity => entity.ValidFromUtc).HasColumnName("valid_from_utc").HasColumnType("timestamp with time zone");
        grant.Property(entity => entity.ExpiresAtUtc).HasColumnName("expires_at_utc").HasColumnType("timestamp with time zone");
        grant.Property(entity => entity.GrantedByActorId).HasColumnName("granted_by_actor_id").HasColumnType("text");
        grant.Property(entity => entity.RevokedByActorId).HasColumnName("revoked_by_actor_id").HasColumnType("text");
        grant.Property(entity => entity.ChangeReason).HasColumnName("change_reason").HasColumnType("text");
        grant.Property(entity => entity.Version).HasColumnName("version").IsConcurrencyToken();
        grant.HasOne<OrganizationMembershipEntity>().WithMany()
            .HasForeignKey(entity => new { entity.MembershipId, entity.ActorId, entity.OrganizationId })
            .HasPrincipalKey(entity => new { entity.MembershipId, entity.ActorId, entity.OrganizationId })
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_access_grant_groups_membership_scope");
        grant.HasOne<WorkspaceOrganizationEntity>().WithMany()
            .HasForeignKey(entity => new { entity.WorkspaceId, entity.OrganizationId })
            .HasPrincipalKey(entity => new { entity.WorkspaceId, entity.OrganizationId })
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_access_grant_groups_workspace_scope");
        grant.HasOne<ActorEntity>().WithMany().HasForeignKey(entity => entity.ActorId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_access_grant_groups_actor");
        grant.HasOne<ActorEntity>().WithMany().HasForeignKey(entity => entity.GrantedByActorId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_access_grant_groups_grantor");
        grant.HasOne<ActorEntity>().WithMany().HasForeignKey(entity => entity.RevokedByActorId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_access_grant_groups_revoker");

        var grantRegion = modelBuilder.Entity<AccessGrantRegionEntity>();
        grantRegion.ToTable("access_grant_group_regions", table =>
        {
            table.HasCheckConstraint("ck_access_grant_group_regions_id_nonempty", "length(btrim(region_id)) > 0");
            table.HasCheckConstraint("ck_access_grant_group_regions_lock_token_zero", "lock_token = 0");
        });
        ConfigureLockToken(grantRegion);
        grantRegion.HasKey(entity => new { entity.GrantGroupId, entity.RegionId }).HasName("pk_access_grant_group_regions");
        grantRegion.HasIndex(entity => new { entity.RegionId, entity.GrantGroupId }).HasDatabaseName("ix_access_grant_group_regions_region");
        grantRegion.Property(entity => entity.GrantGroupId).HasColumnName("grant_group_id").HasColumnType("text");
        grantRegion.Property(entity => entity.RegionId).HasColumnName("region_id").HasColumnType("text");
        grantRegion.HasOne<AccessGrantGroupEntity>().WithMany().HasForeignKey(entity => entity.GrantGroupId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_access_grant_group_regions_group");

        var grantOperation = modelBuilder.Entity<AccessGrantOperationEntity>();
        grantOperation.ToTable("access_grant_group_operations", table =>
        {
            table.HasCheckConstraint("ck_grant_group_operations_value", "operation IN ('ReadCase', 'ReadAttachment', 'Synchronize', 'Search', 'Export', 'Count', 'RequestProtectedAction', 'ExecuteProtectedAction', 'ReadLinkedSignalEvidence', 'CorrectReport', 'ReviewProtectedAction', 'AssignCase', 'ResolveCase', 'ReopenCase', 'ReviewIntake', 'CoordinateIntake', 'RecordObservation', 'ManageFollowUp', 'ManageEscalation')");
            table.HasCheckConstraint("ck_access_grant_group_operations_lock_token_zero", "lock_token = 0");
        });
        ConfigureLockToken(grantOperation);
        grantOperation.HasKey(entity => new { entity.GrantGroupId, entity.Operation }).HasName("pk_access_grant_group_operations");
        grantOperation.HasIndex(entity => new { entity.Operation, entity.GrantGroupId }).HasDatabaseName("ix_grant_group_operations_value");
        grantOperation.Property(entity => entity.GrantGroupId).HasColumnName("grant_group_id").HasColumnType("text");
        grantOperation.Property(entity => entity.Operation).HasColumnName("operation").HasConversion<string>().HasColumnType("text");
        grantOperation.HasOne<AccessGrantGroupEntity>().WithMany().HasForeignKey(entity => entity.GrantGroupId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_access_grant_group_operations_group");
    }

    private static void ConfigureCases(ModelBuilder modelBuilder)
    {
        var managedCase = modelBuilder.Entity<ManagedCaseEntity>();
        managedCase.ToTable("managed_cases", table =>
        {
            table.HasCheckConstraint("ck_managed_cases_kind", "kind IN ('Human', 'Animal')");
            table.HasCheckConstraint("ck_managed_cases_sector", "sector IN ('HumanHealth', 'AnimalHealth')");
            table.HasCheckConstraint("ck_managed_cases_kind_sector", "(kind = 'Human' AND sector = 'HumanHealth') OR (kind = 'Animal' AND sector = 'AnimalHealth')");
            table.HasCheckConstraint("ck_managed_cases_versions", "resource_version > 0 AND aggregate_version >= 2 AND report_revision > 0 AND report_revision <= aggregate_version AND workflow_version >= 0 AND classification_version >= 0 AND laboratory_version >= 0 AND outcome_version >= 0");
            table.HasCheckConstraint("ck_managed_cases_workflow", "workflow IN ('Reported', 'UnderReview', 'Active', 'AwaitingFollowUp', 'Resolved')");
            table.HasCheckConstraint("ck_managed_cases_classification", "classification IN ('Unclassified', 'Suspected', 'Probable', 'Confirmed', 'RuledOut')");
            table.HasCheckConstraint("ck_managed_cases_laboratory", "laboratory IN ('NotRecorded', 'Requested', 'Collected', 'Pending', 'ResultRecorded')");
            table.HasCheckConstraint("ck_managed_cases_outcome_state", "outcome_state IN ('Unknown', 'NotRecorded', 'NotApplicable', 'Absent', 'Known')");
            table.HasCheckConstraint("ck_managed_cases_outcome_value", "(outcome_state = 'Known' AND outcome_value IS NOT NULL) OR (outcome_state <> 'Known' AND outcome_value IS NULL)");
            table.HasCheckConstraint("ck_managed_cases_ids_nonempty", "length(btrim(organization_id)) > 0 AND length(btrim(workspace_id)) > 0 AND length(btrim(country_code)) > 0 AND length(btrim(sensitivity_code)) > 0 AND length(btrim(accountable_owner_actor_id)) > 0");
            table.HasCheckConstraint("ck_managed_cases_lock_token_zero", "lock_token = 0");
        });
        ConfigureLockToken(managedCase);
        managedCase.HasKey(entity => entity.CaseId).HasName("pk_managed_cases");
        managedCase.HasAlternateKey(entity => new { entity.CaseId, entity.OrganizationId })
            .HasName("ak_managed_cases_id_organization");
        managedCase.HasIndex(entity => new { entity.AccountableOwnerActorId, entity.CaseId })
            .HasDatabaseName("ix_managed_cases_owner");
        managedCase.HasIndex(entity => new { entity.OrganizationId, entity.WorkspaceId, entity.CountryCode, entity.Sector, entity.SensitivityCode })
            .HasDatabaseName("ix_managed_cases_scope");
        managedCase.Property(entity => entity.CaseId).HasColumnName("case_id");
        managedCase.Property(entity => entity.OrganizationId).HasColumnName("organization_id").HasColumnType("text");
        managedCase.Property(entity => entity.WorkspaceId).HasColumnName("workspace_id").HasColumnType("text");
        managedCase.Property(entity => entity.CountryCode).HasColumnName("country_code").HasColumnType("text");
        managedCase.Property(entity => entity.Kind).HasColumnName("kind").HasConversion<string>().HasColumnType("text");
        managedCase.Property(entity => entity.Sector).HasColumnName("sector").HasConversion<string>().HasColumnType("text");
        managedCase.Property(entity => entity.SensitivityCode).HasColumnName("sensitivity_code").HasColumnType("text");
        managedCase.Property(entity => entity.AccountableOwnerActorId).HasColumnName("accountable_owner_actor_id").HasColumnType("text");
        managedCase.Property(entity => entity.ResourceVersion).HasColumnName("resource_version").IsConcurrencyToken();
        managedCase.Property(entity => entity.AggregateVersion).HasColumnName("aggregate_version").IsConcurrencyToken();
        managedCase.Property(entity => entity.ReportRevision).HasColumnName("report_revision");
        managedCase.Property(entity => entity.Workflow).HasColumnName("workflow").HasConversion<string>().HasColumnType("text");
        managedCase.Property(entity => entity.WorkflowVersion).HasColumnName("workflow_version");
        managedCase.Property(entity => entity.Classification).HasColumnName("classification").HasConversion<string>().HasColumnType("text");
        managedCase.Property(entity => entity.ClassificationVersion).HasColumnName("classification_version");
        managedCase.Property(entity => entity.Laboratory).HasColumnName("laboratory").HasConversion<string>().HasColumnType("text");
        managedCase.Property(entity => entity.LaboratoryVersion).HasColumnName("laboratory_version");
        managedCase.Property(entity => entity.OutcomeState).HasColumnName("outcome_state").HasConversion<string>().HasColumnType("text");
        managedCase.Property(entity => entity.OutcomeValue).HasColumnName("outcome_value").HasColumnType("text");
        managedCase.Property(entity => entity.OutcomeVersion).HasColumnName("outcome_version");
        managedCase.Property(entity => entity.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("timestamp with time zone");
        managedCase.Property(entity => entity.UpdatedAtUtc).HasColumnName("updated_at_utc").HasColumnType("timestamp with time zone");
        managedCase.HasOne<WorkspaceOrganizationEntity>().WithMany()
            .HasForeignKey(entity => new { entity.WorkspaceId, entity.OrganizationId })
            .HasPrincipalKey(entity => new { entity.WorkspaceId, entity.OrganizationId })
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_managed_cases_workspace_scope");
        managedCase.HasOne<ActorEntity>().WithMany().HasForeignKey(entity => entity.AccountableOwnerActorId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_managed_cases_accountable_owner");

        var caseRegion = modelBuilder.Entity<ManagedCaseRegionEntity>();
        caseRegion.ToTable("managed_case_regions", table =>
        {
            table.HasCheckConstraint("ck_managed_case_regions_id_nonempty", "length(btrim(region_id)) > 0");
            table.HasCheckConstraint("ck_managed_case_regions_lock_token_zero", "lock_token = 0");
        });
        ConfigureLockToken(caseRegion);
        caseRegion.HasKey(entity => new { entity.CaseId, entity.RegionId }).HasName("pk_managed_case_regions");
        caseRegion.HasIndex(entity => new { entity.RegionId, entity.CaseId }).HasDatabaseName("ix_managed_case_regions_region");
        caseRegion.Property(entity => entity.CaseId).HasColumnName("case_id");
        caseRegion.Property(entity => entity.RegionId).HasColumnName("region_id").HasColumnType("text");
        caseRegion.HasOne<ManagedCaseEntity>().WithMany().HasForeignKey(entity => entity.CaseId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_managed_case_regions_case");

        var share = modelBuilder.Entity<CaseShareEntity>();
        share.ToTable("case_shares", table =>
        {
            table.HasCheckConstraint("ck_case_shares_expiry", "expires_at_utc IS NULL OR expires_at_utc > granted_at_utc");
            table.HasCheckConstraint("ck_case_shares_grantor", "length(btrim(granted_by_actor_id)) > 0");
            table.HasCheckConstraint("ck_case_shares_version", "version > 0");
            table.HasCheckConstraint("ck_case_shares_lock_token_zero", "lock_token = 0");
        });
        ConfigureLockToken(share);
        share.HasKey(entity => new { entity.CaseId, entity.ActorId }).HasName("pk_case_shares");
        share.HasIndex(entity => new { entity.ActorId, entity.IsActive, entity.ExpiresAtUtc })
            .HasDatabaseName("ix_case_shares_actor_status_expiry");
        share.Property(entity => entity.CaseId).HasColumnName("case_id");
        share.Property(entity => entity.ActorId).HasColumnName("actor_id").HasColumnType("text");
        share.Property(entity => entity.Version).HasColumnName("version").IsConcurrencyToken();
        share.Property(entity => entity.IsActive).HasColumnName("is_active");
        share.Property(entity => entity.GrantedByActorId).HasColumnName("granted_by_actor_id").HasColumnType("text");
        share.Property(entity => entity.RevokedByActorId).HasColumnName("revoked_by_actor_id").HasColumnType("text");
        share.Property(entity => entity.ChangeReason).HasColumnName("change_reason").HasColumnType("text");
        share.Property(entity => entity.GrantedAtUtc).HasColumnName("granted_at_utc").HasColumnType("timestamp with time zone");
        share.Property(entity => entity.ExpiresAtUtc).HasColumnName("expires_at_utc").HasColumnType("timestamp with time zone");
        share.HasOne<ManagedCaseEntity>().WithMany().HasForeignKey(entity => entity.CaseId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_case_shares_case");
        share.HasOne<ActorEntity>().WithMany().HasForeignKey(entity => entity.ActorId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_case_shares_actor");
        share.HasOne<ActorEntity>().WithMany().HasForeignKey(entity => entity.GrantedByActorId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_case_shares_grantor");
        share.HasOne<ActorEntity>().WithMany().HasForeignKey(entity => entity.RevokedByActorId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_case_shares_revoker");
    }

    private static void ConfigureEventsAndCommands(ModelBuilder modelBuilder)
    {
        var payload = modelBuilder.Entity<ProtectedEventPayloadEntity>();
        payload.ToTable("protected_event_payloads", table =>
        {
            table.HasCheckConstraint("ck_protected_event_payloads_bytes", "octet_length(protected_bytes) > 0");
            table.HasCheckConstraint("ck_protected_event_payloads_purpose", "length(btrim(protection_purpose_version)) > 0");
        });
        payload.HasKey(entity => entity.PayloadId).HasName("pk_protected_event_payloads");
        payload.Property(entity => entity.PayloadId).HasColumnName("payload_id");
        payload.Property(entity => entity.ProtectedBytes).HasColumnName("protected_bytes").HasColumnType("bytea");
        payload.Property(entity => entity.ProtectionPurposeVersion).HasColumnName("protection_purpose_version").HasColumnType("text");
        payload.Property(entity => entity.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("timestamp with time zone");

        var receipt = modelBuilder.Entity<CommandReceiptEntity>();
        receipt.ToTable("command_receipts", table =>
        {
            table.HasCheckConstraint("ck_command_receipts_hash", "octet_length(canonical_payload_hash) = 32");
            table.HasCheckConstraint("ck_command_receipts_versions", "schema_version > 0 AND first_aggregate_version > 0 AND last_aggregate_version >= first_aggregate_version AND report_revision > 0");
            table.HasCheckConstraint("ck_command_receipts_ids_nonempty", "length(btrim(actor_id)) > 0 AND length(btrim(command_type)) > 0");
        });
        receipt.HasKey(entity => entity.ReceiptId).HasName("pk_command_receipts");
        receipt.HasAlternateKey(entity => new { entity.ActorId, entity.CommandId, entity.CaseId })
            .HasName("ak_command_receipts_actor_command_case");
        receipt.HasIndex(entity => new { entity.ActorId, entity.CommandId }).IsUnique()
            .HasDatabaseName("ux_command_receipts_actor_command");
        receipt.HasIndex(entity => new { entity.CaseId, entity.AcceptedAtUtc })
            .HasDatabaseName("ix_command_receipts_case_accepted");
        receipt.Property(entity => entity.ReceiptId).HasColumnName("receipt_id");
        receipt.Property(entity => entity.ActorId).HasColumnName("actor_id").HasColumnType("text");
        receipt.Property(entity => entity.CommandId).HasColumnName("command_id");
        receipt.Property(entity => entity.CaseId).HasColumnName("case_id");
        receipt.Property(entity => entity.CommandType).HasColumnName("command_type").HasColumnType("text");
        receipt.Property(entity => entity.SchemaVersion).HasColumnName("schema_version");
        receipt.Property(entity => entity.CanonicalPayloadHash).HasColumnName("canonical_payload_hash").HasColumnType("bytea");
        receipt.Property(entity => entity.FirstAggregateVersion).HasColumnName("first_aggregate_version");
        receipt.Property(entity => entity.LastAggregateVersion).HasColumnName("last_aggregate_version");
        receipt.Property(entity => entity.ReportRevision).HasColumnName("report_revision");
        receipt.Property(entity => entity.AcceptedAtUtc).HasColumnName("accepted_at_utc").HasColumnType("timestamp with time zone");
        receipt.HasOne<ManagedCaseEntity>().WithMany().HasForeignKey(entity => entity.CaseId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_command_receipts_case");

        var caseEvent = modelBuilder.Entity<CaseEventEntity>();
        caseEvent.ToTable("case_events", table =>
        {
            table.HasCheckConstraint("ck_case_events_versions", "aggregate_version > 0 AND report_revision > 0 AND schema_version > 0");
            table.HasCheckConstraint("ck_case_events_type", "event_type IN ('CaseReported', 'CaseAssigned', 'CaseReportCorrected', 'CaseActionRequested', 'CaseActionRequestReviewed', 'CaseResolved', 'CaseReopened', 'CaseObservationRecorded', 'CaseFollowUpScheduled', 'CaseFollowUpOutcomeRecorded', 'CaseEscalationRaised', 'CaseEscalationAcknowledged', 'CaseEscalationClosed')");
            table.HasCheckConstraint("ck_case_events_actor", "length(btrim(actor_id)) > 0");
        });
        caseEvent.HasKey(entity => entity.EventId).HasName("pk_case_events");
        caseEvent.HasIndex(entity => new { entity.CaseId, entity.AggregateVersion }).IsUnique()
            .HasDatabaseName("ux_case_events_case_aggregate_version");
        caseEvent.HasIndex(entity => new { entity.CaseId, entity.CommandId })
            .HasDatabaseName("ix_case_events_case_command");
        caseEvent.Property(entity => entity.EventId).HasColumnName("event_id");
        caseEvent.Property(entity => entity.CaseId).HasColumnName("case_id");
        caseEvent.Property(entity => entity.AggregateVersion).HasColumnName("aggregate_version");
        caseEvent.Property(entity => entity.ReportRevision).HasColumnName("report_revision");
        caseEvent.Property(entity => entity.CommandId).HasColumnName("command_id");
        caseEvent.Property(entity => entity.ActorId).HasColumnName("actor_id").HasColumnType("text");
        caseEvent.Property(entity => entity.EventType).HasColumnName("event_type").HasColumnType("text");
        caseEvent.Property(entity => entity.SchemaVersion).HasColumnName("schema_version");
        caseEvent.Property(entity => entity.OccurredAtUtc).HasColumnName("occurred_at_utc").HasColumnType("timestamp with time zone");
        caseEvent.Property(entity => entity.RecordedAtUtc).HasColumnName("recorded_at_utc").HasColumnType("timestamp with time zone");
        caseEvent.Property(entity => entity.PayloadId).HasColumnName("payload_id");
        caseEvent.HasOne<ManagedCaseEntity>().WithMany().HasForeignKey(entity => entity.CaseId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_case_events_case");
        caseEvent.HasOne<ProtectedEventPayloadEntity>().WithMany().HasForeignKey(entity => entity.PayloadId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_case_events_payload");
        caseEvent.HasOne<CommandReceiptEntity>().WithMany()
            .HasForeignKey(entity => new { entity.ActorId, entity.CommandId, entity.CaseId })
            .HasPrincipalKey(entity => new { entity.ActorId, entity.CommandId, entity.CaseId })
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_case_events_accepted_receipt");

        var outbox = modelBuilder.Entity<DomainOutboxEntity>();
        outbox.ToTable("domain_outbox", table =>
        {
            table.HasCheckConstraint("ck_domain_outbox_attempt_count", "attempt_count >= 0");
            table.HasCheckConstraint("ck_domain_outbox_message_type", "length(btrim(message_type)) > 0");
            table.HasCheckConstraint("ck_domain_outbox_lease", "published_at_utc IS NULL OR lease_until_utc IS NULL");
            table.HasCheckConstraint("ck_domain_outbox_fence", "(lease_id IS NULL) = (lease_until_utc IS NULL) AND (exhausted_at_utc IS NULL OR (published_at_utc IS NULL AND lease_id IS NULL))");
        });
        outbox.HasKey(entity => entity.OutboxId).HasName("pk_domain_outbox");
        outbox.HasIndex(entity => entity.EventId).IsUnique().HasDatabaseName("ux_domain_outbox_event");
        outbox.HasIndex(entity => new { entity.AvailableAtUtc, entity.CreatedAtUtc })
            .HasFilter("published_at_utc IS NULL AND exhausted_at_utc IS NULL")
            .HasDatabaseName("ix_domain_outbox_pending");
        outbox.Property(entity => entity.OutboxId).HasColumnName("outbox_id");
        outbox.Property(entity => entity.EventId).HasColumnName("event_id");
        outbox.Property(entity => entity.MessageType).HasColumnName("message_type").HasColumnType("text");
        outbox.Property(entity => entity.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("timestamp with time zone");
        outbox.Property(entity => entity.AvailableAtUtc).HasColumnName("available_at_utc").HasColumnType("timestamp with time zone");
        outbox.Property(entity => entity.AttemptCount).HasColumnName("attempt_count");
        outbox.Property(entity => entity.LeaseUntilUtc).HasColumnName("lease_until_utc").HasColumnType("timestamp with time zone");
        outbox.Property(entity => entity.PublishedAtUtc).HasColumnName("published_at_utc").HasColumnType("timestamp with time zone");
        outbox.Property(entity => entity.LeaseId).HasColumnName("lease_id");
        outbox.Property(entity => entity.ExhaustedAtUtc).HasColumnName("exhausted_at_utc");
        outbox.Property(entity => entity.LastFailureCode).HasColumnName("last_failure_code").HasMaxLength(80);
        outbox.HasOne<CaseEventEntity>().WithMany().HasForeignKey(entity => entity.EventId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_domain_outbox_event");
    }

    private static void ConfigureAudit(ModelBuilder modelBuilder)
    {
        var audit = modelBuilder.Entity<SecurityAuditEntryEntity>();
        audit.ToTable("security_audit_entries", table =>
        {
            table.HasCheckConstraint("ck_security_audit_epoch", "access_epoch >= 0");
            table.HasCheckConstraint("ck_security_audit_actor", "length(btrim(actor_id)) > 0");
            table.HasCheckConstraint("ck_security_audit_operation", "length(btrim(operation)) > 0");
            table.HasCheckConstraint("ck_security_audit_decision", "length(btrim(decision_code)) > 0");
        });
        audit.HasKey(entity => entity.AuditId).HasName("pk_security_audit_entries");
        audit.HasIndex(entity => new { entity.ActorId, entity.RecordedAtUtc })
            .HasDatabaseName("ix_security_audit_actor_recorded");
        audit.HasIndex(entity => new { entity.ResourceId, entity.RecordedAtUtc })
            .HasDatabaseName("ix_security_audit_resource_recorded");
        audit.Property(entity => entity.AuditId).HasColumnName("audit_id");
        audit.Property(entity => entity.ActorId).HasColumnName("actor_id").HasColumnType("text");
        audit.Property(entity => entity.TargetActorId).HasColumnName("target_actor_id").HasColumnType("text");
        audit.Property(entity => entity.ResourceId).HasColumnName("resource_id");
        audit.Property(entity => entity.Operation).HasColumnName("operation").HasColumnType("text");
        audit.Property(entity => entity.DecisionCode).HasColumnName("decision_code").HasColumnType("text");
        audit.Property(entity => entity.AccessEpoch).HasColumnName("access_epoch");
        audit.Property(entity => entity.RecordedAtUtc).HasColumnName("recorded_at_utc").HasColumnType("timestamp with time zone");
        audit.Property(entity => entity.ReasonCode).HasColumnName("reason_code").HasColumnType("text");
    }

    private static void ConfigureLockToken<TEntity>(EntityTypeBuilder<TEntity> entity)
        where TEntity : class
    {
        entity.Property<short>("LockToken")
            .HasColumnName("lock_token")
            .HasColumnType("smallint")
            .HasDefaultValue((short)0);
    }
}
