using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using ProjectManagerBot.Data;
using ProjectManagerBot.Options;
using ProjectManagerBot.Services;

var builder = Host.CreateApplicationBuilder(args);

var tokenFromEnv = Environment.GetEnvironmentVariable("DISCORD_BOT_TOKEN");
if (!string.IsNullOrWhiteSpace(tokenFromEnv))
{
    builder.Configuration["Discord:Token"] = tokenFromEnv;
}

builder.Services.Configure<DiscordBotOptions>(builder.Configuration.GetSection("Discord"));

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

builder.Services.AddHostedService<DiscordBotService>();
builder.Services.AddHostedService<AutomationService>();

var host = builder.Build();

await using (var scope = host.Services.CreateAsyncScope())
{
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<BotDbContext>>();
    await using var db = await dbFactory.CreateDbContextAsync();
    await db.Database.EnsureCreatedAsync();
    await EnsureColumnAsync(db, "Projects", "GlobalNotificationChannelId", "INTEGER NULL");
    await EnsureColumnAsync(db, "TaskItems", "LastOverdueReminderDateLocal", "TEXT NULL");
    await EnsureColumnAsync(db, "Sprints", "StartDateLocal", "TEXT NULL");
    await EnsureColumnAsync(db, "Sprints", "EndDateLocal", "TEXT NULL");
}

await host.RunAsync();

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
            ("TaskItems", "LastOverdueReminderDateLocal", "TEXT NULL") =>
                "ALTER TABLE \"TaskItems\" ADD COLUMN \"LastOverdueReminderDateLocal\" TEXT NULL;",
            ("Sprints", "StartDateLocal", "TEXT NULL") =>
                "ALTER TABLE \"Sprints\" ADD COLUMN \"StartDateLocal\" TEXT NULL;",
            ("Sprints", "EndDateLocal", "TEXT NULL") =>
                "ALTER TABLE \"Sprints\" ADD COLUMN \"EndDateLocal\" TEXT NULL;",
            _ => throw new InvalidOperationException("Unsupported schema bootstrap operation.")
        };

        await db.Database.ExecuteSqlRawAsync(alterSql);
    }
}
