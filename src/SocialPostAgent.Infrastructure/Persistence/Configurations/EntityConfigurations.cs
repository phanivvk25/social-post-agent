using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SocialPostAgent.Domain;
using SocialPostAgent.Domain.Entities;

namespace SocialPostAgent.Infrastructure.Persistence.Configurations;

// This file groups every IEntityTypeConfiguration<T> for the whole model. Keeping them
// together (rather than scattered one-per-tiny-file) makes the full table shape of the
// system reviewable in one place, while OnModelCreating in the DbContext stays a single
// ApplyConfigurationsFromAssembly call regardless of how many entities are added later.
//
// Every enum column is stored as its string name (HasConversion<string>()) rather than
// its numeric value: it survives enum member reordering, and it is directly readable
// when someone inspects the table with psql instead of the application.

#region Post

/// <summary>
/// Fluent configuration for <see cref="Post"/> — the central aggregate. Column sizes are
/// deliberately generous for caption text (platform limits top out at ~3,000 characters
/// for LinkedIn) and the brief/notes fields are free text with no practical limit.
/// </summary>
public sealed class PostConfiguration : IEntityTypeConfiguration<Post>
{
    public void Configure(EntityTypeBuilder<Post> builder)
    {
        builder.ToTable("posts");

        builder.HasKey(p => p.Id);

        builder.Property(p => p.Id)
            .ValueGeneratedNever();

        builder.Property(p => p.Brief)
            .IsRequired()
            .HasMaxLength(4000);

        builder.Property(p => p.FacebookText)
            .HasMaxLength(63_206);

        builder.Property(p => p.InstagramText)
            .HasMaxLength(2200);

        builder.Property(p => p.LinkedInText)
            .HasMaxLength(3000);

        builder.Property(p => p.ImagePath)
            .HasMaxLength(2048);

        builder.Property(p => p.ImagePrompt)
            .HasMaxLength(4000);

        builder.Property(p => p.Mode)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(p => p.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(20);

        // Npgsql's EF Core provider maps List<string> to a native Postgres text[] column —
        // no join table, no JSON serialization, and it is queryable with array operators
        // if a future feature needs "posts targeting Instagram" style filtering.
        builder.Property(p => p.TargetPlatforms)
            .IsRequired()
            .HasColumnType("text[]");

        builder.Property(p => p.ReviewNotes)
            .HasMaxLength(4000);

        builder.HasIndex(p => p.Status);
        builder.HasIndex(p => p.CreatedAtUtc);
        builder.HasIndex(p => p.ScheduledAtUtc);

        builder.HasMany(p => p.PlatformResults)
            .WithOne(r => r.Post)
            .HasForeignKey(r => r.PostId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(p => p.GuardrailLogs)
            .WithOne(g => g.Post)
            .HasForeignKey(g => g.PostId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

#endregion

#region PlatformResult

/// <summary>Fluent configuration for <see cref="PlatformResult"/>.</summary>
public sealed class PlatformResultConfiguration : IEntityTypeConfiguration<PlatformResult>
{
    public void Configure(EntityTypeBuilder<PlatformResult> builder)
    {
        builder.ToTable("platform_results");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.Id)
            .ValueGeneratedNever();

        builder.Property(r => r.Platform)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(r => r.RemotePostId)
            .HasMaxLength(256);

        builder.Property(r => r.RemoteUrl)
            .HasMaxLength(2048);

        builder.Property(r => r.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(r => r.ErrorMessage)
            .HasMaxLength(4000);

        builder.HasIndex(r => new { r.PostId, r.Platform });
        builder.HasIndex(r => r.AttemptedAtUtc);
    }
}

#endregion

#region GuardrailLog

/// <summary>Fluent configuration for <see cref="GuardrailLog"/>.</summary>
public sealed class GuardrailLogConfiguration : IEntityTypeConfiguration<GuardrailLog>
{
    public void Configure(EntityTypeBuilder<GuardrailLog> builder)
    {
        builder.ToTable("guardrail_logs");

        builder.HasKey(g => g.Id);

        builder.Property(g => g.Id)
            .ValueGeneratedNever();

        builder.Property(g => g.RuleName)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(g => g.Severity)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(g => g.Message)
            .IsRequired()
            .HasMaxLength(2000);

        builder.HasIndex(g => g.PostId);
    }
}

#endregion

#region PlatformCredential

/// <summary>
/// Fluent configuration for <see cref="PlatformCredential"/>. There is exactly one
/// credential row per platform for this single-brand deployment, enforced with a
/// unique index rather than a business-logic check, so it holds even under concurrent
/// writes.
/// </summary>
public sealed class PlatformCredentialConfiguration : IEntityTypeConfiguration<PlatformCredential>
{
    public void Configure(EntityTypeBuilder<PlatformCredential> builder)
    {
        builder.ToTable("platform_credentials");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.Id)
            .ValueGeneratedNever();

        builder.Property(c => c.Platform)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(c => c.EncryptedAccessToken)
            .IsRequired()
            .HasMaxLength(4000);

        builder.Property(c => c.EncryptedRefreshToken)
            .HasMaxLength(4000);

        builder.Property(c => c.AccountId)
            .HasMaxLength(256);

        builder.HasIndex(c => c.Platform)
            .IsUnique();
    }
}

#endregion
