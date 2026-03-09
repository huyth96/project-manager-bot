using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Lavalink4NET.Extensions;
using Microsoft.EntityFrameworkCore;
using ProjectManagerBot.Data;
using ProjectManagerBot.Options;
using ProjectManagerBot.Services;

LoadDotEnvFromCommonPaths();

var builder = Host.CreateApplicationBuilder(args);
ApplyEnvironmentOverrides(builder.Configuration);

builder.Services.Configure<DiscordBotOptions>(builder.Configuration.GetSection("Discord"));
builder.Services.Configure<GitHubTrackingOptions>(builder.Configuration.GetSection("GitHub"));
builder.Services.Configure<LavalinkOptions>(builder.Configuration.GetSection("Lavalink"));
builder.Services.Configure<AssistantOptions>(builder.Configuration.GetSection("Assistant"));

builder.Services.AddDbContextFactory<BotDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("BotDb") ?? "Data Source=project-manager-bot.db";
    options.UseSqlite(connectionString);
});

builder.Services.AddSingleton(_ => new DiscordSocketClient(new DiscordSocketConfig
{
    GatewayIntents = GatewayIntents.Guilds |
                     GatewayIntents.GuildMembers |
                     GatewayIntents.GuildMessages |
                     GatewayIntents.MessageContent |
                     GatewayIntents.GuildMessageReactions |
                     GatewayIntents.GuildVoiceStates,
    AlwaysDownloadUsers = false,
    LogGatewayIntentWarnings = false
}));

builder.Services.AddSingleton(sp =>
{
    var socketClient = sp.GetRequiredService<DiscordSocketClient>();
    return new InteractionService(socketClient.Rest, new InteractionServiceConfig
    {
        LogLevel = LogSeverity.Info,
        UseCompiledLambda = true
    });
});

builder.Services.AddSingleton<StudioTimeService>();
builder.Services.AddSingleton<InitialSetupService>();
builder.Services.AddSingleton<ProjectService>();
builder.Services.AddSingleton<TaskEventService>();
builder.Services.AddSingleton<ProjectMemoryService>();
builder.Services.AddSingleton<ProjectKnowledgeService>();
builder.Services.AddSingleton<ProjectInsightService>();
builder.Services.AddSingleton<ProjectDailyLeadReportService>();
builder.Services.AddSingleton<ProjectWeeklyReviewService>();
builder.Services.AddSingleton<NotificationService>();
builder.Services.AddSingleton<BotAssistantService>();

