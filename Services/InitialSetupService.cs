using Discord;
using Discord.WebSocket;

namespace ProjectManagerBot.Services;

public sealed class InitialSetupService(ILogger<InitialSetupService> logger)
{
    private const string MainHallCategory = "\U0001F4E2 S\u1EA2NH CH\u00CDNH";
    private const string ProjectCategory = "\u2694\uFE0F Project A: Đồ Án Tốt Nghiệp";
    private const string BotZoneCategory = "\U0001F916 BOT ZONE";
    private const string MeetingCategory = "\U0001F50A H\u1ECCP H\u00C0NH";
    private const string RoleSelectionChannelSlug = "role-selection";
    private const string RoleSelectionChannelName = "\U0001F3AD-" + RoleSelectionChannelSlug;
    private const string OnboardChannelName = "\U0001F9ED-onboard";
    private const string WikiChannelName = "\U0001F4D8-wiki";
    private const string ShopChannelName = "\U0001F6D2-shop";
    private const string RoleSelectionEmbedTitle = "\U0001F3AD Nhận Role Tự Động";
    private const string OnboardingEmbedTitle = "\U0001F9ED Bắt Đầu Nhanh Cho Thành Viên Mới";
    private const string WikiEmbedTitle = "\U0001F4D8 Tài Liệu Thiết Kế Dự Án A";
    private const string ShopEmbedTitle = "\U0001F6D2 Chợ Role Xịn";
    private const string ProjectDesignDocsUrl = "https://drive.google.com/drive/u/1/folders/1gXvOvh5Ab6x26ddpOI5TLKedQdtjWECm";

    private static readonly IReadOnlyDictionary<string, string> ReactionRoleMap = new Dictionary<string, string>
    {
        ["\U0001F3AE"] = "Developer",
        ["\U0001F3A8"] = "Artist"
    };

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

        var announcements = await EnsureTextChannelAsync(guild, mainHall.Id, "\U0001F4E3-announcements");
        var resourcesWiki = await EnsureTextChannelAsync(guild, mainHall.Id, WikiChannelName);
        var generalChat = await EnsureTextChannelAsync(guild, mainHall.Id, "\U0001F4AC-general-chat");
        var onboarding = await EnsureTextChannelAsync(guild, mainHall.Id, OnboardChannelName);
        var roleSelection = await EnsureTextChannelAsync(guild, mainHall.Id, RoleSelectionChannelName);
        var roleShop = await EnsureTextChannelAsync(guild, mainHall.Id, ShopChannelName);

        var p1Dashboard = await EnsureTextChannelAsync(guild, projectHall.Id, "\U0001F5FA\uFE0F-p1-dashboard");
        var p1Backlog = await EnsureTextChannelAsync(guild, projectHall.Id, "\U0001F4DC-p1-backlog");
        var p1General = await EnsureTextChannelAsync(guild, projectHall.Id, "\U0001F3AE-p1-general");
        var p1ArtShowcase = await EnsureTextChannelAsync(guild, projectHall.Id, "\U0001F3A8-p1-art-showcase");
        var p1DevTalk = await EnsureTextChannelAsync(guild, projectHall.Id, "\U0001F4BB-p1-dev-talk");
        var p1Bugs = await EnsureTextChannelAsync(guild, projectHall.Id, "\U0001F41E-p1-bugs");

        var dailyStandup = await EnsureTextChannelAsync(guild, botZone.Id, "\U0001F4DD-daily-standup");
        var githubCommits = await EnsureTextChannelAsync(guild, botZone.Id, "\U0001F4E6-github-commits");
        var commandLogs = await EnsureTextChannelAsync(guild, botZone.Id, "\U0001F9FE-command-logs");
        var globalTaskFeed = await EnsureTextChannelAsync(guild, botZone.Id, "\U0001F4E2-global-task-feed");

        var dailyScrum = await EnsureVoiceChannelAsync(guild, meetingHall.Id, "\U0001F399\uFE0F Daily Scrum");
        var coWorking = await EnsureVoiceChannelAsync(guild, meetingHall.Id, "\U0001F3A7 Co-working");
        var meetingRoom = await EnsureVoiceChannelAsync(guild, meetingHall.Id, "\U0001F4DE Meeting Room");

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
                roleSelection,
                roleShop,
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
        await ConfigureRoleSelectionPermissionsAsync(guild, roleSelection);
        await ConfigureShopChannelPermissionsAsync(guild, roleShop);
        await EnsureRoleSelectionMessageAsync(guild, roleSelection);
        await EnsureOnboardingMessageAsync(guild, onboarding);
        await EnsureWikiMessageAsync(guild, resourcesWiki);
        await EnsureShopMessageAsync(guild, roleShop);
        await ConfigureCommandLogsPermissionsAsync(guild, commandLogs, leadRole);

