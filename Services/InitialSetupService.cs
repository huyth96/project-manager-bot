using Discord;
using Discord.WebSocket;

namespace ProjectManagerBot.Services;

public sealed class InitialSetupService(ILogger<InitialSetupService> logger)
{
    private const string MainHallCategory = "\U0001F4E2 S\u1EA2NH CH\u00CDNH";
    private const string ProjectCategory = "\u2694\uFE0F Project A: Đồ Án Tốt Nghiệp";
    private const string BotZoneCategory = "\U0001F916 BOT ZONE";
    private const string MeetingCategory = "\U0001F50A H\u1ECCP H\u00C0NH";

    private readonly ILogger<InitialSetupService> _logger = logger;

    public async Task<StudioSetupResult> InitializeStudioAsync(SocketGuild guild, CancellationToken cancellationToken = default)
    {
        var deletedChannelsCount = await ResetAllChannelsAsync(guild);

        var leadRole = await EnsureRoleAsync(guild, "Studio Lead");
        var developerRole = await EnsureRoleAsync(guild, "Developer");
        var artistRole = await EnsureRoleAsync(guild, "Artist");

        var mainHall = await EnsureCategoryAsync(guild, MainHallCategory);
        var projectHall = await EnsureCategoryAsync(guild, ProjectCategory);
        var botZone = await EnsureCategoryAsync(guild, BotZoneCategory);
        var meetingHall = await EnsureCategoryAsync(guild, MeetingCategory);

        var announcements = await EnsureTextChannelAsync(guild, mainHall.Id, "announcements");
        var resourcesWiki = await EnsureTextChannelAsync(guild, mainHall.Id, "resources-wiki");
        var generalChat = await EnsureTextChannelAsync(guild, mainHall.Id, "general-chat");
        var onboarding = await EnsureTextChannelAsync(guild, mainHall.Id, "onboarding");

        var p1Dashboard = await EnsureTextChannelAsync(guild, projectHall.Id, "p1-dashboard");
        var p1Backlog = await EnsureTextChannelAsync(guild, projectHall.Id, "p1-backlog");
        var p1General = await EnsureTextChannelAsync(guild, projectHall.Id, "p1-general");
        var p1ArtShowcase = await EnsureTextChannelAsync(guild, projectHall.Id, "p1-art-showcase");
        var p1DevTalk = await EnsureTextChannelAsync(guild, projectHall.Id, "p1-dev-talk");
        var p1Bugs = await EnsureTextChannelAsync(guild, projectHall.Id, "p1-bugs");

        var dailyStandup = await EnsureTextChannelAsync(guild, botZone.Id, "daily-standup");
        var githubCommits = await EnsureTextChannelAsync(guild, botZone.Id, "github-commits");
        var commandLogs = await EnsureTextChannelAsync(guild, botZone.Id, "command-logs");
        var globalTaskFeed = await EnsureTextChannelAsync(guild, botZone.Id, "global-task-feed");

        var dailyScrum = await EnsureVoiceChannelAsync(guild, meetingHall.Id, "Daily Scrum");
        var coWorking = await EnsureVoiceChannelAsync(guild, meetingHall.Id, "Co-working");
        var meetingRoom = await EnsureVoiceChannelAsync(guild, meetingHall.Id, "Meeting Room");

        await ConfigureDashboardPermissionsAsync(guild, p1Dashboard, leadRole);
        await ConfigureMemberVisibilityAsync(
            guild,
            leadRole,
            developerRole,
            artistRole,
            importantReadOnlyChannels:
            [
                announcements,
                resourcesWiki,
                onboarding,
                p1Dashboard,
                githubCommits,
                globalTaskFeed
            ],
            teamOnlyTextChannels:
            [
                generalChat,
                p1Backlog,
                p1General,
                p1ArtShowcase,
                p1DevTalk,
                p1Bugs,
                dailyStandup
            ],
            teamOnlyVoiceChannels:
            [
                dailyScrum,
                coWorking,
                meetingRoom
            ]);
        await ConfigureCommandLogsPermissionsAsync(guild, commandLogs, leadRole);

        return new StudioSetupResult
        {
            P1DashboardChannelId = p1Dashboard.Id,
            P1BugsChannelId = p1Bugs.Id,
            DailyStandupChannelId = dailyStandup.Id,
            GitHubCommitsChannelId = githubCommits.Id,
            CommandLogsChannelId = commandLogs.Id,
            GlobalTaskFeedChannelId = globalTaskFeed.Id,
            DeletedChannelsCount = deletedChannelsCount
        };
    }

