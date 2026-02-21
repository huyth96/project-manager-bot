using System.Reflection;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Options;
using ProjectManagerBot.Options;

namespace ProjectManagerBot.Services;

public sealed class DiscordBotService(
    DiscordSocketClient client,
    InteractionService interactionService,
    IServiceProvider serviceProvider,
    IOptions<DiscordBotOptions> options,
    ILogger<DiscordBotService> logger) : BackgroundService
{
    private readonly DiscordSocketClient _client = client;
    private readonly InteractionService _interactionService = interactionService;
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly DiscordBotOptions _options = options.Value;
    private readonly ILogger<DiscordBotService> _logger = logger;
    private const string RoleSelectionChannelSlug = "role-selection";
    private const string RoleSelectionEmbedTitle = "\U0001F3AD Nhận Role Tự Động";
    private static readonly IReadOnlyDictionary<string, string> ReactionRoleMap = new Dictionary<string, string>
    {
        ["\U0001F3AE"] = "Developer",
        ["\U0001F3A8"] = "Artist"
    };
    private bool _commandsRegistered;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_options.Token))
        {
            throw new InvalidOperationException("Thiếu token Discord. Hãy cấu hình Discord:Token hoặc DISCORD_BOT_TOKEN.");
        }

        _client.Log += OnDiscordLogAsync;
        _interactionService.Log += OnInteractionLogAsync;
        _client.Ready += OnReadyAsync;
        _client.InteractionCreated += OnInteractionCreatedAsync;
        _client.MessageReceived += OnMessageReceivedAsync;
        _client.ReactionAdded += OnReactionAddedAsync;
        _client.ReactionRemoved += OnReactionRemovedAsync;

        await _interactionService.AddModulesAsync(Assembly.GetExecutingAssembly(), _serviceProvider);

        await _client.LoginAsync(TokenType.Bot, _options.Token);
        await _client.StartAsync();

        _logger.LogInformation("Đã khởi động Discord gateway");
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _client.StopAsync();
        await _client.LogoutAsync();
        await base.StopAsync(cancellationToken);
    }

    private async Task OnReadyAsync()
    {
        if (_commandsRegistered)
        {
            return;
        }

        _commandsRegistered = true;

        if (_options.RegisterCommandsGlobally)
        {
            await _interactionService.RegisterCommandsGloballyAsync(deleteMissing: true);
            _logger.LogInformation("Đã đăng ký slash command ở phạm vi toàn cục");
            return;
        }

        if (_options.GuildId != 0)
        {
            await _interactionService.RegisterCommandsToGuildAsync(_options.GuildId, deleteMissing: true);
            _logger.LogInformation("Đã đăng ký slash command cho guild {GuildId}", _options.GuildId);
            return;
        }

        foreach (var guild in _client.Guilds)
        {
            await _interactionService.RegisterCommandsToGuildAsync(guild.Id, deleteMissing: true);
            _logger.LogInformation("Đã đăng ký slash command cho guild {GuildId}", guild.Id);
        }
    }

    private async Task OnInteractionCreatedAsync(SocketInteraction interaction)
    {
        try
        {
            var context = new SocketInteractionContext(_client, interaction);
            var result = await _interactionService.ExecuteCommandAsync(context, _serviceProvider);
            if (!result.IsSuccess && interaction.Type is not InteractionType.ApplicationCommandAutocomplete)
            {
                _logger.LogWarning("Interaction lỗi: {Reason}", result.ErrorReason);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Không thể xử lý interaction");
        }
    }

    private async Task OnMessageReceivedAsync(SocketMessage socketMessage)
    {
        if (socketMessage.Source != MessageSource.User)
        {
            return;
        }

        if (socketMessage.Channel is not SocketTextChannel textChannel)
        {
            return;
        }

        if (!textChannel.Name.Contains("showcase", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (socketMessage is not SocketUserMessage message)
        {
            return;
        }

        var threadNameBase = string.IsNullOrWhiteSpace(message.Content) ? "Thảo luận showcase" : message.Content;
        var threadName = threadNameBase.Length > 90 ? threadNameBase[..90] : threadNameBase;

        try
        {
            await textChannel.CreateThreadAsync(
                name: threadName,
                autoArchiveDuration: ThreadArchiveDuration.OneDay,
                message: message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Không thể tự tạo thread showcase cho message {MessageId}", message.Id);
        }
    }

    private Task OnReactionAddedAsync(
        Cacheable<IUserMessage, ulong> cacheableMessage,
        Cacheable<IMessageChannel, ulong> cacheableChannel,
        SocketReaction reaction)
    {
        return HandleRoleReactionAsync(cacheableMessage, cacheableChannel, reaction, removeRole: false);
    }

    private Task OnReactionRemovedAsync(
        Cacheable<IUserMessage, ulong> cacheableMessage,
        Cacheable<IMessageChannel, ulong> cacheableChannel,
        SocketReaction reaction)
    {
        return HandleRoleReactionAsync(cacheableMessage, cacheableChannel, reaction, removeRole: true);
    }

    private async Task HandleRoleReactionAsync(
        Cacheable<IUserMessage, ulong> cacheableMessage,
        Cacheable<IMessageChannel, ulong> cacheableChannel,
        SocketReaction reaction,
        bool removeRole)
    {
        try
        {
            if (!_commandsRegistered)
            {
                return;
            }

            if (_client.CurrentUser is null || reaction.UserId == _client.CurrentUser.Id)
            {
                return;
            }

            if (!ReactionRoleMap.TryGetValue(reaction.Emote.Name, out var roleName))
            {
                return;
            }

            var channel = await cacheableChannel.GetOrDownloadAsync();
            if (channel is not SocketTextChannel textChannel ||
                !textChannel.Name.Contains(RoleSelectionChannelSlug, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var message = await cacheableMessage.GetOrDownloadAsync();
            if (message is null || message.Author.Id != _client.CurrentUser.Id)
            {
                return;
            }

            var embedTitle = message.Embeds.FirstOrDefault()?.Title;
            if (!string.Equals(embedTitle, RoleSelectionEmbedTitle, StringComparison.Ordinal))
            {
                return;
            }

            var guild = textChannel.Guild;
            var role = guild.Roles.FirstOrDefault(x => x.Name.Equals(roleName, StringComparison.OrdinalIgnoreCase));
            if (role is null)
            {
                return;
            }

            var guildUser = reaction.User.Value as SocketGuildUser ?? guild.GetUser(reaction.UserId);
            if (guildUser is null || guildUser.IsBot)
            {
                return;
            }

            var hasRole = guildUser.Roles.Any(x => x.Id == role.Id);
            if (removeRole)
            {
                if (hasRole)
                {
                    await guildUser.RemoveRoleAsync(role);
                }
            }
            else
            {
                if (!hasRole)
                {
                    await guildUser.AddRoleAsync(role);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Không thể xử lý reaction role cho user {UserId}", reaction.UserId);
        }
    }

    private Task OnDiscordLogAsync(LogMessage logMessage)
    {
        _logger.Log(
            ToLogLevel(logMessage.Severity),
            logMessage.Exception,
            "[Discord] {Source}: {Message}",
            logMessage.Source,
            logMessage.Message);

        return Task.CompletedTask;
    }

    private Task OnInteractionLogAsync(LogMessage logMessage)
    {
        _logger.Log(
            ToLogLevel(logMessage.Severity),
            logMessage.Exception,
            "[Interaction] {Source}: {Message}",
            logMessage.Source,
            logMessage.Message);

        return Task.CompletedTask;
    }

    private static LogLevel ToLogLevel(LogSeverity severity)
    {
        return severity switch
        {
            LogSeverity.Critical => LogLevel.Critical,
            LogSeverity.Error => LogLevel.Error,
            LogSeverity.Warning => LogLevel.Warning,
            LogSeverity.Info => LogLevel.Information,
            LogSeverity.Verbose => LogLevel.Debug,
            LogSeverity.Debug => LogLevel.Trace,
            _ => LogLevel.Information
        };
    }
}
