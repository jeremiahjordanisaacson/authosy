using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Authosy.Service.Models;

namespace Authosy.Service.Services;

public class GitService
{
    private readonly AuthosyConfig _config;
    private readonly ILogger<GitService> _logger;

    public GitService(IOptions<AuthosyConfig> config, ILogger<GitService> logger)
    {
        _config = config.Value;
        _logger = logger;
    }

    public async Task<bool> CommitAndPushAsync(List<string> filePaths, CancellationToken ct)
    {
        var repoPath = _config.RepoPath;
        if (string.IsNullOrEmpty(repoPath))
        {
            _logger.LogError("RepoPath is not configured");
            return false;
        }

        try
        {
            // Stage all new story files
            foreach (var path in filePaths)
            {
                var relativePath = Path.GetRelativePath(repoPath, path).Replace('\\', '/');
                var result = await RunGit($"add \"{relativePath}\"", repoPath, ct);
                if (!result.Success)
                {
                    _logger.LogError("Failed to stage {Path}: {Error}", relativePath, result.Error);
                    return false;
                }
            }

            // Also stage the state file
            var stateRelative = Path.GetRelativePath(repoPath, Path.Combine(repoPath, _config.StateFilePath)).Replace('\\', '/');
            await RunGit($"add \"{stateRelative}\"", repoPath, ct);

            // Check if there are changes to commit
            var statusResult = await RunGit("status --porcelain", repoPath, ct);
            if (string.IsNullOrWhiteSpace(statusResult.Output))
            {
                _logger.LogInformation("No changes to commit");
                return true;
            }

            // Commit
            var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HHmm");
            var commitMessage = $"Add stories {timestamp}";
            var commitResult = await RunGit($"commit -m \"{commitMessage}\"", repoPath, ct);
            if (!commitResult.Success)
            {
                _logger.LogError("Failed to commit: {Error}", commitResult.Error);
                return false;
            }

            _logger.LogInformation("Committed: {Message}", commitMessage);

            // Push with retry
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                var pushResult = await RunGit("push origin main", repoPath, ct);
                if (pushResult.Success)
                {
                    _logger.LogInformation("Pushed to remote successfully");
                    return true;
                }

                _logger.LogWarning("Push attempt {Attempt} failed: {Error}", attempt, pushResult.Error);

                if (attempt < 3)
                {
                    // Pull and retry
                    await RunGit("pull --rebase origin main", repoPath, ct);
                    await Task.Delay(2000, ct);
                }
            }

            _logger.LogError("Failed to push after 3 attempts");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Git commit/push failed");
            return false;
        }
    }

    private async Task<GitResult> RunGit(string arguments, string workingDirectory, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // Configure git to use token from environment variable for authentication
        var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (!string.IsNullOrEmpty(token) && !string.IsNullOrEmpty(_config.GitRemoteUrl))
        {
            // Set credential helper to use the token
            psi.Environment["GIT_ASKPASS"] = "echo";
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        }

        using var process = new Process { StartInfo = psi };
        process.Start();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(60));

        try
        {
            var output = await process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var error = await process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token);

            return new GitResult
            {
                Success = process.ExitCode == 0,
                Output = output,
                Error = error
            };
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return new GitResult { Success = false, Error = "Git command timed out" };
        }
    }

    private record GitResult
    {
        public bool Success { get; init; }
        public string Output { get; init; } = "";
        public string Error { get; init; } = "";
    }
}