    private async Task<int> ResetAllChannelsAsync(SocketGuild guild)
    {
        var deleted = 0;

        var orderedChannels = guild.Channels
            .OrderBy(x => x is SocketCategoryChannel ? 1 : 0)
            .ThenByDescending(x => x.Position)
            .ToList();

        foreach (var channel in orderedChannels)
        {
            try
            {
                await channel.DeleteAsync();
                deleted++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Không thể xóa kênh {ChannelId} ({ChannelName})", channel.Id, channel.Name);
            }
        }

        _logger.LogInformation("Làm mới studio đã xóa {DeletedChannelsCount} kênh trong guild {GuildId}", deleted, guild.Id);
        return deleted;
    }

    private static async Task<IRole> EnsureRoleAsync(SocketGuild guild, string roleName)
    {
        var existing = guild.Roles.FirstOrDefault(x => x.Name.Equals(roleName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return existing;
        }

        return await guild.CreateRoleAsync(
            name: roleName,
            permissions: GuildPermissions.None,
            color: null,
            isHoisted: false,
            isMentionable: true);
    }

    private static async Task<ICategoryChannel> EnsureCategoryAsync(SocketGuild guild, string name)
    {
        var existing = guild.CategoryChannels.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return existing;
        }

        return await guild.CreateCategoryChannelAsync(name);
    }

    private static async Task<ITextChannel> EnsureTextChannelAsync(SocketGuild guild, ulong categoryId, string name)
    {
        var existing = guild.TextChannels.FirstOrDefault(
            x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && x.CategoryId == categoryId);
        if (existing is not null)
        {
            return existing;
        }

        return await guild.CreateTextChannelAsync(name, props => { props.CategoryId = categoryId; });
    }

    private static async Task<IVoiceChannel> EnsureVoiceChannelAsync(SocketGuild guild, ulong categoryId, string name)
    {
        var existing = guild.VoiceChannels.FirstOrDefault(
            x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && x.CategoryId == categoryId);
        if (existing is not null)
        {
            return existing;
        }

        return await guild.CreateVoiceChannelAsync(name, props => { props.CategoryId = categoryId; });
    }

    private async Task ConfigureDashboardPermissionsAsync(SocketGuild guild, ITextChannel dashboardChannel, IRole leadRole)
    {
        await dashboardChannel.AddPermissionOverwriteAsync(
            guild.EveryoneRole,
            new OverwritePermissions(viewChannel: PermValue.Allow, sendMessages: PermValue.Deny));

        await dashboardChannel.AddPermissionOverwriteAsync(
            leadRole,
            new OverwritePermissions(viewChannel: PermValue.Allow, sendMessages: PermValue.Allow, manageMessages: PermValue.Allow));

        await dashboardChannel.AddPermissionOverwriteAsync(
            guild.CurrentUser,
            new OverwritePermissions(viewChannel: PermValue.Allow, sendMessages: PermValue.Allow, manageMessages: PermValue.Allow));

        _logger.LogInformation("Đã cấu hình quyền cho kênh dashboard của guild {GuildId}", guild.Id);
    }

