using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using ProjectManagerBot.Services;

namespace ProjectManagerBot.Modules;

[Group("shop", "Cửa hàng role bằng point (XP).")]
[RequireContext(ContextType.Guild)]
public sealed class ShopModule(
    ProjectService projectService,
    ILogger<ShopModule> logger) : InteractionModuleBase<SocketInteractionContext>
{
    private static readonly IReadOnlyDictionary<string, ShopRoleItem> ShopItems =
        new Dictionary<string, ShopRoleItem>(StringComparer.OrdinalIgnoreCase)
        {
            ["vip-gold"] = new(
                Key: "vip-gold",
                RoleName: "VIP Gold",
                Cost: 120,
                Color: new Color(241, 196, 15),
                Description: "Role màu vàng dành cho thành viên hoạt động ổn định."),
            ["diamond-member"] = new(
                Key: "diamond-member",
                RoleName: "Diamond Member",
                Cost: 300,
                Color: new Color(52, 152, 219),
                Description: "Role màu xanh dành cho thành viên đóng góp tốt."),
            ["mythic-core"] = new(
                Key: "mythic-core",
                RoleName: "Mythic Core",
                Cost: 600,
                Color: new Color(231, 76, 60),
                Description: "Role cấp cao nhất trong shop.")
        };

    private readonly ProjectService _projectService = projectService;
    private readonly ILogger<ShopModule> _logger = logger;

    [SlashCommand("view", "Xem bảng giá role và số dư XP hiện tại.")]
    public async Task ViewShopAsync()
    {
        if (!EnsureShopChannel())
        {
            await RespondAsync("Hãy dùng lệnh trong kênh có chữ `shop` (ví dụ `🛒-shop`).", ephemeral: true);
            return;
        }

        if (Context.User is not SocketGuildUser guildUser)
        {
            await RespondAsync("Không lấy được thông tin thành viên.", ephemeral: true);
            return;
        }

        var xp = await _projectService.GetUserXpAsync(Context.User.Id);
        var embed = BuildShopEmbed(guildUser, xp);
        await RespondAsync(embed: embed, components: BuildShopComponents(), ephemeral: true);
    }

    [SlashCommand("balance", "Xem số dư point (XP) của bạn.")]
    public async Task BalanceAsync()
    {
        if (!EnsureShopChannel())
        {
            await RespondAsync("Hãy dùng lệnh trong kênh có chữ `shop` (ví dụ `🛒-shop`).", ephemeral: true);
            return;
        }

        var xp = await _projectService.GetUserXpAsync(Context.User.Id);
        await RespondAsync($"Số dư hiện tại của bạn: `{xp} XP`.", ephemeral: true);
    }

    [SlashCommand("buy", "Mua role bằng point (XP).")]
    public async Task BuyAsync(
        [Summary("item", "Role muốn mua")]
        [Choice("VIP Gold (120 XP)", "vip-gold")]
        [Choice("Diamond Member (300 XP)", "diamond-member")]
        [Choice("Mythic Core (600 XP)", "mythic-core")]
        string item)
    {
        if (!EnsureShopChannel())
        {
            await RespondAsync("Hãy dùng lệnh trong kênh có chữ `shop` (ví dụ `🛒-shop`).", ephemeral: true);
            return;
        }

        if (Context.User is not SocketGuildUser guildUser)
        {
            await RespondAsync("Không lấy được thông tin thành viên.", ephemeral: true);
            return;
        }

        var message = await PurchaseItemAsync(guildUser, item);
        await RespondAsync(message, ephemeral: true);
    }

    [ComponentInteraction("shop:balance", true)]
    public async Task ShopBalanceButtonAsync()
    {
        var xp = await _projectService.GetUserXpAsync(Context.User.Id);
        await RespondAsync($"Số dư hiện tại của bạn: `{xp} XP`.", ephemeral: true);
    }

    [ComponentInteraction("shop:refresh", true)]
    public async Task ShopRefreshButtonAsync()
    {
        if (!EnsureShopChannel())
        {
            await RespondAsync("Panel shop chỉ sử dụng trong kênh `shop`.", ephemeral: true);
            return;
        }

        if (Context.User is not SocketGuildUser guildUser)
        {
            await RespondAsync("Không lấy được thông tin thành viên.", ephemeral: true);
            return;
        }

        var xp = await _projectService.GetUserXpAsync(Context.User.Id);
        var embed = BuildShopEmbed(guildUser, xp);
        await RespondAsync(embed: embed, components: BuildShopComponents(), ephemeral: true);
    }

    [ComponentInteraction("shop:buy:*", true)]
    public async Task ShopBuyButtonAsync(string item)
    {
        if (!EnsureShopChannel())
        {
            await RespondAsync("Panel shop chỉ sử dụng trong kênh `shop`.", ephemeral: true);
            return;
        }

        if (Context.User is not SocketGuildUser guildUser)
        {
            await RespondAsync("Không lấy được thông tin thành viên.", ephemeral: true);
            return;
        }

        var result = await PurchaseItemAsync(guildUser, item);
        var xp = await _projectService.GetUserXpAsync(Context.User.Id);
        var embed = BuildShopEmbed(guildUser, xp);
        await RespondAsync($"{result}\n\nBảng giá đã được cập nhật bên dưới.", embed: embed, components: BuildShopComponents(), ephemeral: true);
    }

    private async Task<string> PurchaseItemAsync(SocketGuildUser guildUser, string itemKey)
    {
        if (!ShopItems.TryGetValue(itemKey, out var selectedItem))
        {
            return "Món hàng không hợp lệ.";
        }

        var role = await EnsureShopRoleAsync(Context.Guild, selectedItem);
        if (guildUser.Roles.Any(x => x.Id == role.Id))
        {
            return $"Bạn đã sở hữu role `{selectedItem.RoleName}`.";
        }

        var currentXp = await _projectService.GetUserXpAsync(Context.User.Id);
        if (currentXp < selectedItem.Cost)
        {
            var missing = selectedItem.Cost - currentXp;
            return
                $"Không đủ point để mua `{selectedItem.RoleName}`.\n" +
                $"- Cần: `{selectedItem.Cost} XP`\n" +
                $"- Đang có: `{currentXp} XP`\n" +
                $"- Thiếu: `{missing} XP`";
        }

        var spendResult = await _projectService.SpendXpAsync(Context.User.Id, selectedItem.Cost);
        if (!spendResult.Success)
        {
            return $"Không thể trừ point lúc này. Số dư hiện tại: `{spendResult.RemainingXp} XP`.";
        }

        try
        {
            await guildUser.AddRoleAsync(role);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Không thể cấp role {RoleName} cho user {UserId}; đang hoàn XP",
                selectedItem.RoleName,
                guildUser.Id);
            await _projectService.AwardXpAsync(Context.User.Id, selectedItem.Cost);
            return "Mua role thất bại do bot thiếu quyền cấp role. Point đã được hoàn lại.";
        }

        return
            $"Mua role thành công: `{selectedItem.RoleName}`\n" +
            $"- Đã trừ: `{selectedItem.Cost} XP`\n" +
            $"- XP còn lại: `{spendResult.RemainingXp} XP`";
    }

    private static async Task<IRole> EnsureShopRoleAsync(SocketGuild guild, ShopRoleItem item)
    {
        var existing = guild.Roles.FirstOrDefault(x =>
            x.Name.Equals(item.RoleName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            var needsUpdate = existing.Color.RawValue != item.Color.RawValue ||
                              !existing.IsMentionable ||
                              !existing.IsHoisted;
            if (needsUpdate)
            {
                await existing.ModifyAsync(props =>
                {
                    props.Color = item.Color;
                    props.Mentionable = true;
                    props.Hoist = true;
                });
            }

            return existing;
        }

        return await guild.CreateRoleAsync(
            name: item.RoleName,
            permissions: GuildPermissions.None,
            color: item.Color,
            isHoisted: true,
            isMentionable: true);
    }

    private static MessageComponent BuildShopComponents()
    {
        return new ComponentBuilder()
            .WithButton("Xem điểm", "shop:balance", ButtonStyle.Secondary)
            .WithButton("Mua VIP Gold", "shop:buy:vip-gold", ButtonStyle.Success)
            .WithButton("Mua Diamond", "shop:buy:diamond-member", ButtonStyle.Primary)
            .WithButton("Mua Mythic", "shop:buy:mythic-core", ButtonStyle.Danger)
            .WithButton("Làm mới", "shop:refresh", ButtonStyle.Secondary)
            .Build();
    }

    private static Embed BuildShopEmbed(SocketGuildUser guildUser, int xp)
    {
        var roleLines = ShopItems.Values
            .OrderBy(x => x.Cost)
            .Select(x =>
            {
                var owned = guildUser.Roles.Any(r => r.Name.Equals(x.RoleName, StringComparison.OrdinalIgnoreCase));
                var status = owned ? "Đã sở hữu" : "Chưa sở hữu";
                return $"- **{x.RoleName}** • `{x.Cost} XP` • {status}\n  {x.Description}";
            });

        return new EmbedBuilder()
            .WithTitle("🛒 Cửa hàng role")
            .WithColor(Color.Gold)
            .WithDescription(
                $"Số dư hiện tại: **`{xp} XP`**\n" +
                "Nhấn nút bên dưới để mua role hoặc xem điểm.")
            .AddField("Danh sách role", string.Join('\n', roleLines), false)
            .AddField("Lệnh thay thế", "`/shop view` • `/shop balance` • `/shop buy`", false)
            .Build();
    }

    private bool EnsureShopChannel()
    {
        return Context.Channel is SocketTextChannel textChannel &&
               textChannel.Name.Contains("shop", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ShopRoleItem(
        string Key,
        string RoleName,
        int Cost,
        Color Color,
        string Description);
}