builder.Services.AddLavalink();
builder.Services.ConfigureLavalink(options =>
{
    var config = builder.Configuration.GetSection("Lavalink").Get<LavalinkOptions>() ?? new LavalinkOptions();

    options.BaseAddress = new Uri(config.BaseAddress);
    options.WebSocketUri = new Uri(config.WebSocketUri);
    options.Passphrase = config.Passphrase;
    options.Label = config.Label;
    options.ReadyTimeout = TimeSpan.FromSeconds(Math.Max(1, config.ReadyTimeoutSeconds));
});
builder.Services.AddSingleton<YouTubeMusicService>();
builder.Services.AddHttpClient("GitHubTracking", client =>
{
    client.DefaultRequestHeaders.UserAgent.ParseAdd("ProjectManagerBot/1.0");
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddHttpClient("Assistant", client =>
{
    client.DefaultRequestHeaders.UserAgent.ParseAdd("ProjectManagerBot/1.0");
    client.Timeout = TimeSpan.FromSeconds(45);
});
builder.Services.AddSingleton<GitHubTrackingService>();

builder.Services.AddHostedService<DiscordBotService>();
builder.Services.AddHostedService<AutomationService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<GitHubTrackingService>());

var host = builder.Build();

await using (var scope = host.Services.CreateAsyncScope())
{
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<BotDbContext>>();
    await using var db = await dbFactory.CreateDbContextAsync();
    await db.Database.EnsureCreatedAsync();
    await EnsureColumnAsync(db, "Projects", "GlobalNotificationChannelId", "INTEGER NULL");
    await EnsureColumnAsync(db, "Projects", "GitHubCommitsChannelId", "INTEGER NULL");
    await EnsureColumnAsync(db, "Projects", "LastLeadReportDateLocal", "TEXT NULL");
    await EnsureColumnAsync(db, "Projects", "LastWeeklyReviewDateLocal", "TEXT NULL");
    await EnsureColumnAsync(db, "TaskItems", "LastOverdueReminderDateLocal", "TEXT NULL");
    await EnsureColumnAsync(db, "Sprints", "StartDateLocal", "TEXT NULL");
    await EnsureColumnAsync(db, "Sprints", "EndDateLocal", "TEXT NULL");
    await EnsureGitHubRepoBindingsTableAsync(db);
    await EnsureProjectMemoryMessagesTableAsync(db);
    await EnsureProjectDailyDigestsTableAsync(db);
    await EnsureTaskEventsTableAsync(db);
    await EnsureKnowledgeTablesAsync(db);
}

await host.RunAsync();

static void ApplyEnvironmentOverrides(ConfigurationManager configuration)
{
    var tokenFromEnv = Environment.GetEnvironmentVariable("DISCORD_BOT_TOKEN");
    if (!string.IsNullOrWhiteSpace(tokenFromEnv))
    {
        configuration["Discord:Token"] = tokenFromEnv.Trim();
    }

    var guildIdFromEnv = Environment.GetEnvironmentVariable("DISCORD_GUILD_ID");
    if (ulong.TryParse(guildIdFromEnv, out var guildId))
    {
        configuration["Discord:GuildId"] = guildId.ToString();
    }

    var registerCommandsFromEnv = Environment.GetEnvironmentVariable("DISCORD_REGISTER_COMMANDS_GLOBALLY");
    if (bool.TryParse(registerCommandsFromEnv, out var registerCommandsGlobally))
    {
        configuration["Discord:RegisterCommandsGlobally"] = registerCommandsGlobally.ToString();
    }

    var timeZoneIdFromEnv = Environment.GetEnvironmentVariable("DISCORD_TIME_ZONE_ID");
    if (!string.IsNullOrWhiteSpace(timeZoneIdFromEnv))
    {
        configuration["Discord:TimeZoneId"] = timeZoneIdFromEnv.Trim();
    }

    var botDbConnectionStringFromEnv = Environment.GetEnvironmentVariable("BOT_DB_CONNECTION_STRING");
    if (!string.IsNullOrWhiteSpace(botDbConnectionStringFromEnv))
    {
        configuration["ConnectionStrings:BotDb"] = botDbConnectionStringFromEnv.Trim();
    }

    var gitHubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
    if (!string.IsNullOrWhiteSpace(gitHubToken))
    {
        configuration["GitHub:Token"] = gitHubToken.Trim();
    }

    var gitHubPollIntervalRaw = Environment.GetEnvironmentVariable("GITHUB_POLL_INTERVAL_SECONDS");
    if (int.TryParse(gitHubPollIntervalRaw, out var gitHubPollInterval))
    {
        configuration["GitHub:PollIntervalSeconds"] = gitHubPollInterval.ToString();
    }

    var gitHubMaxCommitsPerPollRaw = Environment.GetEnvironmentVariable("GITHUB_MAX_COMMITS_PER_POLL");
    if (int.TryParse(gitHubMaxCommitsPerPollRaw, out var gitHubMaxCommitsPerPoll))
    {
        configuration["GitHub:MaxCommitsPerPoll"] = gitHubMaxCommitsPerPoll.ToString();
    }

    var gitHubEnabledRaw = Environment.GetEnvironmentVariable("GITHUB_TRACKING_ENABLED");
    if (bool.TryParse(gitHubEnabledRaw, out var gitHubEnabled))
    {
        configuration["GitHub:Enabled"] = gitHubEnabled.ToString();
    }

    var lavalinkBaseAddress = Environment.GetEnvironmentVariable("LAVALINK_BASE_ADDRESS");
    if (!string.IsNullOrWhiteSpace(lavalinkBaseAddress))
    {
        configuration["Lavalink:BaseAddress"] = lavalinkBaseAddress.Trim();
    }

    var lavalinkWebSocketUri = Environment.GetEnvironmentVariable("LAVALINK_WEBSOCKET_URI");
    if (!string.IsNullOrWhiteSpace(lavalinkWebSocketUri))
    {
        configuration["Lavalink:WebSocketUri"] = lavalinkWebSocketUri.Trim();
    }

    var lavalinkPassphrase = Environment.GetEnvironmentVariable("LAVALINK_PASSPHRASE");
    if (!string.IsNullOrWhiteSpace(lavalinkPassphrase))
    {
        configuration["Lavalink:Passphrase"] = lavalinkPassphrase.Trim();
    }

    var lavalinkLabel = Environment.GetEnvironmentVariable("LAVALINK_LABEL");
    if (!string.IsNullOrWhiteSpace(lavalinkLabel))
    {
        configuration["Lavalink:Label"] = lavalinkLabel.Trim();
    }

    var lavalinkReadyTimeoutRaw = Environment.GetEnvironmentVariable("LAVALINK_READY_TIMEOUT_SECONDS");
    if (int.TryParse(lavalinkReadyTimeoutRaw, out var lavalinkReadyTimeout))
    {
        configuration["Lavalink:ReadyTimeoutSeconds"] = lavalinkReadyTimeout.ToString();
    }

    var assistantEnabledRaw = Environment.GetEnvironmentVariable("ASSISTANT_ENABLED");
    if (bool.TryParse(assistantEnabledRaw, out var assistantEnabled))
    {
        configuration["Assistant:Enabled"] = assistantEnabled.ToString();
    }

    var assistantApiKey = Environment.GetEnvironmentVariable("ASSISTANT_API_KEY");
    if (!string.IsNullOrWhiteSpace(assistantApiKey))
    {
        configuration["Assistant:ApiKey"] = assistantApiKey.Trim();
    }

    var assistantBaseUrl = Environment.GetEnvironmentVariable("ASSISTANT_BASE_URL");
    if (!string.IsNullOrWhiteSpace(assistantBaseUrl))
    {
        configuration["Assistant:BaseUrl"] = assistantBaseUrl.Trim();
    }

    var assistantModel = Environment.GetEnvironmentVariable("ASSISTANT_MODEL");
    if (!string.IsNullOrWhiteSpace(assistantModel))
    {
        configuration["Assistant:Model"] = assistantModel.Trim();
    }

    var assistantTemperatureRaw = Environment.GetEnvironmentVariable("ASSISTANT_TEMPERATURE");
    if (double.TryParse(assistantTemperatureRaw, out var assistantTemperature))
    {
        configuration["Assistant:Temperature"] = assistantTemperature.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    var assistantMaxRecentMessagesRaw = Environment.GetEnvironmentVariable("ASSISTANT_MAX_RECENT_MESSAGES");
    if (int.TryParse(assistantMaxRecentMessagesRaw, out var assistantMaxRecentMessages))
    {
        configuration["Assistant:MaxRecentMessages"] = assistantMaxRecentMessages.ToString();
    }

    var assistantMaxStandupDaysRaw = Environment.GetEnvironmentVariable("ASSISTANT_MAX_STANDUP_DAYS");
    if (int.TryParse(assistantMaxStandupDaysRaw, out var assistantMaxStandupDays))
    {
        configuration["Assistant:MaxStandupDays"] = assistantMaxStandupDays.ToString();
    }

    var assistantMaxAttentionItemsRaw = Environment.GetEnvironmentVariable("ASSISTANT_MAX_ATTENTION_ITEMS");
    if (int.TryParse(assistantMaxAttentionItemsRaw, out var assistantMaxAttentionItems))
    {
        configuration["Assistant:MaxAttentionItems"] = assistantMaxAttentionItems.ToString();
    }

    var assistantMemoryLookbackDaysRaw = Environment.GetEnvironmentVariable("ASSISTANT_MEMORY_LOOKBACK_DAYS");
    if (int.TryParse(assistantMemoryLookbackDaysRaw, out var assistantMemoryLookbackDays))
    {
        configuration["Assistant:MemoryLookbackDays"] = assistantMemoryLookbackDays.ToString();
    }

    var assistantMemoryDigestDaysRaw = Environment.GetEnvironmentVariable("ASSISTANT_MEMORY_DIGEST_DAYS");
    if (int.TryParse(assistantMemoryDigestDaysRaw, out var assistantMemoryDigestDays))
    {
        configuration["Assistant:MemoryDigestDays"] = assistantMemoryDigestDays.ToString();
    }

    var assistantMaxRelevantMemoryMessagesRaw = Environment.GetEnvironmentVariable("ASSISTANT_MAX_RELEVANT_MEMORY_MESSAGES");
    if (int.TryParse(assistantMaxRelevantMemoryMessagesRaw, out var assistantMaxRelevantMemoryMessages))
    {
        configuration["Assistant:MaxRelevantMemoryMessages"] = assistantMaxRelevantMemoryMessages.ToString();
    }

    var assistantMaxDailyMemoryDigestsRaw = Environment.GetEnvironmentVariable("ASSISTANT_MAX_DAILY_MEMORY_DIGESTS");
    if (int.TryParse(assistantMaxDailyMemoryDigestsRaw, out var assistantMaxDailyMemoryDigests))
    {
        configuration["Assistant:MaxDailyMemoryDigests"] = assistantMaxDailyMemoryDigests.ToString();
    }
}

static void LoadDotEnvFromCommonPaths()
{
    var candidates = new[]
    {
        Path.Combine(Directory.GetCurrentDirectory(), ".env"),
        Path.Combine(AppContext.BaseDirectory, ".env")
    };

    foreach (var filePath in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
    {
        LoadDotEnv(filePath);
    }
}

static void LoadDotEnv(string filePath)
{
    if (!File.Exists(filePath))
    {
        return;
    }

    foreach (var rawLine in File.ReadAllLines(filePath))
    {
        var line = rawLine.Trim();
        if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
        {
            continue;
        }

        if (line.StartsWith("export ", StringComparison.OrdinalIgnoreCase))
        {
            line = line["export ".Length..].Trim();
        }

        var separatorIndex = line.IndexOf('=');
        if (separatorIndex <= 0)
        {
            continue;
        }

        var key = line[..separatorIndex].Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            continue;
        }

        var value = line[(separatorIndex + 1)..].Trim();
        if (value.Length >= 2 &&
            ((value.StartsWith('"') && value.EndsWith('"')) || (value.StartsWith('\'') && value.EndsWith('\''))))
        {
            value = value[1..^1];
        }

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(key)))
        {
            Environment.SetEnvironmentVariable(key, value);
        }
    }
}

