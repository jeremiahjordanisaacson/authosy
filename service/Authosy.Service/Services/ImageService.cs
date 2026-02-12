using System.Text;
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
        ClaudeCliService claudeService)
    {
        _config = config.Value;
        _logger = logger;
        _claudeService = claudeService;
    }

    public async Task<ImageGenerationResult?> GenerateImagesForStoryAsync(RewriteResult story, CancellationToken ct)
    {
        if (!_config.ImageGeneration.Enabled)
        {
            _logger.LogInformation("Image generation is disabled");
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
                // Step 1: Generate image prompt text using Copilot SDK
                var imagePrompt = await GenerateImagePromptAsync(story.Title, section.Heading, section.Text, ct);
                if (string.IsNullOrEmpty(imagePrompt))
                {
                    _logger.LogWarning("Failed to generate image prompt for section: {Section}", section.Heading);
                    continue;
                }

                // Step 2: Generate image via Copilot SDK using ResponseFormat.Image
                var imageData = await _claudeService.GenerateImageAsync(imagePrompt, _config.ImageGeneration, ct);
                if (imageData == null)
                {
                    _logger.LogWarning("Failed to generate image for section: {Section}", section.Heading);
                    continue;
                }

                // Save image
                var imageFilename = $"section-{i + 1}.{imageData.Format}";
                var imagePath = Path.Combine(imageDir, imageFilename);
                var imageBytes = Convert.FromBase64String(imageData.Base64);
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
                body = imageMarkdown + "\n" + body;
            }
            else
            {
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
