using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using ProjectManagerBot.Data;
using ProjectManagerBot.Options;
using ProjectManagerBot.Services;

LoadDotEnvFromCommonPaths();

var builder = Host.CreateApplicationBuilder(args);
ApplyEnvironmentOverrides(builder.Configuration);

builder.Services.Configure<DiscordBotOptions>(builder.Configuration.GetSection("Discord"));
builder.Services.Configure<GitHubTrackingOptions>(builder.Configuration.GetSection("GitHub"));

builder.Services.AddDbContextFactory<BotDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("BotDb") ?? "Data Source=project-manager-bot.db";
    options.UseSqlite(connectionString);
});

builder.Services.AddSingleton(_ => new DiscordSocketClient(new DiscordSocketConfig
{
    GatewayIntents = GatewayIntents.Guilds |
                     GatewayIntents.GuildMessages |
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
builder.Services.AddSingleton<NotificationService>();
builder.Services.AddHttpClient("GitHubTracking", client =>
{
    client.DefaultRequestHeaders.UserAgent.ParseAdd("ProjectManagerBot/1.0");
    client.Timeout = TimeSpan.FromSeconds(15);
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
    await EnsureColumnAsync(db, "TaskItems", "LastOverdueReminderDateLocal", "TEXT NULL");
    await EnsureColumnAsync(db, "Sprints", "StartDateLocal", "TEXT NULL");
    await EnsureColumnAsync(db, "Sprints", "EndDateLocal", "TEXT NULL");
    await EnsureGitHubRepoBindingsTableAsync(db);
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
