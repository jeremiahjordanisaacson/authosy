using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Authosy.Service.Models;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Authosy.Service.Services;

public class ImageService
{
    private readonly AuthosyConfig _config;
    private readonly ILogger<ImageService> _logger;
    private readonly HttpClient _httpClient;
    private readonly ClaudeCliService _claudeService;

    private static readonly string ImagePromptTemplate = """
        Given the following news article section, generate a detailed image prompt for a photo-realistic illustration.
        The image should be warm, optimistic, and visually compelling.

        Article title: {TITLE}
        Section heading: {SECTION}
        Section text: {TEXT}

        Return ONLY a single-line image prompt (no JSON, no explanation). The prompt should describe:
        - A specific, concrete scene (not abstract)
        - Lighting and atmosphere
        - Key visual elements
        - Photo-realistic style
        Keep it under 200 words.
        """;

    public ImageService(
        IOptions<AuthosyConfig> config,
        ILogger<ImageService> logger,
        HttpClient httpClient,
        ClaudeCliService claudeService)
    {
        _config = config.Value;
        _logger = logger;
        _httpClient = httpClient;
        _claudeService = claudeService;
    }

    public async Task<ImageGenerationResult?> GenerateImagesForStoryAsync(RewriteResult story, CancellationToken ct)
    {
        if (!_config.ImageGeneration.Enabled)
        {
            _logger.LogInformation("Image generation is disabled");
            return null;
        }

        var openAiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrEmpty(openAiKey))
        {
            _logger.LogWarning("OPENAI_API_KEY not set, skipping image generation");
            return null;
        }

        var sections = ParseSections(story.Body);
        if (sections.Count == 0)
        {
            _logger.LogInformation("No sections found in story body, skipping image generation");
            return null;
        }

        // Limit to configured max
        var sectionsToProcess = sections.Take(_config.ImageGeneration.MaxImagesPerStory).ToList();

        var imageDir = GetImageDirectory(story.Id);
        Directory.CreateDirectory(imageDir);

        var imageFiles = new List<string>();
        var updatedBody = story.Body;
        var insertions = new List<(string afterHeading, string imageMarkdown)>();

        for (int i = 0; i < sectionsToProcess.Count; i++)
        {
            var section = sectionsToProcess[i];
            try
            {
                // Generate image prompt using Copilot SDK
                var imagePrompt = await GenerateImagePromptAsync(story.Title, section.Heading, section.Text, ct);
                if (string.IsNullOrEmpty(imagePrompt))
                {
                    _logger.LogWarning("Failed to generate image prompt for section: {Section}", section.Heading);
                    continue;
                }

                // Call OpenAI Images API
                var imageBytes = await CallOpenAiImagesApiAsync(imagePrompt, openAiKey, ct);
                if (imageBytes == null)
                {
                    _logger.LogWarning("Failed to generate image for section: {Section}", section.Heading);
                    continue;
                }

                // Save image
                var imageFilename = $"section-{i + 1}.png";
                var imagePath = Path.Combine(imageDir, imageFilename);
                await File.WriteAllBytesAsync(imagePath, imageBytes, ct);
                imageFiles.Add(imagePath);

                // Build the markdown image reference (relative to site public dir)
                var relativeImagePath = $"/authosy/images/stories/{story.Id}/{imageFilename}";
                var altText = string.IsNullOrEmpty(section.Heading) ? story.Title : section.Heading;
                var imageMarkdown = $"\n![{EscapeMarkdown(altText)}]({relativeImagePath})\n";

                insertions.Add((section.Heading, imageMarkdown));

                _logger.LogInformation("Generated image {Index}/{Total} for: {Section}",
                    i + 1, sectionsToProcess.Count, section.Heading);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error generating image for section {Index}: {Section}", i, section.Heading);
            }
        }

        // Insert image references into the body after each h2 heading
        updatedBody = InsertImagesIntoBody(updatedBody, insertions);

