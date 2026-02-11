namespace Authosy.Service.Models;

public class AuthosyConfig
{
    public int IntervalMinutes { get; set; } = 60;
    public int MaxItemsPerRun { get; set; } = 10;
    public double MinPositivityScore { get; set; } = 0.6;
    public int MinClusterSize { get; set; } = 2;
    public double CosineSimilarityThreshold { get; set; } = 0.25;
    public string ClaudeCliPath { get; set; } = "claude";
    public int ClaudeTimeoutSeconds { get; set; } = 120;
    public int MaxRetries { get; set; } = 2;
    public string RepoPath { get; set; } = "";
    public string SiteContentPath { get; set; } = "site/src/content/stories";
    public string StateFilePath { get; set; } = "service/state.json";
    public int PerDomainDelayMs { get; set; } = 1000;
    public string GitRemoteUrl { get; set; } = "";
    public List<FeedConfig> Feeds { get; set; } = new();
}

public class FeedConfig
{
    public string Url { get; set; } = "";
    public string Region { get; set; } = "usa";
    public string Name { get; set; } = "";
}
