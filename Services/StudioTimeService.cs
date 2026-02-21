using Microsoft.Extensions.Options;
using ProjectManagerBot.Options;

namespace ProjectManagerBot.Services;

public sealed class StudioTimeService
{
    public TimeZoneInfo TimeZone { get; }

    public StudioTimeService(IOptions<DiscordBotOptions> options)
    {
        TimeZone = ResolveTimeZone(options.Value.TimeZoneId);
    }

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public DateTimeOffset LocalNow => TimeZoneInfo.ConvertTime(UtcNow, TimeZone);

    public DateTime LocalDate => LocalNow.Date;

    private static TimeZoneInfo ResolveTimeZone(string configuredId)
    {
        var candidates = new[]
        {
            configuredId,
            "SE Asia Standard Time",
            "Asia/Bangkok"
        };

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(candidate);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        return TimeZoneInfo.CreateCustomTimeZone(
            id: "UTC+7",
            baseUtcOffset: TimeSpan.FromHours(7),
            displayName: "UTC+07:00",
            standardDisplayName: "UTC+07:00");
    }
}
