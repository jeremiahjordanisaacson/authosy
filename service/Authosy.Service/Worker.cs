using Authosy.Service.Models;
using Authosy.Service.Services;
using Microsoft.Extensions.Options;

namespace Authosy.Service;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly AuthosyConfig _config;
    private readonly RssFeedService _feedService;
    private readonly ClusteringService _clusteringService;
    private readonly ClaudeCliService _claudeService;
    private readonly MarkdownService _markdownService;
    private readonly StateService _stateService;
    private readonly GitService _gitService;
    private readonly bool _runOnce;

    public Worker(
        ILogger<Worker> logger,
        IOptions<AuthosyConfig> config,
        RssFeedService feedService,
        ClusteringService clusteringService,
        ClaudeCliService claudeService,
        MarkdownService markdownService,
        StateService stateService,
        GitService gitService,
        IConfiguration configuration)
    {
        _logger = logger;
        _config = config.Value;
        _feedService = feedService;
        _clusteringService = clusteringService;
        _claudeService = claudeService;
        _markdownService = markdownService;
        _stateService = stateService;
        _gitService = gitService;
        _runOnce = configuration.GetValue<bool>("RunOnce");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Authosy service starting");

        // Run once at startup
        await RunPipelineAsync(stoppingToken);

        if (_runOnce)
        {
            _logger.LogInformation("RunOnce mode: exiting after single run");
            return;
        }

        // Then run on a periodic timer
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_config.IntervalMinutes));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunPipelineAsync(stoppingToken);
        }
    }

    private async Task RunPipelineAsync(CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("=== Pipeline run starting at {Time} ===", DateTime.UtcNow);

            // Step 1: Fetch RSS feeds
            _logger.LogInformation("Step 1: Fetching RSS feeds...");
            var allItems = await _feedService.FetchAllFeedsAsync(ct);
            _logger.LogInformation("Fetched {Count} total items", allItems.Count);

            if (allItems.Count == 0)
            {
                _logger.LogInformation("No items fetched, skipping run");
                return;
            }

            // Step 2: Filter out already-published URLs
            var newItems = allItems
                .Where(item => !_stateService.IsAlreadyPublished(item.Url))
                .ToList();
            _logger.LogInformation("{Count} new items after filtering published", newItems.Count);

            if (newItems.Count == 0)
            {
                _logger.LogInformation("No new items, skipping run");
                return;
            }

            // Step 3: Classify for positivity
            _logger.LogInformation("Step 3: Classifying {Count} items for positivity...", newItems.Count);
            var positiveItems = new List<FeedItem>();
            foreach (var item in newItems.Take(_config.MaxItemsPerRun * 5)) // Classify more than we need
            {
                if (ct.IsCancellationRequested) break;

                var classification = await _claudeService.ClassifyAsync(item, ct);
                if (classification != null && classification.IsPositive && classification.PositivityScore >= _config.MinPositivityScore)
                {
                    item.Region = classification.Region; // Update region from classifier
                    positiveItems.Add(item);
                    _logger.LogInformation("  Positive ({Score:F2}): {Title}", classification.PositivityScore, item.Title);
                }
            }
            _logger.LogInformation("{Count} items classified as positive", positiveItems.Count);

            if (positiveItems.Count < _config.MinClusterSize)
            {
                _logger.LogInformation("Not enough positive items for clustering");
                return;
            }

            // Step 4: Cluster items
            _logger.LogInformation("Step 4: Clustering...");
            var clusters = _clusteringService.ClusterItems(positiveItems);
            _logger.LogInformation("{Count} valid clusters found", clusters.Count);

            if (clusters.Count == 0)
            {
                _logger.LogInformation("No valid clusters, skipping run");
                return;
            }

            // Step 5: Rewrite and publish
            _logger.LogInformation("Step 5: Rewriting stories...");
            var writtenFiles = new List<string>();
            var publishedCount = 0;

            foreach (var cluster in clusters.Take(_config.MaxItemsPerRun))
            {
                if (ct.IsCancellationRequested) break;

                var sourceUrls = cluster.Items.Select(i => i.Url).ToList();
                var storyId = _markdownService.GenerateStoryId(sourceUrls);

                _logger.LogInformation("  Rewriting cluster: {Title} ({Count} sources)",
                    cluster.PrimaryTitle, cluster.Items.Count);

                var result = await _claudeService.RewriteAsync(cluster, ct);
                if (result == null)
                {
                    _logger.LogWarning("  Failed to rewrite cluster");
                    continue;
                }

                result.Id = storyId;
                result.SourceUrls = sourceUrls;

                var filepath = _markdownService.WriteStory(result);
                writtenFiles.Add(filepath);
                _stateService.MarkPublished(sourceUrls);
                publishedCount++;

                _logger.LogInformation("  Published: {Title} -> {Id}", result.Title, storyId);
            }

            // Step 6: Commit and push
            if (writtenFiles.Count > 0)
            {
                _logger.LogInformation("Step 6: Committing {Count} stories...", writtenFiles.Count);
                var pushed = await _gitService.CommitAndPushAsync(writtenFiles, ct);
                if (pushed)
                {
                    _logger.LogInformation("Successfully committed and pushed {Count} stories", publishedCount);
                }
                else
                {
                    _logger.LogError("Failed to commit/push stories");
                }
            }

            _logger.LogInformation("=== Pipeline run complete. Published {Count} stories ===", publishedCount);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Pipeline run cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pipeline run failed with error");
        }
    }
}
