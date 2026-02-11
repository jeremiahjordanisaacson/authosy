using System.ServiceModel.Syndication;
using System.Xml;
using Authosy.Service.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Authosy.Service.Services;

public class RssFeedService
{
    private readonly AuthosyConfig _config;
    private readonly ILogger<RssFeedService> _logger;
    private readonly HttpClient _httpClient;
    private readonly Dictionary<string, DateTime> _domainLastAccess = new();

    public RssFeedService(IOptions<AuthosyConfig> config, ILogger<RssFeedService> logger, HttpClient httpClient)
    {
        _config = config.Value;
        _logger = logger;
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Authosy/1.0 (Uplifting News Aggregator)");
    }

    public async Task<List<FeedItem>> FetchAllFeedsAsync(CancellationToken ct)
    {
        var allItems = new List<FeedItem>();

        foreach (var feed in _config.Feeds)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                await ThrottleDomain(feed.Url);
                var items = await FetchFeedAsync(feed, ct);
                allItems.AddRange(items);
                _logger.LogInformation("Fetched {Count} items from {Name}", items.Count, feed.Name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch feed {Name}: {Url}", feed.Name, feed.Url);
            }
        }

        return allItems;
    }

    private async Task<List<FeedItem>> FetchFeedAsync(FeedConfig feedConfig, CancellationToken ct)
    {
        var items = new List<FeedItem>();

        using var stream = await _httpClient.GetStreamAsync(feedConfig.Url, ct);
        using var reader = XmlReader.Create(stream);
        var feed = SyndicationFeed.Load(reader);

        if (feed == null) return items;

        foreach (var entry in feed.Items)
        {
            var link = entry.Links.FirstOrDefault()?.Uri?.AbsoluteUri ?? "";
            if (string.IsNullOrEmpty(link)) continue;

            var description = entry.Summary?.Text ?? "";
            // Strip HTML tags from description
            description = System.Text.RegularExpressions.Regex.Replace(description, "<.*?>", " ");
            description = System.Text.RegularExpressions.Regex.Replace(description, @"\s+", " ").Trim();

            items.Add(new FeedItem
            {
                Title = entry.Title?.Text ?? "",
                Description = description.Length > 500 ? description[..500] : description,
                Url = link,
                Region = feedConfig.Region,
                SourceName = feedConfig.Name,
                PublishedDate = entry.PublishDate != DateTimeOffset.MinValue
                    ? entry.PublishDate
                    : entry.LastUpdatedTime != DateTimeOffset.MinValue
                        ? entry.LastUpdatedTime
                        : DateTimeOffset.UtcNow
            });
        }

        return items;
    }

    private async Task ThrottleDomain(string url)
    {
        try
        {
            var domain = new Uri(url).Host;
            if (_domainLastAccess.TryGetValue(domain, out var lastAccess))
            {
                var elapsed = DateTime.UtcNow - lastAccess;
                var delay = TimeSpan.FromMilliseconds(_config.PerDomainDelayMs) - elapsed;
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay);
                }
            }
            _domainLastAccess[domain] = DateTime.UtcNow;
        }
        catch { /* ignore malformed URLs */ }
    }
}