        return new ImageGenerationResult
        {
            Body = updatedBody,
            ImageFiles = imageFiles
        };
    }

    private List<StorySection> ParseSections(string body)
    {
        var sections = new List<StorySection>();
        var lines = body.Split('\n');
        var currentHeading = "";
        var currentText = new StringBuilder();

        // Add intro section (before first h2)
        var introText = new StringBuilder();
        var foundFirstH2 = false;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith("## "))
            {
                if (!foundFirstH2)
                {
                    // Save intro
                    var intro = introText.ToString().Trim();
                    if (!string.IsNullOrEmpty(intro))
                    {
                        sections.Add(new StorySection { Heading = "Introduction", Text = intro });
                    }
                    foundFirstH2 = true;
                }
                else if (!string.IsNullOrEmpty(currentHeading))
                {
                    sections.Add(new StorySection { Heading = currentHeading, Text = currentText.ToString().Trim() });
                }

                currentHeading = line[3..].Trim();
                currentText.Clear();
            }
            else
            {
                if (!foundFirstH2)
                    introText.AppendLine(line);
                else
                    currentText.AppendLine(line);
            }
        }

        // Add last section
        if (!string.IsNullOrEmpty(currentHeading))
        {
            sections.Add(new StorySection { Heading = currentHeading, Text = currentText.ToString().Trim() });
        }
        else if (!foundFirstH2)
        {
            // No h2 headers at all — treat the whole body as one section
            var fullText = body.Trim();
            if (!string.IsNullOrEmpty(fullText))
            {
                sections.Add(new StorySection { Heading = "Article", Text = fullText });
            }
        }

        return sections;
    }

    private async Task<string?> GenerateImagePromptAsync(string title, string sectionHeading, string sectionText, CancellationToken ct)
    {
        var prompt = ImagePromptTemplate
            .Replace("{TITLE}", title)
            .Replace("{SECTION}", sectionHeading)
            .Replace("{TEXT}", sectionText.Length > 500 ? sectionText[..500] : sectionText);

        var result = await _claudeService.SendPromptAsync(prompt, ct);
        return result?.Trim();
    }

    private async Task<byte[]?> CallOpenAiImagesApiAsync(string prompt, string apiKey, CancellationToken ct)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/images/generations")
            {
                Headers = { { "Authorization", $"Bearer {apiKey}" } },
                Content = JsonContent.Create(new
                {
                    model = "dall-e-3",
                    prompt = prompt,
                    n = 1,
                    size = _config.ImageGeneration.ImageSize,
                    response_format = "b64_json"
                })
            };

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(120));

            var response = await _httpClient.SendAsync(request, timeoutCts.Token);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                _logger.LogError("OpenAI Images API error ({Status}): {Error}", response.StatusCode, errorBody);
                return null;
            }

            var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            var b64 = json.GetProperty("data")[0].GetProperty("b64_json").GetString();
            if (string.IsNullOrEmpty(b64)) return null;

            return Convert.FromBase64String(b64);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to call OpenAI Images API");
            return null;
        }
    }

    private string GetImageDirectory(string storyId)
    {
        var publicPath = Path.IsPathRooted(_config.SiteContentPath)
            ? Path.GetFullPath(Path.Combine(_config.SiteContentPath, "..", "..", "public"))
            : Path.Combine(_config.RepoPath, "site", "public");

        return Path.Combine(publicPath, "images", "stories", storyId);
    }

    private static string InsertImagesIntoBody(string body, List<(string afterHeading, string imageMarkdown)> insertions)
    {
        if (insertions.Count == 0) return body;

        foreach (var (heading, imageMarkdown) in insertions)
        {
            if (heading == "Introduction" || heading == "Article")
            {
                // Insert image at the very beginning of the body
                body = imageMarkdown + "\n" + body;
            }
            else
            {
                // Insert after the ## heading line
                var headingPattern = $"## {Regex.Escape(heading)}";
                var match = Regex.Match(body, headingPattern);
                if (match.Success)
                {
                    var insertPos = match.Index + match.Length;
                    body = body[..insertPos] + "\n" + imageMarkdown + body[insertPos..];
                }
            }
        }

        return body;
    }

    private static string EscapeMarkdown(string text)
    {
        return text.Replace("[", "\\[").Replace("]", "\\]");
    }

    private class StorySection
    {
        public string Heading { get; set; } = "";
        public string Text { get; set; } = "";
    }
}

public class ImageGenerationResult
{
    public string Body { get; set; } = "";
    public List<string> ImageFiles { get; set; } = new();
}
