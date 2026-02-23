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
    private const string OnboardingChannelName = "\U0001F9ED-onboarding";
    private const string WikiChannelName = "\U0001F4D8-wiki";
    private const string ShopChannelName = "\U0001F6D2-shop";
    private const string RoleSelectionEmbedTitle = "\U0001F3AD Chọn Vai Trò";
    private const string OnboardingEmbedTitle = "\U0001F9ED Hướng Dẫn Bắt Đầu";
    private const string WikiEmbedTitle = "\U0001F4D8 Tài Liệu Dự Án A";
    private const string ShopEmbedTitle = "\U0001F6D2 Cửa Hàng Vai Trò";
    private const string ChannelGuideEmbedTitle = "\U0001F5C2\uFE0F Hướng Dẫn Sử Dụng Kênh";
    private const string ProjectDesignDocsUrl = "https://drive.google.com/drive/u/1/folders/1gXvOvh5Ab6x26ddpOI5TLKedQdtjWECm";

    private static readonly IReadOnlyDictionary<string, string> ReactionRoleMap = new Dictionary<string, string>
    {
        ["\U0001F3AE"] = "Developer",
        ["\U0001F3A8"] = "Artist"
    };

    private readonly ILogger<InitialSetupService> _logger = logger;

    public Task<StudioSetupResult> InitializeStudioAsync(SocketGuild guild, CancellationToken cancellationToken = default)
    {
        return SetupStudioAsync(guild, resetAllChannels: true, cancellationToken);
    }

    public Task<StudioSetupResult> UpdateStudioAsync(SocketGuild guild, CancellationToken cancellationToken = default)
    {
        return SetupStudioAsync(guild, resetAllChannels: false, cancellationToken);
    }

    private async Task<StudioSetupResult> SetupStudioAsync(
        SocketGuild guild,
        bool resetAllChannels,
        CancellationToken cancellationToken = default)
    {
        var deletedChannelsCount = resetAllChannels ? await ResetAllChannelsAsync(guild) : 0;

        var leadRole = await EnsureRoleAsync(guild, "Studio Lead", new Color(230, 126, 34), isHoisted: true);
        var guestRole = await EnsureRoleAsync(guild, "Guest", new Color(149, 165, 166));
        var developerRole = await EnsureRoleAsync(guild, "Developer", new Color(52, 152, 219));
        var artistRole = await EnsureRoleAsync(guild, "Artist", new Color(231, 76, 60));

        var mainHall = await EnsureCategoryAsync(guild, MainHallCategory);
        var projectHall = await EnsureCategoryAsync(guild, ProjectCategory);
        var botZone = await EnsureCategoryAsync(guild, BotZoneCategory);
        var meetingHall = await EnsureCategoryAsync(guild, MeetingCategory);

        var announcements = await EnsureTextChannelAsync(
            guild,
            mainHall.Id,
            "\U0001F4E3-announcements",
            "Thông báo chính thức của studio: lịch, mốc sprint và cập nhật quan trọng.");
        var resourcesWiki = await EnsureTextChannelAsync(
            guild,
            mainHall.Id,
            WikiChannelName,
            "Tài liệu thiết kế Project A, guideline kỹ thuật và tài nguyên tham khảo.");
        var generalChat = await EnsureTextChannelAsync(
            guild,
            mainHall.Id,
            "\U0001F4AC-general-chat",
            "Khu trò chuyện chung của thành viên.");
        var onboarding = await EnsureTextChannelAsync(
            guild,
            mainHall.Id,
            OnboardingChannelName,
            "Điểm khởi đầu cho thành viên mới: đọc wiki, chọn role, theo dõi dashboard.");
        var roleSelection = await EnsureTextChannelAsync(
            guild,
            mainHall.Id,
            RoleSelectionChannelName,
            "Thả reaction vào tin nhắn bot để nhận hoặc gỡ role.");
        var roleShop = await EnsureTextChannelAsync(
            guild,
            mainHall.Id,
            ShopChannelName,
            "Mua role bằng XP bằng nút bấm hoặc slash command /shop.");

        var p1Dashboard = await EnsureTextChannelAsync(
            guild,
            projectHall.Id,
            "\U0001F5FA\uFE0F-p1-dashboard",
            "Bảng điều phối sprint và trạng thái dự án.");
        var p1Backlog = await EnsureTextChannelAsync(
            guild,
            projectHall.Id,
            "\U0001F4DC-p1-backlog",
            "Nơi thêm nhiệm vụ tồn đọng trước khi vào sprint.");
        var p1General = await EnsureTextChannelAsync(
            guild,
            projectHall.Id,
            "\U0001F3AE-p1-general",
            "Trao đổi công việc chung của team Project A.");
        var p1ArtShowcase = await EnsureTextChannelAsync(
            guild,
            projectHall.Id,
            "\U0001F3A8-p1-art-showcase",
            "Đăng sản phẩm art để nhận góp ý; bot sẽ tự tạo thread thảo luận.");
        var p1DevTalk = await EnsureTextChannelAsync(
            guild,
            projectHall.Id,
            "\U0001F4BB-p1-dev-talk",
            "Trao đổi kỹ thuật, kiến trúc và giải pháp triển khai.");
        var p1Bugs = await EnsureTextChannelAsync(
            guild,
            projectHall.Id,
            "\U0001F41E-p1-bugs",
            "Báo lỗi và theo dõi trạng thái xử lý bug.");

        var dailyStandup = await EnsureTextChannelAsync(
            guild,
            botZone.Id,
            "\U0001F4DD-daily-standup",
            "Nơi bot nhắc và tổng hợp báo cáo hằng ngày.");
        var githubCommits = await EnsureTextChannelAsync(
            guild,
            botZone.Id,
            "\U0001F4E6-github-commits",
            "Log commit/push tự động từ GitHub.");
        var commandLogs = await EnsureTextChannelAsync(
            guild,
            botZone.Id,
            "\U0001F9FE-command-logs",
            "Nhật ký lệnh bot, dành cho admin và Studio Lead.");
        var globalTaskFeed = await EnsureTextChannelAsync(
            guild,
            botZone.Id,
            "\U0001F4E2-global-task-feed",
            "Thông báo task toàn cục: quá hạn, cập nhật quan trọng.");

        var dailyScrum = await EnsureVoiceChannelAsync(guild, meetingHall.Id, "\U0001F399\uFE0F Daily Scrum");
        var coWorking = await EnsureVoiceChannelAsync(guild, meetingHall.Id, "\U0001F3A7 Co-working");
        var meetingRoom = await EnsureVoiceChannelAsync(guild, meetingHall.Id, "\U0001F4DE Meeting Room");
        var openLobby = await EnsureVoiceChannelAsync(guild, meetingHall.Id, "\U0001F5E3\uFE0F Open Lobby");

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
        await ConfigureOpenVoiceChannelPermissionsAsync(guild, openLobby);
        await ConfigureGuestAccessAsync(
            guild,
            guestRole,
            generalChat,
            p1ArtShowcase,
            mainHallReadOnlyChannels:
            [
                announcements,
                resourcesWiki,
                onboarding,
                roleSelection,
                roleShop
            ],
            restrictedTextChannels:
            [
                p1Dashboard,
                p1Backlog,
                p1General,
                p1DevTalk,
                p1Bugs,
                dailyStandup,
                githubCommits,
                globalTaskFeed,
                commandLogs
            ],
            restrictedVoiceChannels:
            [
                dailyScrum,
                coWorking,
                meetingRoom
            ]);
        await EnsureRoleSelectionMessageAsync(guild, roleSelection, onboarding, roleShop);
        await EnsureOnboardingMessageAsync(
            guild,
            onboarding,
            resourcesWiki,
            roleSelection,
            p1Dashboard,
            roleShop);
        await EnsureWikiMessageAsync(guild, resourcesWiki, onboarding, p1Dashboard);
        await EnsureShopMessageAsync(guild, roleShop);
        await EnsureChannelGuideMessageAsync(
            guild,
            onboarding,
            resourcesWiki,
            onboarding,
            roleSelection,
            roleShop,
            p1Dashboard,
            p1Backlog,
            p1General,
            p1ArtShowcase,
            p1DevTalk,
            p1Bugs,
            dailyStandup,
            githubCommits,
            globalTaskFeed);
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

    private static async Task<IRole> EnsureRoleAsync(
        SocketGuild guild,
        string roleName,
        Color roleColor,
        bool isHoisted = false)
    {
        var existing = guild.Roles.FirstOrDefault(x => x.Name.Equals(roleName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            var needsUpdate = existing.Color.RawValue != roleColor.RawValue ||
                              existing.IsHoisted != isHoisted ||
                              !existing.IsMentionable;
            if (needsUpdate)
            {
                await existing.ModifyAsync(props =>
                {
                    props.Color = roleColor;
                    props.Hoist = isHoisted;
                    props.Mentionable = true;
                });
            }

            return existing;
        }

        return await guild.CreateRoleAsync(
            name: roleName,
            permissions: GuildPermissions.None,
            color: roleColor,
            isHoisted: isHoisted,
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

    private static async Task<ITextChannel> EnsureTextChannelAsync(
        SocketGuild guild,
        ulong categoryId,
        string name,
        string? topic = null)
    {
        var existing = guild.TextChannels.FirstOrDefault(
            x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && x.CategoryId == categoryId);
        if (existing is not null)
        {
            if (!string.Equals(existing.Topic ?? string.Empty, topic ?? string.Empty, StringComparison.Ordinal))
            {
                await existing.ModifyAsync(props =>
                {
                    props.Topic = topic;
                });
            }

            return existing;
        }

        return await guild.CreateTextChannelAsync(name, props =>
        {
            props.CategoryId = categoryId;
            props.Topic = topic;
        });
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

    private static async Task ConfigureOpenVoiceChannelPermissionsAsync(SocketGuild guild, IVoiceChannel openVoiceChannel)
    {
        await openVoiceChannel.AddPermissionOverwriteAsync(
            guild.EveryoneRole,
            new OverwritePermissions(
                viewChannel: PermValue.Allow,
                connect: PermValue.Allow,
                speak: PermValue.Allow));

        await openVoiceChannel.AddPermissionOverwriteAsync(
            guild.CurrentUser,
            new OverwritePermissions(
                viewChannel: PermValue.Allow,
                connect: PermValue.Allow,
                speak: PermValue.Allow));
    }

    private async Task ConfigureGuestAccessAsync(
        SocketGuild guild,
        IRole guestRole,
        ITextChannel generalChatChannel,
        ITextChannel showcaseChannel,
        IReadOnlyCollection<ITextChannel> mainHallReadOnlyChannels,
        IReadOnlyCollection<ITextChannel> restrictedTextChannels,
        IReadOnlyCollection<IVoiceChannel> restrictedVoiceChannels)
    {
        foreach (var channel in mainHallReadOnlyChannels)
        {
            await channel.AddPermissionOverwriteAsync(
                guestRole,
                new OverwritePermissions(
                    viewChannel: PermValue.Allow,
                    sendMessages: PermValue.Deny,
                    readMessageHistory: PermValue.Allow));
        }

        await generalChatChannel.AddPermissionOverwriteAsync(
            guestRole,
            new OverwritePermissions(
                viewChannel: PermValue.Allow,
                sendMessages: PermValue.Allow,
                readMessageHistory: PermValue.Allow));

        await showcaseChannel.AddPermissionOverwriteAsync(
            guestRole,
            new OverwritePermissions(
                viewChannel: PermValue.Allow,
                sendMessages: PermValue.Deny,
                readMessageHistory: PermValue.Allow));

        foreach (var channel in restrictedTextChannels)
        {
            await channel.AddPermissionOverwriteAsync(
                guestRole,
                new OverwritePermissions(
                    viewChannel: PermValue.Deny,
                    sendMessages: PermValue.Deny,
                    readMessageHistory: PermValue.Deny));
        }

        foreach (var channel in restrictedVoiceChannels)
        {
            await channel.AddPermissionOverwriteAsync(
                guestRole,
                new OverwritePermissions(
                    viewChannel: PermValue.Deny,
                    connect: PermValue.Deny,
                    speak: PermValue.Deny));
        }

        _logger.LogInformation(
            "Đã cấu hình quyền Guest (chỉ chat general, xem sảnh chính và showcase) cho guild {GuildId}",
            guild.Id);
    }

    private async Task EnsureRoleSelectionMessageAsync(
        SocketGuild guild,
        ITextChannel roleSelectionChannel,
        ITextChannel onboardingChannel,
        ITextChannel shopChannel)
    {
        var existingMessage = (await roleSelectionChannel.GetMessagesAsync(20).FlattenAsync())
            .OfType<IUserMessage>()
            .FirstOrDefault(x =>
                x.Author.Id == guild.CurrentUser.Id &&
                x.Embeds.FirstOrDefault()?.Title == RoleSelectionEmbedTitle);

        var components = new ComponentBuilder()
            .WithButton(
                "Hướng dẫn bắt đầu",
                style: ButtonStyle.Link,
                url: BuildChannelUrl(guild, onboardingChannel.Id))
            .WithButton(
                "Mở cửa hàng role",
                style: ButtonStyle.Link,
                url: BuildChannelUrl(guild, shopChannel.Id))
            .Build();

        var embed = new EmbedBuilder()
            .WithTitle(RoleSelectionEmbedTitle)
            .WithColor(Color.Blue)
            .WithDescription(
                "Thả reaction bên dưới để nhận vai trò.\n" +
                "Gỡ reaction nếu muốn rời vai trò.")
            .AddField(
                "\U0001F3AE Developer",
                "Dành cho thành viên tập trung dev, kỹ thuật và xử lý task/bug.",
                true)
            .AddField(
                "\U0001F3A8 Artist",
                "Dành cho thành viên tập trung art, showcase và feedback hình ảnh.",
                true)
            .AddField(
                "Lưu ý",
                "Bot cần quyền `Manage Roles` và role bot phải đứng trên các role thành viên.\n" +
                "Nếu còn role `Guest`, bạn vẫn chưa truy cập được kênh team cho đến khi được gỡ `Guest`.",
                false)
            .Build();

        if (existingMessage is null)
        {
            existingMessage = await roleSelectionChannel.SendMessageAsync(embed: embed, components: components);
        }
        else
        {
            await existingMessage.ModifyAsync(props =>
            {
                props.Embed = embed;
                props.Components = components;
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

    private async Task EnsureOnboardingMessageAsync(
        SocketGuild guild,
        ITextChannel onboardingChannel,
        ITextChannel wikiChannel,
        ITextChannel roleSelectionChannel,
        ITextChannel dashboardChannel,
        ITextChannel shopChannel)
    {
        var existingMessage = (await onboardingChannel.GetMessagesAsync(20).FlattenAsync())
            .OfType<IUserMessage>()
            .FirstOrDefault(x =>
                x.Author.Id == guild.CurrentUser.Id &&
                x.Embeds.FirstOrDefault()?.Title == OnboardingEmbedTitle);

        var components = new ComponentBuilder()
            .WithButton(
                "Wiki",
                style: ButtonStyle.Link,
                url: BuildChannelUrl(guild, wikiChannel.Id))
            .WithButton(
                "Chọn vai trò",
                style: ButtonStyle.Link,
                url: BuildChannelUrl(guild, roleSelectionChannel.Id))
            .WithButton(
                "Dashboard",
                style: ButtonStyle.Link,
                url: BuildChannelUrl(guild, dashboardChannel.Id))
            .WithButton(
                "Shop",
                style: ButtonStyle.Link,
                url: BuildChannelUrl(guild, shopChannel.Id))
            .Build();

        var embed = new EmbedBuilder()
            .WithTitle(OnboardingEmbedTitle)
            .WithColor(Color.Green)
            .WithDescription(
                "Chào mừng bạn đến với studio. Làm theo các bước sau để bắt đầu nhanh.")
            .AddField(
                "1) Đọc tài liệu",
                $"- Mở <#{wikiChannel.Id}> và đọc mục thiết kế chính.",
                false)
            .AddField(
                "2) Chọn vai trò",
                $"- Vào <#{roleSelectionChannel.Id}> và thả reaction để nhận role.",
                false)
            .AddField(
                "3) Theo dõi tiến độ",
                $"- Xem <#{dashboardChannel.Id}> để nắm sprint và task hiện tại.",
                false)
            .AddField(
                "4) Mua role bằng point",
                $"- Mở <#{shopChannel.Id}> và dùng nút bấm hoặc lệnh `/shop`.",
                false)
            .AddField(
                "Cần hỗ trợ?",
                "- Ping `Studio Lead` hoặc admin trong `💬-general-chat`.",
                false)
            .Build();

        if (existingMessage is null)
        {
            await onboardingChannel.SendMessageAsync(embed: embed, components: components);
            return;
        }

        await existingMessage.ModifyAsync(props =>
        {
            props.Embed = embed;
            props.Components = components;
        });
    }

    private async Task EnsureWikiMessageAsync(
        SocketGuild guild,
        ITextChannel wikiChannel,
        ITextChannel onboardingChannel,
        ITextChannel dashboardChannel)
    {
        var existingMessage = (await wikiChannel.GetMessagesAsync(20).FlattenAsync())
            .OfType<IUserMessage>()
            .FirstOrDefault(x =>
                x.Author.Id == guild.CurrentUser.Id &&
                x.Embeds.FirstOrDefault()?.Title == WikiEmbedTitle);

        var components = new ComponentBuilder()
            .WithButton(
                "Mở tài liệu Project A",
                style: ButtonStyle.Link,
                url: ProjectDesignDocsUrl)
            .WithButton(
                "Onboarding",
                style: ButtonStyle.Link,
                url: BuildChannelUrl(guild, onboardingChannel.Id))
            .WithButton(
                "Dashboard",
                style: ButtonStyle.Link,
                url: BuildChannelUrl(guild, dashboardChannel.Id))
            .Build();

        var embed = new EmbedBuilder()
            .WithTitle(WikiEmbedTitle)
            .WithColor(Color.Blue)
            .WithDescription(
                "Kho tài liệu chính thức của **Project A**:\n" +
                $"{ProjectDesignDocsUrl}\n\n" +
                "Đề xuất thứ tự đọc:\n" +
                "1. Vision và gameplay loop\n" +
                "2. Art direction\n" +
                "3. Tech design và task breakdown")
            .Build();

        if (existingMessage is null)
        {
            await wikiChannel.SendMessageAsync(embed: embed, components: components);
            return;
        }

        await existingMessage.ModifyAsync(props =>
        {
            props.Embed = embed;
            props.Components = components;
        });
    }

    private async Task EnsureShopMessageAsync(SocketGuild guild, ITextChannel shopChannel)
    {
        var existingMessage = (await shopChannel.GetMessagesAsync(20).FlattenAsync())
            .OfType<IUserMessage>()
            .FirstOrDefault(x =>
                x.Author.Id == guild.CurrentUser.Id &&
                x.Embeds.FirstOrDefault()?.Title == ShopEmbedTitle);

        var components = BuildShopPanelComponents();
        var embed = new EmbedBuilder()
            .WithTitle(ShopEmbedTitle)
            .WithColor(Color.Gold)
            .WithDescription(
                "Dùng bảng tương tác bên dưới để mua role bằng point (XP).\n" +
                "Bạn cũng có thể dùng slash command nếu muốn.")
            .AddField(
                "Role hiện có",
                "- `VIP Gold` • `120 XP`\n" +
                "- `Diamond Member` • `300 XP`\n" +
                "- `Mythic Core` • `600 XP`",
                false)
            .AddField(
                "Lệnh thay thế",
                "- `/shop view`\n" +
                "- `/shop balance`\n" +
                "- `/shop buy`",
                false)
            .AddField(
                "Nguồn point",
                "XP được cộng khi hoàn thành task/bug trong dự án.",
                false)
            .Build();

        if (existingMessage is null)
        {
            await shopChannel.SendMessageAsync(embed: embed, components: components);
            return;
        }

        await existingMessage.ModifyAsync(props =>
        {
            props.Embed = embed;
            props.Components = components;
        });
    }

    private async Task EnsureChannelGuideMessageAsync(
        SocketGuild guild,
        ITextChannel guideChannel,
        ITextChannel wikiChannel,
        ITextChannel onboardingChannel,
        ITextChannel roleSelectionChannel,
        ITextChannel shopChannel,
        ITextChannel dashboardChannel,
        ITextChannel backlogChannel,
        ITextChannel projectGeneralChannel,
        ITextChannel artShowcaseChannel,
        ITextChannel devTalkChannel,
        ITextChannel bugsChannel,
        ITextChannel standupChannel,
        ITextChannel githubCommitsChannel,
        ITextChannel globalTaskFeedChannel)
    {
        var existingMessage = (await guideChannel.GetMessagesAsync(30).FlattenAsync())
            .OfType<IUserMessage>()
            .FirstOrDefault(x =>
                x.Author.Id == guild.CurrentUser.Id &&
                x.Embeds.FirstOrDefault()?.Title == ChannelGuideEmbedTitle);

        var components = new ComponentBuilder()
            .WithButton(
                "Onboarding",
                style: ButtonStyle.Link,
                url: BuildChannelUrl(guild, onboardingChannel.Id))
            .WithButton(
                "Dashboard",
                style: ButtonStyle.Link,
                url: BuildChannelUrl(guild, dashboardChannel.Id))
            .WithButton(
                "Wiki",
                style: ButtonStyle.Link,
                url: BuildChannelUrl(guild, wikiChannel.Id))
            .Build();

        var embed = new EmbedBuilder()
            .WithTitle(ChannelGuideEmbedTitle)
            .WithColor(Color.Teal)
            .WithDescription("Tóm tắt mục đích từng kênh chính.")
            .AddField(
                "Main Hall",
                $"- <#{onboardingChannel.Id}>: Hướng dẫn thành viên mới\n" +
                $"- <#{wikiChannel.Id}>: Tài liệu dự án\n" +
                $"- <#{roleSelectionChannel.Id}>: Nhận role bằng reaction\n" +
                $"- <#{shopChannel.Id}>: Mua role bằng XP",
                false)
            .AddField(
                "Project A",
                $"- <#{dashboardChannel.Id}>: Trạng thái sprint và bảng điều phối\n" +
                $"- <#{backlogChannel.Id}>: Nơi thêm task tồn đọng\n" +
                $"- <#{projectGeneralChannel.Id}>: Trao đổi công việc chung\n" +
                $"- <#{artShowcaseChannel.Id}>: Showcase art và góp ý\n" +
                $"- <#{devTalkChannel.Id}>: Trao đổi kỹ thuật\n" +
                $"- <#{bugsChannel.Id}>: Theo dõi và xử lý bug",
                false)
            .AddField(
                "Bot Zone",
                $"- <#{standupChannel.Id}>: Báo cáo hằng ngày\n" +
                $"- <#{githubCommitsChannel.Id}>: Log commit GitHub\n" +
                $"- <#{globalTaskFeedChannel.Id}>: Thông báo task toàn cục",
                false)
            .Build();

        if (existingMessage is null)
        {
            await guideChannel.SendMessageAsync(embed: embed, components: components);
            return;
        }

        await existingMessage.ModifyAsync(props =>
        {
            props.Embed = embed;
            props.Components = components;
        });
    }

    private static MessageComponent BuildShopPanelComponents()
    {
        return new ComponentBuilder()
            .WithButton("Xem điểm", "shop:balance", ButtonStyle.Secondary)
            .WithButton("Mua VIP Gold", "shop:buy:vip-gold", ButtonStyle.Success)
            .WithButton("Mua Diamond", "shop:buy:diamond-member", ButtonStyle.Primary)
            .WithButton("Mua Mythic", "shop:buy:mythic-core", ButtonStyle.Danger)
            .WithButton("Làm mới", "shop:refresh", ButtonStyle.Secondary)
            .Build();
    }

    private static string BuildChannelUrl(SocketGuild guild, ulong channelId)
    {
        return $"https://discord.com/channels/{guild.Id}/{channelId}";
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
