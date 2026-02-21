using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProjectManagerBot.Data;
using ProjectManagerBot.Models;
using ProjectManagerBot.Options;

namespace ProjectManagerBot.Services;

public sealed class GitHubTrackingService(
    IDbContextFactory<BotDbContext> dbContextFactory,
    IHttpClientFactory httpClientFactory,
    DiscordSocketClient client,
    IOptions<GitHubTrackingOptions> options,
    ILogger<GitHubTrackingService> logger) : BackgroundService
{
    private const string GitHubCommitsChannelName = "github-commits";
    private const string AllBranchesMarker = "*";

    private readonly IDbContextFactory<BotDbContext> _dbContextFactory = dbContextFactory;
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly DiscordSocketClient _client = client;
    private readonly GitHubTrackingOptions _options = options.Value;
    private readonly ILogger<GitHubTrackingService> _logger = logger;
    private readonly SemaphoreSlim _pollLock = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static bool TryNormalizeRepository(string raw, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var value = raw.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            uri.Host.Contains("github.com", StringComparison.OrdinalIgnoreCase))
        {
            value = uri.AbsolutePath.Trim('/');
        }

        value = value.Trim('/');
        if (value.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^4];
        }

        var parts = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        if (!IsValidSegment(parts[0]) || !IsValidSegment(parts[1]))
        {
            return false;
        }

        normalized = $"{parts[0]}/{parts[1]}";
        return true;
    }

    public static bool IsTrackAllBranches(string? branch)
    {
        if (string.IsNullOrWhiteSpace(branch))
        {
            return false;
        }

        var value = branch.Trim();
        return value == AllBranchesMarker ||
               value.Equals("all", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("all-branches", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<GitHubRepoBinding> UpsertBindingAsync(
        int projectId,
        string repoFullName,
        string branch,
        CancellationToken cancellationToken = default)
    {
        var normalizedRepo = repoFullName.Trim();
        var normalizedBranch = NormalizeBranch(branch);

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var binding = await db.GitHubRepoBindings.FirstOrDefaultAsync(
            x =>
                x.ProjectId == projectId &&
                x.RepoFullName == normalizedRepo &&
                x.Branch == normalizedBranch,
            cancellationToken);

        if (binding is null)
        {
            binding = new GitHubRepoBinding
            {
                ProjectId = projectId,
                RepoFullName = normalizedRepo,
                Branch = normalizedBranch,
                IsEnabled = true
            };
            db.GitHubRepoBindings.Add(binding);
        }
        else
        {
            binding.IsEnabled = true;
        }

        if (normalizedBranch == AllBranchesMarker)
        {
            await ExpandTrackAllBindingAsync(db, binding, initializeCursor: true, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        return binding;
    }

    public async Task<bool> RemoveBindingAsync(
        int projectId,
        string repoFullName,
        string branch,
        CancellationToken cancellationToken = default)
    {
        var normalizedBranch = NormalizeBranch(branch);
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        if (normalizedBranch == AllBranchesMarker)
        {
            var allRepoBindings = await db.GitHubRepoBindings
                .Where(x => x.ProjectId == projectId && x.RepoFullName == repoFullName)
                .ToListAsync(cancellationToken);
            if (allRepoBindings.Count == 0)
            {
                return false;
            }

            db.GitHubRepoBindings.RemoveRange(allRepoBindings);
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }

        var binding = await db.GitHubRepoBindings.FirstOrDefaultAsync(
            x =>
                x.ProjectId == projectId &&
                x.RepoFullName == repoFullName &&
                x.Branch == normalizedBranch,
            cancellationToken);

        if (binding is null)
        {
            return false;
        }

        db.GitHubRepoBindings.Remove(binding);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<GitHubRepoBinding>> ListBindingsAsync(
        int projectId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.GitHubRepoBindings
            .AsNoTracking()
            .Where(x => x.ProjectId == projectId)
            .OrderBy(x => x.RepoFullName)
            .ThenBy(x => x.Branch)
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> PrimeBindingCursorAsync(int bindingId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var binding = await db.GitHubRepoBindings.FirstOrDefaultAsync(x => x.Id == bindingId, cancellationToken);
        if (binding is null)
        {
            return false;
        }

        if (binding.Branch == AllBranchesMarker)
        {
            await ExpandTrackAllBindingAsync(db, binding, initializeCursor: true, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }

        var commits = await FetchLatestCommitsAsync(binding.RepoFullName, binding.Branch, cancellationToken);
        if (commits.Count == 0)
        {
            return false;
        }

        binding.LastSeenCommitSha = commits[0].Sha;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<int> PollProjectAsync(int projectId, CancellationToken cancellationToken = default)
    {
        await _pollLock.WaitAsync(cancellationToken);
        try
        {
            return await PollBindingsInternalAsync(projectId, cancellationToken);
        }
        finally
        {
            _pollLock.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("GitHub tracking đã tắt qua cấu hình.");
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Clamp(_options.PollIntervalSeconds, 30, 600));
        using var timer = new PeriodicTimer(interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _pollLock.WaitAsync(stoppingToken);
                try
                {
                    await PollBindingsInternalAsync(projectId: null, stoppingToken);
                }
                finally
                {
                    _pollLock.Release();
                }

                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GitHub tracking loop lỗi.");
            }
        }
    }

    private async Task<int> PollBindingsInternalAsync(int? projectId, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var bindings = await LoadEnabledBindingsAsync(db, projectId, cancellationToken);
        if (bindings.Count == 0)
        {
            return 0;
        }

        var wildcardBindings = bindings.Where(x => x.Branch == AllBranchesMarker).ToList();
        foreach (var wildcard in wildcardBindings)
        {
            try
            {
                await ExpandTrackAllBindingAsync(db, wildcard, initializeCursor: false, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Không thể mở rộng theo dõi mọi nhánh cho {Repo} (project {ProjectId})",
                    wildcard.RepoFullName,
                    wildcard.ProjectId);
            }
        }

        bindings = await LoadEnabledBindingsAsync(db, projectId, cancellationToken);
        bindings = bindings.Where(x => x.Branch != AllBranchesMarker).ToList();
        if (bindings.Count == 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            return 0;
        }

        var projectIds = bindings.Select(x => x.ProjectId).Distinct().ToArray();
        var projects = await db.Projects
            .Where(x => projectIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);

        var notified = 0;
        foreach (var binding in bindings)
        {
            if (!projects.TryGetValue(binding.ProjectId, out var project))
            {
                continue;
            }

            try
            {
                var commits = await FetchLatestCommitsAsync(binding.RepoFullName, binding.Branch, cancellationToken);
                if (commits.Count == 0)
                {
                    continue;
                }

                var latestSha = commits[0].Sha;
                if (string.IsNullOrWhiteSpace(latestSha))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(binding.LastSeenCommitSha))
                {
                    binding.LastSeenCommitSha = latestSha;
                    continue;
                }

                var newCommits = commits
                    .TakeWhile(x => !string.Equals(x.Sha, binding.LastSeenCommitSha, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (newCommits.Count == 0)
                {
                    if (!commits.Any(x => string.Equals(x.Sha, binding.LastSeenCommitSha, StringComparison.OrdinalIgnoreCase)))
                    {
                        // Trường hợp force-push/rewrite lịch sử: đồng bộ lại con trỏ theo commit mới nhất.
                        binding.LastSeenCommitSha = latestSha;
                    }

                    continue;
                }

                var targetChannel = ResolveGitHubCommitsChannel(project);
                if (targetChannel is null)
                {
                    _logger.LogWarning(
                        "Không tìm thấy kênh github-commits cho dự án {ProjectId}. Bỏ qua gửi thông báo push {Repo}/{Branch}.",
                        project.Id,
                        binding.RepoFullName,
                        binding.Branch);
                    continue;
                }

                var embed = BuildPushEmbed(binding.RepoFullName, binding.Branch, newCommits);
                await targetChannel.SendMessageAsync(embed: embed);
                binding.LastSeenCommitSha = latestSha;
                notified++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Không thể lấy commit mới từ {Repo} ({Branch}) cho dự án {ProjectId}",
                    binding.RepoFullName,
                    binding.Branch,
                    binding.ProjectId);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return notified;
    }

    private static async Task<List<GitHubRepoBinding>> LoadEnabledBindingsAsync(
        BotDbContext db,
        int? projectId,
        CancellationToken cancellationToken)
    {
        var bindingsQuery = db.GitHubRepoBindings
            .Where(x => x.IsEnabled);

        if (projectId.HasValue)
        {
            bindingsQuery = bindingsQuery.Where(x => x.ProjectId == projectId.Value);
        }

        return await bindingsQuery
            .OrderBy(x => x.ProjectId)
            .ThenBy(x => x.RepoFullName)
            .ThenBy(x => x.Branch)
            .ToListAsync(cancellationToken);
    }

    private async Task ExpandTrackAllBindingAsync(
        BotDbContext db,
        GitHubRepoBinding wildcardBinding,
        bool initializeCursor,
        CancellationToken cancellationToken)
    {
        var branches = await FetchBranchesAsync(wildcardBinding.RepoFullName, cancellationToken);
        if (branches.Count == 0)
        {
            return;
        }

        var existing = await db.GitHubRepoBindings
            .Where(x =>
                x.ProjectId == wildcardBinding.ProjectId &&
                x.RepoFullName == wildcardBinding.RepoFullName &&
                x.Branch != AllBranchesMarker)
            .ToDictionaryAsync(x => x.Branch, StringComparer.OrdinalIgnoreCase, cancellationToken);

        foreach (var branch in branches)
        {
            if (existing.TryGetValue(branch.Name, out var tracked))
            {
                tracked.IsEnabled = true;
                if (initializeCursor && string.IsNullOrWhiteSpace(tracked.LastSeenCommitSha))
                {
                    tracked.LastSeenCommitSha = branch.HeadSha;
                }
                continue;
            }

            db.GitHubRepoBindings.Add(new GitHubRepoBinding
            {
                ProjectId = wildcardBinding.ProjectId,
                RepoFullName = wildcardBinding.RepoFullName,
                Branch = branch.Name,
                IsEnabled = true,
                LastSeenCommitSha = branch.HeadSha
            });
        }
    }

    private async Task<IReadOnlyList<GitHubBranchDto>> FetchBranchesAsync(
        string repoFullName,
        CancellationToken cancellationToken)
    {
        var requestUri = $"https://api.github.com/repos/{repoFullName}/branches?per_page=100";
        var client = _httpClientFactory.CreateClient("GitHubTracking");
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");

        if (!string.IsNullOrWhiteSpace(_options.Token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.Token.Trim());
        }

        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning(
                "GitHub API trả về {StatusCode} khi đọc nhánh của {Repo}. Payload: {Payload}",
                (int)response.StatusCode,
                repoFullName,
                Truncate(payload, 300));
            return [];
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var result = await JsonSerializer.DeserializeAsync<List<GitHubBranchApiModel>>(stream, _jsonOptions, cancellationToken)
            ?? [];

        return result
            .Where(x => !string.IsNullOrWhiteSpace(x.Name) && !string.IsNullOrWhiteSpace(x.Commit?.Sha))
            .Select(x => new GitHubBranchDto(x.Name!, x.Commit!.Sha!))
            .ToList();
    }

    private async Task<IReadOnlyList<GitHubCommitDto>> FetchLatestCommitsAsync(
        string repoFullName,
        string branch,
        CancellationToken cancellationToken)
    {
        var commitsPerPoll = Math.Clamp(_options.MaxCommitsPerPoll, 1, 25);
        var requestUri =
            $"https://api.github.com/repos/{repoFullName}/commits?sha={Uri.EscapeDataString(branch)}&per_page={commitsPerPoll}";

        var client = _httpClientFactory.CreateClient("GitHubTracking");
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");

        if (!string.IsNullOrWhiteSpace(_options.Token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.Token.Trim());
        }

        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning(
                "GitHub API trả về {StatusCode} khi đọc {Repo}/{Branch}. Payload: {Payload}",
                (int)response.StatusCode,
                repoFullName,
                branch,
                Truncate(payload, 300));
            return [];
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var result = await JsonSerializer.DeserializeAsync<List<GitHubCommitApiModel>>(stream, _jsonOptions, cancellationToken)
            ?? [];

        return result
            .Where(x => !string.IsNullOrWhiteSpace(x.Sha))
            .Select(x => new GitHubCommitDto(
                x.Sha!,
                x.HtmlUrl ?? string.Empty,
                x.Commit?.Message ?? string.Empty,
                x.Commit?.Author?.Name ?? "Unknown"))
            .ToList();
    }

    private ITextChannel? ResolveGitHubCommitsChannel(Project project)
    {
        if (project.GitHubCommitsChannelId.HasValue)
        {
            var mapped = _client.GetChannel(project.GitHubCommitsChannelId.Value) as ITextChannel;
            if (mapped is not null)
            {
                return mapped;
            }
        }

        if (_client.GetChannel(project.ChannelId) is not SocketGuildChannel guildChannel)
        {
            return null;
        }

        return guildChannel.Guild.TextChannels.FirstOrDefault(x =>
            x.Name.Equals(GitHubCommitsChannelName, StringComparison.OrdinalIgnoreCase));
    }

    private static Embed BuildPushEmbed(
        string repoFullName,
        string branch,
        IReadOnlyList<GitHubCommitDto> newCommits)
    {
        var commitLines = newCommits
            .Reverse()
            .Take(8)
            .Select(x =>
            {
                var shortSha = x.Sha.Length > 7 ? x.Sha[..7] : x.Sha;
                var title = FirstLine(x.Message);
                if (x.HtmlUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    return $"- [`{shortSha}`]({x.HtmlUrl}) {title} • `{x.AuthorName}`";
                }

                return $"- `{shortSha}` {title} • `{x.AuthorName}`";
            })
            .ToList();

        if (newCommits.Count > 8)
        {
            commitLines.Add($"- ... và `{newCommits.Count - 8}` commit khác");
        }

        return new EmbedBuilder()
            .WithTitle($"📦 Git Push • {repoFullName}")
            .WithColor(Color.DarkBlue)
            .WithDescription(
                $"🌿 **Branch:** `{branch}`\n" +
                $"🧾 **Commit mới:** `{newCommits.Count}`\n" +
                "━━━━━━━━━━━━━━━━━━━━\n" +
                string.Join('\n', commitLines))
            .WithCurrentTimestamp()
            .Build();
    }

    private static string FirstLine(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "(không có nội dung)";
        }

        var line = message.Split('\n', '\r', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? message;
        return Truncate(line.Trim(), 100);
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private static bool IsValidSegment(string value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               value.Length <= 100 &&
               value.All(x => char.IsLetterOrDigit(x) || x is '-' or '_' or '.');
    }

    private static string NormalizeBranch(string branch)
    {
        if (IsTrackAllBranches(branch))
        {
            return AllBranchesMarker;
        }

        return string.IsNullOrWhiteSpace(branch) ? "main" : branch.Trim();
    }

    private sealed record GitHubBranchDto(
        string Name,
        string HeadSha);

    private sealed record GitHubCommitDto(
        string Sha,
        string HtmlUrl,
        string Message,
        string AuthorName);

    private sealed class GitHubBranchApiModel
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("commit")]
        public GitHubBranchCommitApiModel? Commit { get; set; }
    }

    private sealed class GitHubBranchCommitApiModel
    {
        [JsonPropertyName("sha")]
        public string? Sha { get; set; }
    }

    private sealed class GitHubCommitApiModel
    {
        [JsonPropertyName("sha")]
        public string? Sha { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("commit")]
        public GitHubCommitDetailApiModel? Commit { get; set; }
    }

    private sealed class GitHubCommitDetailApiModel
    {
        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("author")]
        public GitHubCommitAuthorApiModel? Author { get; set; }
    }

    private sealed class GitHubCommitAuthorApiModel
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }
}
