using System.Text.Json;
using Authosy.Service.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Authosy.Service.Services;

public class StateService
{
    private readonly string _stateFilePath;
    private readonly ILogger<StateService> _logger;
    private readonly object _lock = new();
    private PublishedState _state;

    public StateService(IOptions<AuthosyConfig> config, ILogger<StateService> logger)
    {
        var cfg = config.Value;
        _stateFilePath = Path.IsPathRooted(cfg.StateFilePath)
            ? cfg.StateFilePath
            : Path.Combine(cfg.RepoPath, cfg.StateFilePath);
        _logger = logger;
        _state = Load();
    }

    public bool IsAlreadyPublished(string sourceUrl)
    {
        lock (_lock) { return _state.PublishedSourceUrls.Contains(sourceUrl); }
    }

    public void MarkPublished(IEnumerable<string> sourceUrls)
    {
        lock (_lock)
        {
            foreach (var url in sourceUrls)
            {
                _state.PublishedSourceUrls.Add(url);
            }
            _state.LastUpdated = DateTime.UtcNow;
            Save();
        }
    }

    private PublishedState Load()
    {
        try
        {
            if (File.Exists(_stateFilePath))
            {
                var json = File.ReadAllText(_stateFilePath);
                return JsonSerializer.Deserialize<PublishedState>(json) ?? new PublishedState();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load state file, starting fresh");
        }
        return new PublishedState();
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_stateFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_stateFilePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save state file");
        }
    }
}
