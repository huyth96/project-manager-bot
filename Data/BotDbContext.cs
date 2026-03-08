using Microsoft.EntityFrameworkCore;
using ProjectManagerBot.Models;

namespace ProjectManagerBot.Data;

public sealed class BotDbContext(DbContextOptions<BotDbContext> options) : DbContext(options)
{
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Sprint> Sprints => Set<Sprint>();
    public DbSet<TaskItem> TaskItems => Set<TaskItem>();
    public DbSet<User> Users => Set<User>();
    public DbSet<StandupReport> StandupReports => Set<StandupReport>();
    public DbSet<GitHubRepoBinding> GitHubRepoBindings => Set<GitHubRepoBinding>();
    public DbSet<ProjectMemoryMessage> ProjectMemoryMessages => Set<ProjectMemoryMessage>();
    public DbSet<ProjectDailyDigest> ProjectDailyDigests => Set<ProjectDailyDigest>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Project>(entity =>
        {
            entity.HasIndex(x => x.ChannelId).IsUnique();
            entity.HasIndex(x => x.BugChannelId);
            entity.HasIndex(x => x.StandupChannelId);
            entity.HasIndex(x => x.GitHubCommitsChannelId);
            entity.HasIndex(x => x.GlobalNotificationChannelId);
            entity.Property(x => x.Name).HasMaxLength(128);
        });

        modelBuilder.Entity<Sprint>(entity =>
        {
            entity.Property(x => x.Name).HasMaxLength(128);
            entity.Property(x => x.Goal).HasMaxLength(500);
            entity.HasOne(x => x.Project)
                .WithMany(x => x.Sprints)
                .HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TaskItem>(entity =>
        {
            entity.Property(x => x.Type).HasConversion<string>();
            entity.Property(x => x.Status).HasConversion<string>();
            entity.Property(x => x.Title).HasMaxLength(200);
            entity.Property(x => x.Description).HasMaxLength(2000);

            entity.HasIndex(x => new { x.ProjectId, x.Status });
            entity.HasIndex(x => new { x.ProjectId, x.SprintId });

            entity.HasOne(x => x.Project)
                .WithMany(x => x.TaskItems)
                .HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(x => x.Sprint)
                .WithMany(x => x.TaskItems)
                .HasForeignKey(x => x.SprintId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(x => x.DiscordId);
        });

        modelBuilder.Entity<StandupReport>(entity =>
        {
            entity.Property(x => x.Yesterday).HasMaxLength(1200);
            entity.Property(x => x.Today).HasMaxLength(1200);
            entity.Property(x => x.Blockers).HasMaxLength(1200);

            entity.HasIndex(x => new { x.ProjectId, x.LocalDate, x.DiscordUserId }).IsUnique();

            entity.HasOne(x => x.Project)
                .WithMany()
                .HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<GitHubRepoBinding>(entity =>
        {
            entity.Property(x => x.RepoFullName).HasMaxLength(200);
            entity.Property(x => x.Branch).HasMaxLength(100);
            entity.Property(x => x.LastSeenCommitSha).HasMaxLength(100);
            entity.HasIndex(x => new { x.ProjectId, x.RepoFullName, x.Branch }).IsUnique();
            entity.HasOne(x => x.Project)
                .WithMany()
                .HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProjectMemoryMessage>(entity =>
        {
            entity.Property(x => x.ChannelName).HasMaxLength(100);
            entity.Property(x => x.ThreadName).HasMaxLength(100);
            entity.Property(x => x.AuthorName).HasMaxLength(100);
            entity.Property(x => x.Content).HasMaxLength(2500);
            entity.Property(x => x.NormalizedContent).HasMaxLength(2500);

            entity.HasIndex(x => x.MessageId).IsUnique();
            entity.HasIndex(x => new { x.ProjectId, x.CreatedAtUtc });
            entity.HasIndex(x => new { x.ProjectId, x.LocalDate });
            entity.HasIndex(x => new { x.ProjectId, x.ChannelId, x.CreatedAtUtc });

            entity.HasOne(x => x.Project)
                .WithMany()
                .HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProjectDailyDigest>(entity =>
        {
            entity.Property(x => x.Summary).HasMaxLength(2000);
            entity.Property(x => x.KeywordsJson).HasMaxLength(1200);
            entity.Property(x => x.ActiveChannelsJson).HasMaxLength(1200);
            entity.Property(x => x.HighlightsJson).HasMaxLength(2000);

            entity.HasIndex(x => new { x.ProjectId, x.LocalDate }).IsUnique();
            entity.HasIndex(x => new { x.ProjectId, x.GeneratedAtUtc });

            entity.HasOne(x => x.Project)
                .WithMany()
                .HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
