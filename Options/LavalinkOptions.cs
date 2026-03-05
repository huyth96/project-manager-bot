namespace ProjectManagerBot.Options;

public sealed class LavalinkOptions
{
    public string BaseAddress { get; set; } = "http://127.0.0.1:2333/";
    public string WebSocketUri { get; set; } = "ws://127.0.0.1:2333/v4/websocket";
    public string Passphrase { get; set; } = "youshallnotpass";
    public string Label { get; set; } = "main";
    public int ReadyTimeoutSeconds { get; set; } = 20;
}

