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

        await RunPipelineAsync(stoppingToken);

        if (_runOnce)
        {
            _logger.LogInformation("RunOnce mode: exiting after single run");
            return;
        }

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

            if (allItems.Count == 0) { _logger.LogInformation("No items, skipping"); return; }

            // Step 2: Filter already-published
            var newItems = allItems
                .Where(item => !_stateService.IsAlreadyPublished(item.Url))
                .ToList();
            _logger.LogInformation("{Count} new items after filtering published", newItems.Count);

            if (newItems.Count == 0) { _logger.LogInformation("No new items, skipping"); return; }

            // Step 3: Parallel classification — classify ALL items, not just a subset
            _logger.LogInformation("Step 3: Classifying {Count} items in parallel (max {Concurrency} concurrent)...",
                newItems.Count, _config.ClassifyConcurrency);

            var positiveItems = await ClassifyInParallelAsync(newItems, ct);
            _logger.LogInformation("{Count} items classified as positive", positiveItems.Count);

            if (positiveItems.Count == 0) { _logger.LogInformation("No positive items, skipping"); return; }

            // Step 4: Cluster items
            _logger.LogInformation("Step 4: Clustering...");
            var clusters = _clusteringService.ClusterItems(positiveItems);

            // Step 4b: Also add single-source stories from trusted positive-news outlets
            var clusteredUrls = new HashSet<string>(clusters.SelectMany(c => c.Items.Select(i => i.Url)));
            var trustedSingles = positiveItems
                .Where(item => !clusteredUrls.Contains(item.Url))
                .Where(item => _config.TrustedPositiveSources.Any(s =>
                    item.SourceName.Contains(s, StringComparison.OrdinalIgnoreCase)))
                .Take(_config.MaxItemsPerRun)
                .Select(item => new StoryCluster
                {
                    Items = new List<FeedItem> { item },
                    PrimaryTitle = item.Title,
                    Region = item.Region
                })
                .ToList();

            var allPublishable = clusters.Concat(trustedSingles)
                .Take(_config.MaxItemsPerRun)
                .ToList();

            _logger.LogInformation("{Clustered} multi-source clusters + {Singles} trusted single-source = {Total} publishable stories",
                clusters.Count, trustedSingles.Count, allPublishable.Count);

            if (allPublishable.Count == 0) { _logger.LogInformation("No publishable stories, skipping"); return; }

            // Step 5: Parallel rewrite + ship-as-you-go
            _logger.LogInformation("Step 5: Rewriting and shipping {Count} stories in parallel (max {Concurrency} concurrent)...",
                allPublishable.Count, _config.RewriteConcurrency);

            var totalPublished = await RewriteAndShipInParallelAsync(allPublishable, ct);

            _logger.LogInformation("=== Pipeline run complete. Published {Count} stories ===", totalPublished);
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

    private async Task<List<FeedItem>> ClassifyInParallelAsync(List<FeedItem> items, CancellationToken ct)
    {
        var positiveItems = new List<FeedItem>();
        var lockObj = new object();
        var semaphore = new SemaphoreSlim(_config.ClassifyConcurrency);
        var completed = 0;
        var total = items.Count;

        var tasks = items.Select(async item =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var classification = await _claudeService.ClassifyAsync(item, ct);
                var count = Interlocked.Increment(ref completed);

                if (count % 20 == 0 || count == total)
                    _logger.LogInformation("  Classification progress: {Done}/{Total}", count, total);

                if (classification != null && classification.IsPositive &&
                    classification.PositivityScore >= _config.MinPositivityScore)
                {
                    item.Region = classification.Region;
                    lock (lockObj)
                    {
                        positiveItems.Add(item);
                    }
                    _logger.LogInformation("  + Positive ({Score:F2}): {Title}",
                        classification.PositivityScore, item.Title);
                }
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
        return positiveItems;
    }

    private async Task<int> RewriteAndShipInParallelAsync(List<StoryCluster> clusters, CancellationToken ct)
    {
        var publishedCount = 0;
        var semaphore = new SemaphoreSlim(_config.RewriteConcurrency);

        var tasks = clusters.Select(async cluster =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var sourceUrls = cluster.Items.Select(i => i.Url).ToList();
                var storyId = _markdownService.GenerateStoryId(sourceUrls);

                _logger.LogInformation("  Rewriting: {Title} ({Count} source(s))",
                    cluster.PrimaryTitle, cluster.Items.Count);

                var result = await _claudeService.RewriteAsync(cluster, ct);
                if (result == null)
                {
                    _logger.LogWarning("  Failed to rewrite: {Title}", cluster.PrimaryTitle);
                    return;
                }

                result.Id = storyId;
                result.SourceUrls = sourceUrls;

                // Write markdown immediately
                var filepath = _markdownService.WriteStory(result);
                _stateService.MarkPublished(sourceUrls);

                // Commit and push this story right away
                var pushed = await _gitService.CommitAndPushAsync(new List<string> { filepath }, ct);
                if (pushed)
                {
                    Interlocked.Increment(ref publishedCount);
                    _logger.LogInformation("  Shipped: {Title} -> {Id}", result.Title, storyId);
                }
                else
                {
                    _logger.LogWarning("  Wrote but failed to push: {Title}", result.Title);
                    Interlocked.Increment(ref publishedCount); // still count it
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "  Error processing cluster: {Title}", cluster.PrimaryTitle);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
        return publishedCount;
    }
}
