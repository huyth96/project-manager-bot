# ProjectManagerBot

Discord project/scrum bot for game studios, built with .NET 8 Worker Service + Discord.Net + SQLite (EF Core).

## Quick Setup

1. Copy `.env.example` to `.env`
2. Fill at least:
   - `DISCORD_BOT_TOKEN`
   - `DISCORD_GUILD_ID` (optional if registering commands globally)
3. Run:

```powershell
dotnet run
```

Or on Windows:

```powershell
.\run.bat
```

## Security Notes

- `.env` is ignored by git and must never be committed.
- Keep `appsettings.json` free of secrets.
- If a token was ever exposed, rotate it in Discord Developer Portal.

