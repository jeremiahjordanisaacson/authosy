namespace Authosy.Service.Models;

public class FeedItem
{
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Url { get; set; } = "";
    public string Region { get; set; } = "";
    public string SourceName { get; set; } = "";
    public DateTimeOffset PublishedDate { get; set; }
}

public class ClassificationResult
{
    public double PositivityScore { get; set; }
    public bool IsPositive { get; set; }
    public string Region { get; set; } = "";
    public List<string> Tags { get; set; } = new();
    public string Summary { get; set; } = "";
}

public class RewriteResult
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Region { get; set; } = "";
    public string Summary { get; set; } = "";
    public List<string> Tags { get; set; } = new();
    public double PositivityScore { get; set; }
    public double ConfidenceScore { get; set; }
    public string Body { get; set; } = "";
    public List<string> SourceUrls { get; set; } = new();
}

public class StoryCluster
{
    public List<FeedItem> Items { get; set; } = new();
    public string PrimaryTitle { get; set; } = "";
    public string Region { get; set; } = "";
}

public class PublishedState
{
    public HashSet<string> PublishedSourceUrls { get; set; } = new();
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}
