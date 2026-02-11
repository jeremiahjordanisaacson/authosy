using System.Diagnostics;
using System.Text.Json;
using Authosy.Service.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Authosy.Service.Services;

public class ClaudeCliService
{
    private readonly AuthosyConfig _config;
    private readonly ILogger<ClaudeCliService> _logger;

    private static readonly string ClassifyPromptTemplate = """
        You are a news classifier. Analyze the following news item and return ONLY valid JSON (no markdown, no explanation).

        Title: {TITLE}
        Description: {DESCRIPTION}
        Source: {SOURCE}

        Return this exact JSON structure:
        {{
          "positivity_score": <float 0-1, how uplifting/positive this story is>,
          "is_positive": <boolean, true if positivity_score >= 0.6>,
          "region": "<seattle|usa|world>",
          "tags": ["<tag1>", "<tag2>", "<tag3>"],
          "summary": "<one sentence summary>"
        }}

        Rules:
        - positivity_score: 0 = very negative, 1 = very positive
        - Filter out: crime, disasters, political conflict, sensationalism
        - Tags: 3-5 descriptive tags like "community", "science", "environment", "health", "education", "technology", "arts"
        - Region should match the geographic scope: seattle for PNW/Seattle area, usa for national US, world for international
        """;

    private static readonly string RewritePromptTemplate = """
        You are a journalist for Authosy, an uplifting news site. Rewrite the following verified story and return ONLY valid JSON (no markdown, no explanation).

        Sources:
        {SOURCES}

        Return this exact JSON structure:
        {{
          "title": "<compelling, original headline, no clickbait>",
          "region": "<seattle|usa|world>",
          "summary": "<2-3 sentence summary>",
          "tags": ["<tag1>", "<tag2>", "<tag3>"],
          "positivity_score": <float 0-1>,
          "confidence_score": <float 0-1, based on how well sources corroborate>,
          "body": "<full article body in markdown format>"
        }}

        Writing rules:
        - Use entirely original phrasing. Do NOT copy paragraphs from sources.
        - Minimal short quotes only when necessary for direct attribution.
        - Tone: optimistic, grounded, factual, non-sensational. No political takes.
        - Length: 500 to 900 words.
        - Structure: opening paragraph, 2-3 body sections, closing paragraph.
        - Do NOT end with a Sources list in the body - sources are handled separately.
        - Use markdown formatting (## for section headers if needed).
        """;

    private static readonly string RepairPromptTemplate = """
        The previous response was not valid JSON. Here is the error: {ERROR}

        Please fix the JSON and return ONLY the corrected valid JSON object. No markdown code fences, no explanation, just the raw JSON.
        """;

    public ClaudeCliService(IOptions<AuthosyConfig> config, ILogger<ClaudeCliService> logger)
    {
        _config = config.Value;
        _logger = logger;
    }

    public async Task<ClassificationResult?> ClassifyAsync(FeedItem item, CancellationToken ct)
    {
        var prompt = ClassifyPromptTemplate
            .Replace("{TITLE}", item.Title)
            .Replace("{DESCRIPTION}", item.Description)
            .Replace("{SOURCE}", item.SourceName);

        var json = await CallClaudeWithRetry(prompt, ct);
        if (json == null) return null;

        try
        {
            return JsonSerializer.Deserialize<ClassificationResult>(json, JsonOpts);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize classification result");
            return null;
        }
    }

    public async Task<RewriteResult?> RewriteAsync(StoryCluster cluster, CancellationToken ct)
    {
        var sourcesText = string.Join("\n\n", cluster.Items.Select((item, i) =>
            $"Source {i + 1}: {item.SourceName}\nURL: {item.Url}\nTitle: {item.Title}\nSnippet: {item.Description}"));

        var prompt = RewritePromptTemplate.Replace("{SOURCES}", sourcesText);

        var json = await CallClaudeWithRetry(prompt, ct);
        if (json == null) return null;

        try
        {
            var result = JsonSerializer.Deserialize<RewriteResult>(json, JsonOpts);
            if (result != null)
            {
                result.SourceUrls = cluster.Items.Select(i => i.Url).Distinct().ToList();
            }
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize rewrite result");
            return null;
        }
    }

    private async Task<string?> CallClaudeWithRetry(string prompt, CancellationToken ct)
    {
        string? lastOutput = null;
        string? lastError = null;

        for (int attempt = 0; attempt <= _config.MaxRetries; attempt++)
        {
            var currentPrompt = attempt == 0
                ? prompt
                : RepairPromptTemplate.Replace("{ERROR}", lastError ?? "Invalid JSON") + "\n\nOriginal prompt:\n" + prompt;

            lastOutput = await CallClaudeCli(currentPrompt, ct);
            if (lastOutput == null) return null;

            // Try to extract JSON from the output
            var json = ExtractJson(lastOutput);
            if (json != null)
            {
                // Validate it parses
                try
                {
                    JsonDocument.Parse(json);
                    return json;
                }
                catch (JsonException ex)
                {
                    lastError = ex.Message;
                    _logger.LogWarning("Attempt {Attempt}: JSON parse failed: {Error}", attempt + 1, ex.Message);
                }
            }
            else
            {
                lastError = "No JSON object found in output";
                _logger.LogWarning("Attempt {Attempt}: No JSON found in Claude output", attempt + 1);
            }
        }

        _logger.LogError("All {Max} attempts to get valid JSON from Claude failed", _config.MaxRetries + 1);
        return null;
    }

    private async Task<string?> CallClaudeCli(string prompt, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _config.ClaudeCliPath,
                Arguments = "--print",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi };
            process.Start();

            // Write prompt to stdin
            await process.StandardInput.WriteAsync(prompt);
            process.StandardInput.Close();

            // Read output with timeout
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(_config.ClaudeTimeoutSeconds));

            var outputTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
                var output = await outputTask;
                var error = await errorTask;

                if (!string.IsNullOrWhiteSpace(error))
                {
                    _logger.LogWarning("Claude CLI stderr: {Error}", error);
                }

                if (process.ExitCode != 0)
                {
                    _logger.LogError("Claude CLI exited with code {Code}", process.ExitCode);
                    return null;
                }

                return output;
            }
            catch (OperationCanceledException)
            {
                _logger.LogError("Claude CLI timed out after {Timeout}s, killing process", _config.ClaudeTimeoutSeconds);
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to invoke Claude CLI");
            return null;
        }
    }

    private static string? ExtractJson(string text)
    {
        // Try to find a JSON object in the text
        text = text.Trim();

        // Remove markdown code fences if present
        if (text.Contains("```"))
        {
            var start = text.IndexOf("```");
            var afterFence = text.IndexOf('\n', start);
            if (afterFence >= 0)
            {
                var end = text.IndexOf("```", afterFence);
                if (end >= 0)
                {
                    text = text[(afterFence + 1)..end].Trim();
                }
            }
        }

        // Find first { and last }
        var firstBrace = text.IndexOf('{');
        var lastBrace = text.LastIndexOf('}');
        if (firstBrace >= 0 && lastBrace > firstBrace)
        {
            return text[firstBrace..(lastBrace + 1)];
        }

        return null;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };
}