static async Task EnsureColumnAsync(BotDbContext db, string table, string column, string sqlType)
{
    await using var connection = db.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open)
    {
        await connection.OpenAsync();
    }

    await using var cmd = connection.CreateCommand();
    cmd.CommandText = $"PRAGMA table_info('{table}');";

    var exists = false;
    await using (var reader = await cmd.ExecuteReaderAsync())
    {
        while (await reader.ReadAsync())
        {
            var columnName = reader.GetString(1);
            if (columnName.Equals(column, StringComparison.OrdinalIgnoreCase))
            {
                exists = true;
                break;
            }
        }
    }

    if (!exists)
    {
        var alterSql = (table, column, sqlType) switch
        {
            ("Projects", "GlobalNotificationChannelId", "INTEGER NULL") =>
                "ALTER TABLE \"Projects\" ADD COLUMN \"GlobalNotificationChannelId\" INTEGER NULL;",
            ("Projects", "GitHubCommitsChannelId", "INTEGER NULL") =>
                "ALTER TABLE \"Projects\" ADD COLUMN \"GitHubCommitsChannelId\" INTEGER NULL;",
            ("Projects", "LastLeadReportDateLocal", "TEXT NULL") =>
                "ALTER TABLE \"Projects\" ADD COLUMN \"LastLeadReportDateLocal\" TEXT NULL;",
            ("Projects", "LastWeeklyReviewDateLocal", "TEXT NULL") =>
                "ALTER TABLE \"Projects\" ADD COLUMN \"LastWeeklyReviewDateLocal\" TEXT NULL;",
            ("TaskItems", "LastOverdueReminderDateLocal", "TEXT NULL") =>
                "ALTER TABLE \"TaskItems\" ADD COLUMN \"LastOverdueReminderDateLocal\" TEXT NULL;",
            ("Sprints", "StartDateLocal", "TEXT NULL") =>
                "ALTER TABLE \"Sprints\" ADD COLUMN \"StartDateLocal\" TEXT NULL;",
            ("Sprints", "EndDateLocal", "TEXT NULL") =>
                "ALTER TABLE \"Sprints\" ADD COLUMN \"EndDateLocal\" TEXT NULL;",
            _ => throw new InvalidOperationException("Thao tác khởi tạo lược đồ không được hỗ trợ.")
        };

        await db.Database.ExecuteSqlRawAsync(alterSql);
    }
}

