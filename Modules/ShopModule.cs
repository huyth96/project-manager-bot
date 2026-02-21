using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using ProjectManagerBot.Services;

namespace ProjectManagerBot.Modules;

[Group("shop", "Cửa hàng role xịn bằng point (XP)")]
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
                Description: "Hào quang vàng, hợp cho thành viên hoạt động ổn định."),
            ["diamond-member"] = new(
                Key: "diamond-member",
                RoleName: "Diamond Member",
                Cost: 300,
                Color: new Color(52, 152, 219),
                Description: "Role xanh kim cương dành cho người chơi cày điểm tốt."),
            ["mythic-core"] = new(
                Key: "mythic-core",
                RoleName: "Mythic Core",
                Cost: 600,
                Color: new Color(231, 76, 60),
                Description: "Role xịn cấp cao nhất của shop.")
        };

    private readonly ProjectService _projectService = projectService;
    private readonly ILogger<ShopModule> _logger = logger;

    [SlashCommand("view", "Xem danh sách role có thể mua bằng point.")]
    public async Task ViewShopAsync()
    {
        if (!EnsureShopChannel())
        {
            await RespondAsync("Hãy dùng lệnh shop trong kênh có chữ `shop` (ví dụ `🛒-shop`).", ephemeral: true);
            return;
        }

        var xp = await _projectService.GetUserXpAsync(Context.User.Id);
        var guildUser = Context.User as SocketGuildUser;

        var lines = ShopItems.Values
            .OrderBy(x => x.Cost)
            .Select(x =>
            {
                var owned = guildUser?.Roles.Any(r => r.Name.Equals(x.RoleName, StringComparison.OrdinalIgnoreCase)) == true;
                return
                    $"- **{x.RoleName}** • `{x.Cost} XP` {(owned ? "✅" : string.Empty)}\n" +
                    $"  {x.Description}";
            });

        var embed = new EmbedBuilder()
            .WithTitle("🛒 Shop Role Xịn")
            .WithColor(Color.Gold)
            .WithDescription(
                $"Point hiện tại của bạn: **`{xp} XP`**\n\n" +
                string.Join('\n', lines) +
                "\n\nDùng `/shop buy` để mua role.")
            .Build();

        await RespondAsync(embed: embed, ephemeral: true);
    }

    [SlashCommand("balance", "Xem point (XP) hiện tại của bạn.")]
    public async Task BalanceAsync()
    {
        if (!EnsureShopChannel())
        {
            await RespondAsync("Hãy dùng lệnh shop trong kênh có chữ `shop` (ví dụ `🛒-shop`).", ephemeral: true);
            return;
        }

        var xp = await _projectService.GetUserXpAsync(Context.User.Id);
        await RespondAsync($"Bạn đang có `{xp} XP`.", ephemeral: true);
    }

    [SlashCommand("buy", "Mua role xịn bằng point (XP).")]
    public async Task BuyAsync(
        [Summary("item", "Role muốn mua")]
        [Choice("VIP Gold (120 XP)", "vip-gold")]
        [Choice("Diamond Member (300 XP)", "diamond-member")]
        [Choice("Mythic Core (600 XP)", "mythic-core")]
        string item)
    {
        if (!EnsureShopChannel())
        {
            await RespondAsync("Hãy dùng lệnh shop trong kênh có chữ `shop` (ví dụ `🛒-shop`).", ephemeral: true);
            return;
        }

        if (Context.User is not SocketGuildUser guildUser)
        {
            await RespondAsync("Không lấy được thông tin thành viên.", ephemeral: true);
            return;
        }

        if (!ShopItems.TryGetValue(item, out var selectedItem))
        {
            await RespondAsync("Món hàng không hợp lệ.", ephemeral: true);
            return;
        }

        var role = await EnsureShopRoleAsync(Context.Guild, selectedItem);
        if (guildUser.Roles.Any(x => x.Id == role.Id))
        {
            await RespondAsync($"Bạn đã sở hữu role `{selectedItem.RoleName}` rồi.", ephemeral: true);
            return;
        }

        var currentXp = await _projectService.GetUserXpAsync(Context.User.Id);
        if (currentXp < selectedItem.Cost)
        {
            var missing = selectedItem.Cost - currentXp;
            await RespondAsync(
                $"Không đủ point để mua `{selectedItem.RoleName}`.\n" +
                $"- Cần: `{selectedItem.Cost} XP`\n" +
                $"- Đang có: `{currentXp} XP`\n" +
                $"- Thiếu: `{missing} XP`",
                ephemeral: true);
            return;
        }

        var spendResult = await _projectService.SpendXpAsync(Context.User.Id, selectedItem.Cost);
        if (!spendResult.Success)
        {
            await RespondAsync(
                $"Không thể trừ point lúc này. XP hiện tại của bạn: `{spendResult.RemainingXp} XP`.",
                ephemeral: true);
            return;
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
            await RespondAsync(
                "Mua role thất bại do thiếu quyền cấp role của bot. Point đã được hoàn lại.",
                ephemeral: true);
            return;
        }

        await RespondAsync(
            $"Mua role thành công: `{selectedItem.RoleName}`\n" +
            $"- Đã trừ: `{selectedItem.Cost} XP`\n" +
            $"- XP còn lại: `{spendResult.RemainingXp} XP`",
            ephemeral: true);
    }

    private static async Task<IRole> EnsureShopRoleAsync(SocketGuild guild, ShopRoleItem item)
    {
        var existing = guild.Roles.FirstOrDefault(x =>
            x.Name.Equals(item.RoleName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return existing;
        }

        return await guild.CreateRoleAsync(
            name: item.RoleName,
            permissions: GuildPermissions.None,
            color: item.Color,
            isHoisted: false,
            isMentionable: true);
    }

    private sealed record ShopRoleItem(
        string Key,
        string RoleName,
        int Cost,
        Color Color,
        string Description);

    private bool EnsureShopChannel()
    {
        return Context.Channel is SocketTextChannel textChannel &&
               textChannel.Name.Contains("shop", StringComparison.OrdinalIgnoreCase);
    }
}
