namespace Authosy.Service.Models;

public class AuthosyConfig
{
    public int IntervalMinutes { get; set; } = 60;
    public int MaxItemsPerRun { get; set; } = 20;
    public double MinPositivityScore { get; set; } = 0.6;
    public int MinClusterSize { get; set; } = 2;
    public double CosineSimilarityThreshold { get; set; } = 0.15;
    public string ClaudeCliPath { get; set; } = "claude";
    public string CopilotModel { get; set; } = "claude-sonnet-4-5-20250929";
    public int ClaudeTimeoutSeconds { get; set; } = 120;
    public int MaxRetries { get; set; } = 2;
    public int ClassifyConcurrency { get; set; } = 5;
    public int RewriteConcurrency { get; set; } = 3;
    public string RepoPath { get; set; } = "";
    public string SiteContentPath { get; set; } = "site/src/content/stories";
    public string StateFilePath { get; set; } = "service/state.json";
    public int PerDomainDelayMs { get; set; } = 1000;
    public string GitRemoteUrl { get; set; } = "";
    public List<string> TrustedPositiveSources { get; set; } = new()
    {
        "Good News Network",
        "Positive News",
        "Reasons to be Cheerful"
    };
    public List<FeedConfig> Feeds { get; set; } = new();
}

public class FeedConfig
{
    public string Url { get; set; } = "";
    public string Region { get; set; } = "usa";
    public string Name { get; set; } = "";
}