static async Task EnsureGitHubRepoBindingsTableAsync(BotDbContext db)
{
    await db.Database.ExecuteSqlRawAsync(
        "CREATE TABLE IF NOT EXISTS \"GitHubRepoBindings\" (" +
        "\"Id\" INTEGER NOT NULL CONSTRAINT \"PK_GitHubRepoBindings\" PRIMARY KEY AUTOINCREMENT, " +
        "\"ProjectId\" INTEGER NOT NULL, " +
        "\"RepoFullName\" TEXT NOT NULL, " +
        "\"Branch\" TEXT NOT NULL, " +
        "\"LastSeenCommitSha\" TEXT NULL, " +
        "\"IsEnabled\" INTEGER NOT NULL DEFAULT 1, " +
        "CONSTRAINT \"FK_GitHubRepoBindings_Projects_ProjectId\" FOREIGN KEY (\"ProjectId\") REFERENCES \"Projects\" (\"Id\") ON DELETE CASCADE);");

    await db.Database.ExecuteSqlRawAsync(
        "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_GitHubRepoBindings_ProjectId_RepoFullName_Branch\" " +
        "ON \"GitHubRepoBindings\" (\"ProjectId\", \"RepoFullName\", \"Branch\");");
}

static async Task EnsureProjectMemoryMessagesTableAsync(BotDbContext db)
{
    await db.Database.ExecuteSqlRawAsync(
        "CREATE TABLE IF NOT EXISTS \"ProjectMemoryMessages\" (" +
        "\"Id\" INTEGER NOT NULL CONSTRAINT \"PK_ProjectMemoryMessages\" PRIMARY KEY AUTOINCREMENT, " +
        "\"ProjectId\" INTEGER NOT NULL, " +
        "\"MessageId\" INTEGER NOT NULL, " +
        "\"ChannelId\" INTEGER NOT NULL, " +
        "\"ChannelName\" TEXT NOT NULL, " +
        "\"ThreadId\" INTEGER NULL, " +
        "\"ThreadName\" TEXT NULL, " +
        "\"AuthorId\" INTEGER NOT NULL, " +
        "\"AuthorName\" TEXT NOT NULL, " +
        "\"IsBot\" INTEGER NOT NULL, " +
        "\"CreatedAtUtc\" TEXT NOT NULL, " +
        "\"LocalDate\" TEXT NOT NULL, " +
        "\"Content\" TEXT NOT NULL, " +
        "\"NormalizedContent\" TEXT NOT NULL, " +
        "CONSTRAINT \"FK_ProjectMemoryMessages_Projects_ProjectId\" FOREIGN KEY (\"ProjectId\") REFERENCES \"Projects\" (\"Id\") ON DELETE CASCADE);");

    await db.Database.ExecuteSqlRawAsync(
        "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_ProjectMemoryMessages_MessageId\" " +
        "ON \"ProjectMemoryMessages\" (\"MessageId\");");

    await db.Database.ExecuteSqlRawAsync(
        "CREATE INDEX IF NOT EXISTS \"IX_ProjectMemoryMessages_ProjectId_CreatedAtUtc\" " +
        "ON \"ProjectMemoryMessages\" (\"ProjectId\", \"CreatedAtUtc\");");

    await db.Database.ExecuteSqlRawAsync(
        "CREATE INDEX IF NOT EXISTS \"IX_ProjectMemoryMessages_ProjectId_LocalDate\" " +
        "ON \"ProjectMemoryMessages\" (\"ProjectId\", \"LocalDate\");");

    await db.Database.ExecuteSqlRawAsync(
        "CREATE INDEX IF NOT EXISTS \"IX_ProjectMemoryMessages_ProjectId_ChannelId_CreatedAtUtc\" " +
        "ON \"ProjectMemoryMessages\" (\"ProjectId\", \"ChannelId\", \"CreatedAtUtc\");");
}

