using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using ProjectManagerBot.Services;

namespace ProjectManagerBot.Modules;

[Group("shop", "Cua hang role bang point (XP).")]
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
                Description: "Role mau vang danh cho thanh vien hoat dong on dinh."),
            ["diamond-member"] = new(
                Key: "diamond-member",
                RoleName: "Diamond Member",
                Cost: 300,
                Color: new Color(52, 152, 219),
                Description: "Role mau xanh danh cho thanh vien dong gop tot."),
            ["mythic-core"] = new(
                Key: "mythic-core",
                RoleName: "Mythic Core",
                Cost: 600,
                Color: new Color(231, 76, 60),
                Description: "Role cap cao nhat trong shop.")
        };

    private readonly ProjectService _projectService = projectService;
    private readonly ILogger<ShopModule> _logger = logger;

    [SlashCommand("view", "Xem bang gia role va so du XP hien tai.")]
    public async Task ViewShopAsync()
    {
        if (!EnsureShopChannel())
        {
            await RespondAsync("Hay dung lenh trong kenh co chu `shop` (vi du `🛒-shop`).", ephemeral: true);
            return;
        }

        if (Context.User is not SocketGuildUser guildUser)
        {
            await RespondAsync("Khong lay duoc thong tin thanh vien.", ephemeral: true);
            return;
        }

        var xp = await _projectService.GetUserXpAsync(Context.User.Id);
        var embed = BuildShopEmbed(guildUser, xp);
        await RespondAsync(embed: embed, components: BuildShopComponents(), ephemeral: true);
    }

    [SlashCommand("balance", "Xem so du point (XP) cua ban.")]
    public async Task BalanceAsync()
    {
        if (!EnsureShopChannel())
        {
            await RespondAsync("Hay dung lenh trong kenh co chu `shop` (vi du `🛒-shop`).", ephemeral: true);
            return;
        }

        var xp = await _projectService.GetUserXpAsync(Context.User.Id);
        await RespondAsync($"So du hien tai cua ban: `{xp} XP`.", ephemeral: true);
    }

    [SlashCommand("buy", "Mua role bang point (XP).")]
    public async Task BuyAsync(
        [Summary("item", "Role muon mua")]
        [Choice("VIP Gold (120 XP)", "vip-gold")]
        [Choice("Diamond Member (300 XP)", "diamond-member")]
        [Choice("Mythic Core (600 XP)", "mythic-core")]
        string item)
    {
        if (!EnsureShopChannel())
        {
            await RespondAsync("Hay dung lenh trong kenh co chu `shop` (vi du `🛒-shop`).", ephemeral: true);
            return;
        }

        if (Context.User is not SocketGuildUser guildUser)
        {
            await RespondAsync("Khong lay duoc thong tin thanh vien.", ephemeral: true);
            return;
        }

        var message = await PurchaseItemAsync(guildUser, item);
        await RespondAsync(message, ephemeral: true);
    }

    [ComponentInteraction("shop:balance", true)]
    public async Task ShopBalanceButtonAsync()
    {
        var xp = await _projectService.GetUserXpAsync(Context.User.Id);
        await RespondAsync($"So du hien tai cua ban: `{xp} XP`.", ephemeral: true);
    }

    [ComponentInteraction("shop:refresh", true)]
    public async Task ShopRefreshButtonAsync()
    {
        if (!EnsureShopChannel())
        {
            await RespondAsync("Panel shop chi su dung trong kenh `shop`.", ephemeral: true);
            return;
        }

        if (Context.User is not SocketGuildUser guildUser)
        {
            await RespondAsync("Khong lay duoc thong tin thanh vien.", ephemeral: true);
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
            await RespondAsync("Panel shop chi su dung trong kenh `shop`.", ephemeral: true);
            return;
        }

        if (Context.User is not SocketGuildUser guildUser)
        {
            await RespondAsync("Khong lay duoc thong tin thanh vien.", ephemeral: true);
            return;
        }

        var result = await PurchaseItemAsync(guildUser, item);
        var xp = await _projectService.GetUserXpAsync(Context.User.Id);
        var embed = BuildShopEmbed(guildUser, xp);
        await RespondAsync($"{result}\n\nBang gia da duoc cap nhat ben duoi.", embed: embed, components: BuildShopComponents(), ephemeral: true);
    }

    private async Task<string> PurchaseItemAsync(SocketGuildUser guildUser, string itemKey)
    {
        if (!ShopItems.TryGetValue(itemKey, out var selectedItem))
        {
            return "Mon hang khong hop le.";
        }

        var role = await EnsureShopRoleAsync(Context.Guild, selectedItem);
        if (guildUser.Roles.Any(x => x.Id == role.Id))
        {
            return $"Ban da so huu role `{selectedItem.RoleName}`.";
        }

        var currentXp = await _projectService.GetUserXpAsync(Context.User.Id);
        if (currentXp < selectedItem.Cost)
        {
            var missing = selectedItem.Cost - currentXp;
            return
                $"Khong du point de mua `{selectedItem.RoleName}`.\n" +
                $"- Can: `{selectedItem.Cost} XP`\n" +
                $"- Dang co: `{currentXp} XP`\n" +
                $"- Thieu: `{missing} XP`";
        }

        var spendResult = await _projectService.SpendXpAsync(Context.User.Id, selectedItem.Cost);
        if (!spendResult.Success)
        {
            return $"Khong the tru point luc nay. So du hien tai: `{spendResult.RemainingXp} XP`.";
        }

        try
        {
            await guildUser.AddRoleAsync(role);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Khong the cap role {RoleName} cho user {UserId}; dang hoan XP",
                selectedItem.RoleName,
                guildUser.Id);
            await _projectService.AwardXpAsync(Context.User.Id, selectedItem.Cost);
            return "Mua role that bai do bot thieu quyen cap role. Point da duoc hoan lai.";
        }

        return
            $"Mua role thanh cong: `{selectedItem.RoleName}`\n" +
            $"- Da tru: `{selectedItem.Cost} XP`\n" +
            $"- XP con lai: `{spendResult.RemainingXp} XP`";
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
            .WithButton("Xem diem", "shop:balance", ButtonStyle.Secondary)
            .WithButton("Mua VIP Gold", "shop:buy:vip-gold", ButtonStyle.Success)
            .WithButton("Mua Diamond", "shop:buy:diamond-member", ButtonStyle.Primary)
            .WithButton("Mua Mythic", "shop:buy:mythic-core", ButtonStyle.Danger)
            .WithButton("Lam moi", "shop:refresh", ButtonStyle.Secondary)
            .Build();
    }

    private static Embed BuildShopEmbed(SocketGuildUser guildUser, int xp)
    {
        var roleLines = ShopItems.Values
            .OrderBy(x => x.Cost)
            .Select(x =>
            {
                var owned = guildUser.Roles.Any(r => r.Name.Equals(x.RoleName, StringComparison.OrdinalIgnoreCase));
                var status = owned ? "Da so huu" : "Chua so huu";
                return $"- **{x.RoleName}** • `{x.Cost} XP` • {status}\n  {x.Description}";
            });

        return new EmbedBuilder()
            .WithTitle("🛒 Cua hang role")
            .WithColor(Color.Gold)
            .WithDescription(
                $"So du hien tai: **`{xp} XP`**\n" +
                "Nhan nut ben duoi de mua role hoac xem diem.")
            .AddField("Danh sach role", string.Join('\n', roleLines), false)
            .AddField("Lenh thay the", "`/shop view` • `/shop balance` • `/shop buy`", false)
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