    private async Task ConfigureMemberVisibilityAsync(
        SocketGuild guild,
        IRole leadRole,
        IRole developerRole,
        IRole artistRole,
        IReadOnlyCollection<ITextChannel> importantReadOnlyChannels,
        IReadOnlyCollection<ITextChannel> teamOnlyTextChannels,
        IReadOnlyCollection<IVoiceChannel> teamOnlyVoiceChannels)
    {
        foreach (var channel in importantReadOnlyChannels)
        {
            await channel.AddPermissionOverwriteAsync(
                guild.EveryoneRole,
                new OverwritePermissions(
                    viewChannel: PermValue.Allow,
                    sendMessages: PermValue.Deny,
                    readMessageHistory: PermValue.Allow));
        }

        var teamRoles = new[] { leadRole, developerRole, artistRole };
        foreach (var channel in teamOnlyTextChannels)
        {
            await channel.AddPermissionOverwriteAsync(
                guild.EveryoneRole,
                new OverwritePermissions(viewChannel: PermValue.Deny));

            foreach (var role in teamRoles)
            {
                await channel.AddPermissionOverwriteAsync(
                    role,
                    new OverwritePermissions(
                        viewChannel: PermValue.Allow,
                        sendMessages: PermValue.Allow,
                        readMessageHistory: PermValue.Allow));
            }

            await channel.AddPermissionOverwriteAsync(
                guild.CurrentUser,
                new OverwritePermissions(
                    viewChannel: PermValue.Allow,
                    sendMessages: PermValue.Allow,
                    manageMessages: PermValue.Allow,
                    readMessageHistory: PermValue.Allow));
        }

        foreach (var channel in teamOnlyVoiceChannels)
        {
            await channel.AddPermissionOverwriteAsync(
                guild.EveryoneRole,
                new OverwritePermissions(
                    viewChannel: PermValue.Deny,
                    connect: PermValue.Deny,
                    speak: PermValue.Deny));

            foreach (var role in teamRoles)
            {
                await channel.AddPermissionOverwriteAsync(
                    role,
                    new OverwritePermissions(
                        viewChannel: PermValue.Allow,
                        connect: PermValue.Allow,
                        speak: PermValue.Allow));
            }

            await channel.AddPermissionOverwriteAsync(
                guild.CurrentUser,
                new OverwritePermissions(
                    viewChannel: PermValue.Allow,
                    connect: PermValue.Allow,
                    speak: PermValue.Allow));
        }

        _logger.LogInformation(
            "Đã giới hạn thành viên thường chỉ xem kênh quan trọng trong guild {GuildId}",
            guild.Id);
    }

    private async Task ConfigureCommandLogsPermissionsAsync(SocketGuild guild, ITextChannel commandLogs, IRole leadRole)
    {
        await commandLogs.AddPermissionOverwriteAsync(
            guild.EveryoneRole,
            new OverwritePermissions(viewChannel: PermValue.Deny));

        await commandLogs.AddPermissionOverwriteAsync(
            leadRole,
            new OverwritePermissions(viewChannel: PermValue.Allow, sendMessages: PermValue.Allow, readMessageHistory: PermValue.Allow));

        foreach (var adminRole in guild.Roles.Where(x => x.Permissions.Administrator && x.Id != guild.EveryoneRole.Id))
        {
            await commandLogs.AddPermissionOverwriteAsync(
                adminRole,
                new OverwritePermissions(viewChannel: PermValue.Allow, sendMessages: PermValue.Allow, readMessageHistory: PermValue.Allow));
        }

        await commandLogs.AddPermissionOverwriteAsync(
            guild.CurrentUser,
            new OverwritePermissions(viewChannel: PermValue.Allow, sendMessages: PermValue.Allow, readMessageHistory: PermValue.Allow));

        _logger.LogInformation("Đã cấu hình quyền riêng tư kênh nhật ký lệnh cho guild {GuildId}", guild.Id);
    }
}

public sealed class StudioSetupResult
{
    public ulong P1DashboardChannelId { get; set; }
    public ulong P1BugsChannelId { get; set; }
    public ulong DailyStandupChannelId { get; set; }
    public ulong GitHubCommitsChannelId { get; set; }
    public ulong CommandLogsChannelId { get; set; }
    public ulong GlobalTaskFeedChannelId { get; set; }
    public int DeletedChannelsCount { get; set; }
}