static async Task EnsureProjectDailyDigestsTableAsync(BotDbContext db)
{
    await db.Database.ExecuteSqlRawAsync(
        "CREATE TABLE IF NOT EXISTS \"ProjectDailyDigests\" (" +
        "\"Id\" INTEGER NOT NULL CONSTRAINT \"PK_ProjectDailyDigests\" PRIMARY KEY AUTOINCREMENT, " +
        "\"ProjectId\" INTEGER NOT NULL, " +
        "\"LocalDate\" TEXT NOT NULL, " +
        "\"MessageCount\" INTEGER NOT NULL, " +
        "\"DistinctAuthorCount\" INTEGER NOT NULL, " +
        "\"UserMessageCount\" INTEGER NOT NULL, " +
        "\"BotMessageCount\" INTEGER NOT NULL, " +
        "\"StandupReportCount\" INTEGER NOT NULL, " +
        "\"BlockerCount\" INTEGER NOT NULL, " +
        "\"Summary\" TEXT NOT NULL, " +
        "\"KeywordsJson\" TEXT NOT NULL, " +
        "\"ActiveChannelsJson\" TEXT NOT NULL, " +
        "\"HighlightsJson\" TEXT NOT NULL, " +
        "\"GeneratedAtUtc\" TEXT NOT NULL, " +
        "CONSTRAINT \"FK_ProjectDailyDigests_Projects_ProjectId\" FOREIGN KEY (\"ProjectId\") REFERENCES \"Projects\" (\"Id\") ON DELETE CASCADE);");

    await db.Database.ExecuteSqlRawAsync(
        "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_ProjectDailyDigests_ProjectId_LocalDate\" " +
        "ON \"ProjectDailyDigests\" (\"ProjectId\", \"LocalDate\");");

    await db.Database.ExecuteSqlRawAsync(
        "CREATE INDEX IF NOT EXISTS \"IX_ProjectDailyDigests_ProjectId_GeneratedAtUtc\" " +
        "ON \"ProjectDailyDigests\" (\"ProjectId\", \"GeneratedAtUtc\");");
}