        return new StudioSetupResult
        {
            P1DashboardChannelId = p1Dashboard.Id,
            P1BugsChannelId = p1Bugs.Id,
            DailyStandupChannelId = dailyStandup.Id,
            GitHubCommitsChannelId = githubCommits.Id,
            CommandLogsChannelId = commandLogs.Id,
            GlobalTaskFeedChannelId = globalTaskFeed.Id,
            ShopChannelId = roleShop.Id,
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

    private static async Task ConfigureRoleSelectionPermissionsAsync(SocketGuild guild, ITextChannel roleSelectionChannel)
    {
        await roleSelectionChannel.AddPermissionOverwriteAsync(
            guild.EveryoneRole,
            new OverwritePermissions(
                viewChannel: PermValue.Allow,
                sendMessages: PermValue.Deny,
                readMessageHistory: PermValue.Allow,
                addReactions: PermValue.Allow));
    }

    private static async Task ConfigureShopChannelPermissionsAsync(SocketGuild guild, ITextChannel shopChannel)
    {
        await shopChannel.AddPermissionOverwriteAsync(
            guild.EveryoneRole,
            new OverwritePermissions(
                viewChannel: PermValue.Allow,
                sendMessages: PermValue.Allow,
                readMessageHistory: PermValue.Allow));
    }

    private async Task EnsureRoleSelectionMessageAsync(SocketGuild guild, ITextChannel roleSelectionChannel)
    {
        var existingMessage = (await roleSelectionChannel.GetMessagesAsync(20).FlattenAsync())
            .OfType<IUserMessage>()
            .FirstOrDefault(x =>
                x.Author.Id == guild.CurrentUser.Id &&
                x.Embeds.FirstOrDefault()?.Title == RoleSelectionEmbedTitle);

        var embed = new EmbedBuilder()
            .WithTitle(RoleSelectionEmbedTitle)
            .WithColor(Color.Blue)
            .WithDescription(
                "Thả reaction để tự nhận/gỡ role:\n" +
                "- \U0001F3AE `Developer`\n" +
                "- \U0001F3A8 `Artist`\n\n" +
                "Gỡ reaction nếu muốn bỏ role.")
            .Build();

        if (existingMessage is null)
        {
            existingMessage = await roleSelectionChannel.SendMessageAsync(embed: embed);
        }
        else
        {
            await existingMessage.ModifyAsync(props =>
            {
                props.Embed = embed;
            });
        }

        foreach (var emojiText in ReactionRoleMap.Keys)
        {
            try
            {
                await existingMessage.AddReactionAsync(new Emoji(emojiText));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Không thể thêm reaction {Emoji} vào message chọn role ở guild {GuildId}",
                    emojiText,
                    guild.Id);
            }
        }
    }

    private async Task EnsureOnboardingMessageAsync(SocketGuild guild, ITextChannel onboardingChannel)
    {
        var existingMessage = (await onboardingChannel.GetMessagesAsync(20).FlattenAsync())
            .OfType<IUserMessage>()
            .FirstOrDefault(x =>
                x.Author.Id == guild.CurrentUser.Id &&
                x.Embeds.FirstOrDefault()?.Title == OnboardingEmbedTitle);

        var embed = new EmbedBuilder()
            .WithTitle(OnboardingEmbedTitle)
            .WithColor(Color.Green)
            .WithDescription(
                "Chào mừng bạn đến với studio.\n\n" +
                "Lộ trình đề xuất:\n" +
                "1. Vào `📘-wiki` để đọc tài liệu dự án.\n" +
                "2. Vào `🎭-role-selection` để tự nhận role phù hợp.\n" +
                "3. Vào `🗺️-p1-dashboard` để nắm trạng thái sprint.\n" +
                "4. Dùng `/shop view` trong `🛒-shop` để xem role xịn.\n\n" +
                "Nếu có vướng mắc, ping `Studio Lead`.")
            .Build();

        if (existingMessage is null)
        {
            await onboardingChannel.SendMessageAsync(embed: embed);
            return;
        }

        await existingMessage.ModifyAsync(props =>
        {
            props.Embed = embed;
        });
    }

    private async Task EnsureWikiMessageAsync(SocketGuild guild, ITextChannel wikiChannel)
    {
        var existingMessage = (await wikiChannel.GetMessagesAsync(20).FlattenAsync())
            .OfType<IUserMessage>()
            .FirstOrDefault(x =>
                x.Author.Id == guild.CurrentUser.Id &&
                x.Embeds.FirstOrDefault()?.Title == WikiEmbedTitle);

        var embed = new EmbedBuilder()
            .WithTitle(WikiEmbedTitle)
            .WithColor(Color.Blue)
            .WithDescription(
                "Kho tài liệu chính thức của **Project A**:\n" +
                $"{ProjectDesignDocsUrl}\n\n" +
                "Gợi ý đọc nhanh:\n" +
                "- Vision & gameplay loop\n" +
                "- Art direction\n" +
                "- Tech design và task breakdown")
            .Build();

        if (existingMessage is null)
        {
            await wikiChannel.SendMessageAsync(embed: embed);
            return;
        }

        await existingMessage.ModifyAsync(props =>
        {
            props.Embed = embed;
        });
    }

    private async Task EnsureShopMessageAsync(SocketGuild guild, ITextChannel shopChannel)
    {
        var existingMessage = (await shopChannel.GetMessagesAsync(20).FlattenAsync())
            .OfType<IUserMessage>()
            .FirstOrDefault(x =>
                x.Author.Id == guild.CurrentUser.Id &&
                x.Embeds.FirstOrDefault()?.Title == ShopEmbedTitle);

        var embed = new EmbedBuilder()
            .WithTitle(ShopEmbedTitle)
            .WithColor(Color.Gold)
            .WithDescription(
                "Dùng slash command để mua role bằng point (XP):\n" +
                "- `/shop view`: xem danh sách role\n" +
                "- `/shop balance`: xem điểm hiện tại\n" +
                "- `/shop buy`: mua role xịn\n\n" +
                "Point (XP) nhận được khi hoàn thành task/bug.")
            .Build();

        if (existingMessage is null)
        {
            await shopChannel.SendMessageAsync(embed: embed);
            return;
        }

        await existingMessage.ModifyAsync(props =>
        {
            props.Embed = embed;
        });
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
    public ulong ShopChannelId { get; set; }
    public int DeletedChannelsCount { get; set; }
}
