using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SwiftAid.Infrastructure.Persistence.Ellai;

/// <summary>Durable usage-sample row: one per provider call. Totals derive
/// from row sums, so restarts and sibling instances converge. Sample ids
/// are unique per write; replays write a new row only when the provider is
/// actually called again.</summary>
public sealed class EllaiUsageSampleEntity
{
    public string SampleId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public decimal Dollars { get; set; }
    public DateTimeOffset AtUtc { get; set; }
    public int EstimatedPromptTokens { get; set; }
    public int EstimatedCompletionTokens { get; set; }
    public decimal EstimatedDollars { get; set; }
}

public sealed class EllaiUsageSampleEntityConfiguration : IEntityTypeConfiguration<EllaiUsageSampleEntity>
{
    public void Configure(EntityTypeBuilder<EllaiUsageSampleEntity> builder)
    {
        builder.ToTable("ellai_usage_samples", table =>
        {
            table.HasCheckConstraint(
                "ck_ellai_usage_samples_nonnegative",
                "prompt_tokens >= 0 AND completion_tokens >= 0 AND dollars >= 0");
            table.HasCheckConstraint(
                "ck_ellai_usage_estimates_nonnegative",
                "estimated_prompt_tokens >= 0 AND estimated_completion_tokens >= 0 AND estimated_dollars >= 0");
        });

        builder.HasKey(entity => entity.SampleId).HasName("pk_ellai_usage_samples");
        builder.HasIndex(entity => entity.SessionId).HasDatabaseName("ix_ellai_usage_samples_session");
        builder.Property(entity => entity.SampleId).HasColumnName("sample_id").HasColumnType("text");
        builder.Property(entity => entity.SessionId).HasColumnName("session_id").HasColumnType("text");
        builder.Property(entity => entity.PromptTokens).HasColumnName("prompt_tokens");
        builder.Property(entity => entity.CompletionTokens).HasColumnName("completion_tokens");
        builder.Property(entity => entity.Dollars).HasColumnName("dollars").HasColumnType("numeric");
        builder.Property(entity => entity.AtUtc).HasColumnName("at_utc").HasColumnType("timestamp with time zone");
        builder.Property(entity => entity.EstimatedPromptTokens).HasColumnName("estimated_prompt_tokens").HasDefaultValue(0);
        builder.Property(entity => entity.EstimatedCompletionTokens).HasColumnName("estimated_completion_tokens").HasDefaultValue(0);
        builder.Property(entity => entity.EstimatedDollars).HasColumnName("estimated_dollars").HasColumnType("numeric").HasDefaultValue(0m);
    }
}
