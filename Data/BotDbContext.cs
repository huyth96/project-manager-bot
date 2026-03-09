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
    public DbSet<TaskEvent> TaskEvents => Set<TaskEvent>();
    public DbSet<MemberProfile> MemberProfiles => Set<MemberProfile>();
    public DbSet<MemberDailySignal> MemberDailySignals => Set<MemberDailySignal>();
    public DbSet<TopicMention> TopicMentions => Set<TopicMention>();
    public DbSet<DecisionLog> DecisionLogs => Set<DecisionLog>();
    public DbSet<RiskLog> RiskLogs => Set<RiskLog>();
    public DbSet<SprintDailySnapshot> SprintDailySnapshots => Set<SprintDailySnapshot>();
    public DbSet<ProjectRiskSnapshot> ProjectRiskSnapshots => Set<ProjectRiskSnapshot>();

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

        modelBuilder.Entity<TaskEvent>(entity =>
        {
            entity.Property(x => x.TaskType).HasConversion<string>();
            entity.Property(x => x.EventType).HasConversion<string>();
            entity.Property(x => x.FromStatus).HasConversion<string>();
            entity.Property(x => x.ToStatus).HasConversion<string>();
            entity.Property(x => x.TitleSnapshot).HasMaxLength(200);
            entity.Property(x => x.DescriptionSnapshot).HasMaxLength(1200);
            entity.Property(x => x.Summary).HasMaxLength(500);
            entity.Property(x => x.Source).HasMaxLength(64);

            entity.HasIndex(x => new { x.ProjectId, x.OccurredAtUtc });
            entity.HasIndex(x => new { x.ProjectId, x.LocalDate });
            entity.HasIndex(x => new { x.TaskItemId, x.OccurredAtUtc });
            entity.HasIndex(x => new { x.ProjectId, x.ActorDiscordId, x.OccurredAtUtc });

            entity.HasOne(x => x.Project)
                .WithMany()
                .HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MemberProfile>(entity =>
        {
            entity.Property(x => x.DisplayName).HasMaxLength(100);
            entity.Property(x => x.RoleSummary).HasMaxLength(64);
            entity.Property(x => x.SkillKeywordsJson).HasMaxLength(1200);
            entity.Property(x => x.DominantTopicsJson).HasMaxLength(1200);
            entity.Property(x => x.ActiveChannelsJson).HasMaxLength(1200);
            entity.Property(x => x.StandupSummary).HasMaxLength(500);
            entity.Property(x => x.CurrentFocusSummary).HasMaxLength(700);
            entity.Property(x => x.RecentOutputSummary).HasMaxLength(500);
            entity.Property(x => x.RiskSummary).HasMaxLength(500);
            entity.Property(x => x.EvidenceSummary).HasMaxLength(1000);

            entity.HasIndex(x => new { x.ProjectId, x.DiscordUserId }).IsUnique();
            entity.HasIndex(x => new { x.ProjectId, x.ReliabilityScore });

            entity.HasOne(x => x.Project)
                .WithMany()
                .HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MemberDailySignal>(entity =>
        {
            entity.Property(x => x.EvidenceJson).HasMaxLength(2000);

            entity.HasIndex(x => new { x.ProjectId, x.DiscordUserId, x.LocalDate }).IsUnique();
            entity.HasIndex(x => new { x.ProjectId, x.LocalDate });

            entity.HasOne(x => x.Project)
                .WithMany()
                .HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TopicMention>(entity =>
        {
            entity.Property(x => x.TopicKey).HasMaxLength(64);
            entity.Property(x => x.TopChannelsJson).HasMaxLength(1200);
            entity.Property(x => x.TopAuthorsJson).HasMaxLength(1200);
            entity.Property(x => x.SourceSummary).HasMaxLength(1200);
            entity.Property(x => x.EvidenceJson).HasMaxLength(2000);

            entity.HasIndex(x => new { x.ProjectId, x.LocalDate, x.TopicKey }).IsUnique();
            entity.HasIndex(x => new { x.ProjectId, x.TopicKey, x.LocalDate });

            entity.HasOne(x => x.Project)
                .WithMany()
                .HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DecisionLog>(entity =>
        {
            entity.Property(x => x.TopicKey).HasMaxLength(64);
            entity.Property(x => x.Summary).HasMaxLength(500);
            entity.Property(x => x.Evidence).HasMaxLength(1000);
            entity.Property(x => x.SourceChannelName).HasMaxLength(100);

            entity.HasIndex(x => new { x.ProjectId, x.LocalDate });
            entity.HasIndex(x => new { x.ProjectId, x.TopicKey, x.LocalDate });

            entity.HasOne(x => x.Project)
                .WithMany()
                .HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RiskLog>(entity =>
        {
            entity.Property(x => x.RiskKey).HasMaxLength(96);
            entity.Property(x => x.Severity).HasMaxLength(24);
            entity.Property(x => x.Summary).HasMaxLength(500);
            entity.Property(x => x.Evidence).HasMaxLength(1000);

            entity.HasIndex(x => new { x.ProjectId, x.LocalDate });
            entity.HasIndex(x => new { x.ProjectId, x.RiskKey, x.LocalDate });

            entity.HasOne(x => x.Project)
                .WithMany()
                .HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SprintDailySnapshot>(entity =>
        {
            entity.Property(x => x.HealthLabel).HasMaxLength(32);

            entity.HasIndex(x => new { x.ProjectId, x.SprintId, x.LocalDate }).IsUnique();
            entity.HasIndex(x => new { x.ProjectId, x.LocalDate });

            entity.HasOne(x => x.Project)
                .WithMany()
                .HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProjectRiskSnapshot>(entity =>
        {
            entity.Property(x => x.Summary).HasMaxLength(500);

            entity.HasIndex(x => new { x.ProjectId, x.LocalDate }).IsUnique();

            entity.HasOne(x => x.Project)
                .WithMany()
                .HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
