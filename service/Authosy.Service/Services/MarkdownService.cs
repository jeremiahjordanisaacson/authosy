using System.Security.Cryptography;
using System.Text;
using Authosy.Service.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Authosy.Service.Services;

public class MarkdownService
{
    private readonly AuthosyConfig _config;
    private readonly ILogger<MarkdownService> _logger;

    public MarkdownService(IOptions<AuthosyConfig> config, ILogger<MarkdownService> logger)
    {
        _config = config.Value;
        _logger = logger;
    }

    public string GenerateStoryId(List<string> sourceUrls)
    {
        var input = string.Join("|", sourceUrls.OrderBy(u => u));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    public string GenerateFilename(string id, string title)
    {
        var slug = GenerateSlug(title);
        return $"{id}-{slug}.md";
    }

    public string GenerateMarkdown(RewriteResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"id: \"{result.Id}\"");
        sb.AppendLine($"title: \"{EscapeYaml(result.Title)}\"");
        sb.AppendLine($"region: \"{result.Region}\"");
        sb.AppendLine($"date_published: \"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}\"");
        sb.AppendLine($"summary: \"{EscapeYaml(result.Summary)}\"");
        sb.AppendLine("tags:");
        foreach (var tag in result.Tags)
        {
            sb.AppendLine($"  - \"{EscapeYaml(tag)}\"");
        }
        sb.AppendLine("source_urls:");
        foreach (var url in result.SourceUrls)
        {
            sb.AppendLine($"  - \"{url}\"");
        }
        sb.AppendLine($"positivity_score: {result.PositivityScore:F2}");
        sb.AppendLine($"confidence_score: {result.ConfidenceScore:F2}");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine(result.Body);

        return sb.ToString();
    }

    public string WriteStory(RewriteResult result)
    {
        var contentPath = Path.IsPathRooted(_config.SiteContentPath)
            ? _config.SiteContentPath
            : Path.Combine(_config.RepoPath, _config.SiteContentPath);

        Directory.CreateDirectory(contentPath);

        var filename = GenerateFilename(result.Id, result.Title);
        var filepath = Path.Combine(contentPath, filename);

        var markdown = GenerateMarkdown(result);
        File.WriteAllText(filepath, markdown, Encoding.UTF8);

        _logger.LogInformation("Wrote story: {Filename}", filename);
        return filepath;
    }

    private static string GenerateSlug(string title)
    {
        var slug = title.ToLowerInvariant();
        slug = System.Text.RegularExpressions.Regex.Replace(slug, @"[^a-z0-9\s-]", "");
        slug = System.Text.RegularExpressions.Regex.Replace(slug, @"\s+", "-");
        slug = slug.Trim('-');
        if (slug.Length > 60) slug = slug[..60].TrimEnd('-');
        return slug;
    }

    private static string EscapeYaml(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", "");
    }
}