static async Task EnsureTaskEventsTableAsync(BotDbContext db)
{
    await db.Database.ExecuteSqlRawAsync(
        "CREATE TABLE IF NOT EXISTS \"TaskEvents\" (" +
        "\"Id\" INTEGER NOT NULL CONSTRAINT \"PK_TaskEvents\" PRIMARY KEY AUTOINCREMENT, " +
        "\"ProjectId\" INTEGER NOT NULL, " +
        "\"TaskItemId\" INTEGER NOT NULL, " +
        "\"TaskType\" TEXT NOT NULL, " +
        "\"EventType\" TEXT NOT NULL, " +
        "\"ActorDiscordId\" INTEGER NULL, " +
        "\"OccurredAtUtc\" TEXT NOT NULL, " +
        "\"LocalDate\" TEXT NOT NULL, " +
        "\"TitleSnapshot\" TEXT NOT NULL, " +
        "\"DescriptionSnapshot\" TEXT NULL, " +
        "\"FromStatus\" TEXT NULL, " +
        "\"ToStatus\" TEXT NULL, " +
        "\"FromAssigneeId\" INTEGER NULL, " +
        "\"ToAssigneeId\" INTEGER NULL, " +
        "\"FromSprintId\" INTEGER NULL, " +
        "\"ToSprintId\" INTEGER NULL, " +
        "\"FromPoints\" INTEGER NULL, " +
        "\"ToPoints\" INTEGER NULL, " +
        "\"Summary\" TEXT NULL, " +
        "\"Source\" TEXT NULL, " +
        "CONSTRAINT \"FK_TaskEvents_Projects_ProjectId\" FOREIGN KEY (\"ProjectId\") REFERENCES \"Projects\" (\"Id\") ON DELETE CASCADE);");

    await db.Database.ExecuteSqlRawAsync(
        "CREATE INDEX IF NOT EXISTS \"IX_TaskEvents_ProjectId_OccurredAtUtc\" " +
        "ON \"TaskEvents\" (\"ProjectId\", \"OccurredAtUtc\");");

    await db.Database.ExecuteSqlRawAsync(
        "CREATE INDEX IF NOT EXISTS \"IX_TaskEvents_ProjectId_LocalDate\" " +
        "ON \"TaskEvents\" (\"ProjectId\", \"LocalDate\");");

    await db.Database.ExecuteSqlRawAsync(
        "CREATE INDEX IF NOT EXISTS \"IX_TaskEvents_TaskItemId_OccurredAtUtc\" " +
        "ON \"TaskEvents\" (\"TaskItemId\", \"OccurredAtUtc\");");

    await db.Database.ExecuteSqlRawAsync(
        "CREATE INDEX IF NOT EXISTS \"IX_TaskEvents_ProjectId_ActorDiscordId_OccurredAtUtc\" " +
        "ON \"TaskEvents\" (\"ProjectId\", \"ActorDiscordId\", \"OccurredAtUtc\");");
}

