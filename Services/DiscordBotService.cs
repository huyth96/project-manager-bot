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