static async Task EnsureKnowledgeTablesAsync(BotDbContext db)
{
    await db.Database.ExecuteSqlRawAsync(
        "CREATE TABLE IF NOT EXISTS \"MemberProfiles\" (" +
        "\"Id\" INTEGER NOT NULL CONSTRAINT \"PK_MemberProfiles\" PRIMARY KEY AUTOINCREMENT, " +
        "\"ProjectId\" INTEGER NOT NULL, " +
        "\"DiscordUserId\" INTEGER NOT NULL, " +
        "\"DisplayName\" TEXT NOT NULL, " +
        "\"RoleSummary\" TEXT NOT NULL, " +
        "\"SkillKeywordsJson\" TEXT NOT NULL, " +
        "\"ActiveChannelsJson\" TEXT NOT NULL, " +
        "\"TotalMessageCount\" INTEGER NOT NULL, " +
        "\"TotalStandupReports\" INTEGER NOT NULL, " +
        "\"TotalTaskEvents\" INTEGER NOT NULL, " +
        "\"OpenTaskCount\" INTEGER NOT NULL, " +
        "\"OpenBugCount\" INTEGER NOT NULL, " +
        "\"OpenPoints\" INTEGER NOT NULL, " +
        "\"ReliabilityScore\" INTEGER NOT NULL, " +
        "\"ConfidencePercent\" INTEGER NOT NULL, " +
        "\"EvidenceSummary\" TEXT NOT NULL, " +
        "\"LastSignalDate\" TEXT NULL, " +
        "\"LastSeenAtUtc\" TEXT NULL, " +
        "\"UpdatedAtUtc\" TEXT NOT NULL, " +
        "CONSTRAINT \"FK_MemberProfiles_Projects_ProjectId\" FOREIGN KEY (\"ProjectId\") REFERENCES \"Projects\" (\"Id\") ON DELETE CASCADE);");
    await db.Database.ExecuteSqlRawAsync("CREATE UNIQUE INDEX IF NOT EXISTS \"IX_MemberProfiles_ProjectId_DiscordUserId\" ON \"MemberProfiles\" (\"ProjectId\", \"DiscordUserId\");");
    await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_MemberProfiles_ProjectId_ReliabilityScore\" ON \"MemberProfiles\" (\"ProjectId\", \"ReliabilityScore\");");

    await db.Database.ExecuteSqlRawAsync(
        "CREATE TABLE IF NOT EXISTS \"MemberDailySignals\" (" +
        "\"Id\" INTEGER NOT NULL CONSTRAINT \"PK_MemberDailySignals\" PRIMARY KEY AUTOINCREMENT, " +
        "\"ProjectId\" INTEGER NOT NULL, " +
        "\"DiscordUserId\" INTEGER NOT NULL, " +
        "\"LocalDate\" TEXT NOT NULL, " +
        "\"ExpectedStandup\" INTEGER NOT NULL, " +
        "\"SubmittedStandup\" INTEGER NOT NULL, " +
        "\"WasLate\" INTEGER NOT NULL, " +
        "\"LateMinutes\" INTEGER NULL, " +
        "\"HasBlocker\" INTEGER NOT NULL, " +
        "\"CompletedTasks\" INTEGER NOT NULL, " +
        "\"FixedBugs\" INTEGER NOT NULL, " +
        "\"ActivityCount\" INTEGER NOT NULL, " +
        "\"OpenTaskCount\" INTEGER NOT NULL, " +
        "\"OpenBugCount\" INTEGER NOT NULL, " +
        "\"OpenPoints\" INTEGER NOT NULL, " +
        "\"ReliabilityScore\" INTEGER NOT NULL, " +
        "\"EvidenceJson\" TEXT NOT NULL, " +
        "\"UpdatedAtUtc\" TEXT NOT NULL, " +
        "CONSTRAINT \"FK_MemberDailySignals_Projects_ProjectId\" FOREIGN KEY (\"ProjectId\") REFERENCES \"Projects\" (\"Id\") ON DELETE CASCADE);");
    await db.Database.ExecuteSqlRawAsync("CREATE UNIQUE INDEX IF NOT EXISTS \"IX_MemberDailySignals_ProjectId_DiscordUserId_LocalDate\" ON \"MemberDailySignals\" (\"ProjectId\", \"DiscordUserId\", \"LocalDate\");");
    await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_MemberDailySignals_ProjectId_LocalDate\" ON \"MemberDailySignals\" (\"ProjectId\", \"LocalDate\");");

    await db.Database.ExecuteSqlRawAsync(
        "CREATE TABLE IF NOT EXISTS \"TopicMentions\" (" +
        "\"Id\" INTEGER NOT NULL CONSTRAINT \"PK_TopicMentions\" PRIMARY KEY AUTOINCREMENT, " +
        "\"ProjectId\" INTEGER NOT NULL, " +
        "\"LocalDate\" TEXT NOT NULL, " +
        "\"TopicKey\" TEXT NOT NULL, " +
        "\"MentionCount\" INTEGER NOT NULL, " +
        "\"DistinctAuthorCount\" INTEGER NOT NULL, " +
        "\"TopChannelsJson\" TEXT NOT NULL, " +
        "\"TopAuthorsJson\" TEXT NOT NULL, " +
        "\"SourceSummary\" TEXT NOT NULL, " +
        "\"EvidenceJson\" TEXT NOT NULL, " +
        "\"UpdatedAtUtc\" TEXT NOT NULL, " +
        "CONSTRAINT \"FK_TopicMentions_Projects_ProjectId\" FOREIGN KEY (\"ProjectId\") REFERENCES \"Projects\" (\"Id\") ON DELETE CASCADE);");
    await db.Database.ExecuteSqlRawAsync("CREATE UNIQUE INDEX IF NOT EXISTS \"IX_TopicMentions_ProjectId_LocalDate_TopicKey\" ON \"TopicMentions\" (\"ProjectId\", \"LocalDate\", \"TopicKey\");");
    await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_TopicMentions_ProjectId_TopicKey_LocalDate\" ON \"TopicMentions\" (\"ProjectId\", \"TopicKey\", \"LocalDate\");");

    await db.Database.ExecuteSqlRawAsync(
        "CREATE TABLE IF NOT EXISTS \"DecisionLogs\" (" +
        "\"Id\" INTEGER NOT NULL CONSTRAINT \"PK_DecisionLogs\" PRIMARY KEY AUTOINCREMENT, " +
        "\"ProjectId\" INTEGER NOT NULL, " +
        "\"LocalDate\" TEXT NOT NULL, " +
        "\"TopicKey\" TEXT NOT NULL, " +
        "\"Summary\" TEXT NOT NULL, " +
        "\"Evidence\" TEXT NOT NULL, " +
        "\"ConfidencePercent\" INTEGER NOT NULL, " +
        "\"SourceMessageId\" INTEGER NULL, " +
        "\"SourceChannelName\" TEXT NULL, " +
        "\"CreatedAtUtc\" TEXT NOT NULL, " +
        "CONSTRAINT \"FK_DecisionLogs_Projects_ProjectId\" FOREIGN KEY (\"ProjectId\") REFERENCES \"Projects\" (\"Id\") ON DELETE CASCADE);");
    await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_DecisionLogs_ProjectId_LocalDate\" ON \"DecisionLogs\" (\"ProjectId\", \"LocalDate\");");
    await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_DecisionLogs_ProjectId_TopicKey_LocalDate\" ON \"DecisionLogs\" (\"ProjectId\", \"TopicKey\", \"LocalDate\");");

    await db.Database.ExecuteSqlRawAsync(
        "CREATE TABLE IF NOT EXISTS \"RiskLogs\" (" +
        "\"Id\" INTEGER NOT NULL CONSTRAINT \"PK_RiskLogs\" PRIMARY KEY AUTOINCREMENT, " +
        "\"ProjectId\" INTEGER NOT NULL, " +
        "\"LocalDate\" TEXT NOT NULL, " +
        "\"RiskKey\" TEXT NOT NULL, " +
        "\"Severity\" TEXT NOT NULL, " +
        "\"Summary\" TEXT NOT NULL, " +
        "\"Evidence\" TEXT NOT NULL, " +
        "\"ConfidencePercent\" INTEGER NOT NULL, " +
        "\"CreatedAtUtc\" TEXT NOT NULL, " +
        "CONSTRAINT \"FK_RiskLogs_Projects_ProjectId\" FOREIGN KEY (\"ProjectId\") REFERENCES \"Projects\" (\"Id\") ON DELETE CASCADE);");
    await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_RiskLogs_ProjectId_LocalDate\" ON \"RiskLogs\" (\"ProjectId\", \"LocalDate\");");
    await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_RiskLogs_ProjectId_RiskKey_LocalDate\" ON \"RiskLogs\" (\"ProjectId\", \"RiskKey\", \"LocalDate\");");

    await db.Database.ExecuteSqlRawAsync(
        "CREATE TABLE IF NOT EXISTS \"SprintDailySnapshots\" (" +
        "\"Id\" INTEGER NOT NULL CONSTRAINT \"PK_SprintDailySnapshots\" PRIMARY KEY AUTOINCREMENT, " +
        "\"ProjectId\" INTEGER NOT NULL, " +
        "\"SprintId\" INTEGER NOT NULL, " +
        "\"LocalDate\" TEXT NOT NULL, " +
        "\"TotalTasks\" INTEGER NOT NULL, " +
        "\"DoneTasks\" INTEGER NOT NULL, " +
        "\"InProgressTasks\" INTEGER NOT NULL, " +
        "\"BacklogTasksInSprint\" INTEGER NOT NULL, " +
        "\"OpenBugCount\" INTEGER NOT NULL, " +
        "\"TotalPoints\" INTEGER NOT NULL, " +
        "\"DonePoints\" INTEGER NOT NULL, " +
        "\"InProgressPoints\" INTEGER NOT NULL, " +
        "\"DeliveryProgressPercent\" INTEGER NOT NULL, " +
        "\"ScheduleProgressPercent\" INTEGER NULL, " +
        "\"StalledTaskCount\" INTEGER NOT NULL, " +
        "\"OverdueTaskCount\" INTEGER NOT NULL, " +
        "\"HealthLabel\" TEXT NOT NULL, " +
        "\"HealthDeltaPercent\" INTEGER NULL, " +
        "\"GeneratedAtUtc\" TEXT NOT NULL, " +
        "CONSTRAINT \"FK_SprintDailySnapshots_Projects_ProjectId\" FOREIGN KEY (\"ProjectId\") REFERENCES \"Projects\" (\"Id\") ON DELETE CASCADE);");
    await db.Database.ExecuteSqlRawAsync("CREATE UNIQUE INDEX IF NOT EXISTS \"IX_SprintDailySnapshots_ProjectId_SprintId_LocalDate\" ON \"SprintDailySnapshots\" (\"ProjectId\", \"SprintId\", \"LocalDate\");");
    await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_SprintDailySnapshots_ProjectId_LocalDate\" ON \"SprintDailySnapshots\" (\"ProjectId\", \"LocalDate\");");

    await db.Database.ExecuteSqlRawAsync(
        "CREATE TABLE IF NOT EXISTS \"ProjectRiskSnapshots\" (" +
        "\"Id\" INTEGER NOT NULL CONSTRAINT \"PK_ProjectRiskSnapshots\" PRIMARY KEY AUTOINCREMENT, " +
        "\"ProjectId\" INTEGER NOT NULL, " +
        "\"LocalDate\" TEXT NOT NULL, " +
        "\"RiskScore\" INTEGER NOT NULL, " +
        "\"OpenRiskCount\" INTEGER NOT NULL, " +
        "\"OverdueTaskCount\" INTEGER NOT NULL, " +
        "\"StalledTaskCount\" INTEGER NOT NULL, " +
        "\"MissingStandupCount\" INTEGER NOT NULL, " +
        "\"OpenBugCount\" INTEGER NOT NULL, " +
        "\"BlockerCount\" INTEGER NOT NULL, " +
        "\"Summary\" TEXT NOT NULL, " +
        "\"GeneratedAtUtc\" TEXT NOT NULL, " +
        "CONSTRAINT \"FK_ProjectRiskSnapshots_Projects_ProjectId\" FOREIGN KEY (\"ProjectId\") REFERENCES \"Projects\" (\"Id\") ON DELETE CASCADE);");
    await db.Database.ExecuteSqlRawAsync("CREATE UNIQUE INDEX IF NOT EXISTS \"IX_ProjectRiskSnapshots_ProjectId_LocalDate\" ON \"ProjectRiskSnapshots\" (\"ProjectId\", \"LocalDate\");");
}




