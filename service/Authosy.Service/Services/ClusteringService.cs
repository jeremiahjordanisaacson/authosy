using Authosy.Service.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Authosy.Service.Services;

public class ClusteringService
{
    private readonly AuthosyConfig _config;
    private readonly ILogger<ClusteringService> _logger;

    public ClusteringService(IOptions<AuthosyConfig> config, ILogger<ClusteringService> logger)
    {
        _config = config.Value;
        _logger = logger;
    }

    public List<StoryCluster> ClusterItems(List<FeedItem> items)
    {
        if (items.Count == 0) return new List<StoryCluster>();

        // Build TF-IDF vectors
        var documents = items.Select(i => Tokenize($"{i.Title} {i.Description}")).ToList();
        var vocabulary = BuildVocabulary(documents);
        var tfidfVectors = documents.Select(doc => ComputeTfIdf(doc, documents, vocabulary)).ToList();

        // Greedy clustering using cosine similarity
        var assigned = new bool[items.Count];
        var clusters = new List<StoryCluster>();

        for (int i = 0; i < items.Count; i++)
        {
            if (assigned[i]) continue;

            var cluster = new StoryCluster
            {
                Items = new List<FeedItem> { items[i] },
                PrimaryTitle = items[i].Title,
                Region = items[i].Region
            };

            var sourceDomains = new HashSet<string> { GetDomain(items[i].Url) };

            for (int j = i + 1; j < items.Count; j++)
            {
                if (assigned[j]) continue;

                var similarity = CosineSimilarity(tfidfVectors[i], tfidfVectors[j]);
                if (similarity >= _config.CosineSimilarityThreshold)
                {
                    var domain = GetDomain(items[j].Url);
                    // Only add if from a different source domain
                    if (!sourceDomains.Contains(domain))
                    {
                        cluster.Items.Add(items[j]);
                        sourceDomains.Add(domain);
                        assigned[j] = true;
                    }
                }
            }

            assigned[i] = true;

            // Determine majority region
            var regionCounts = cluster.Items.GroupBy(x => x.Region)
                .OrderByDescending(g => g.Count())
                .First();
            cluster.Region = regionCounts.Key;

            clusters.Add(cluster);
        }

        var validClusters = clusters
            .Where(c => c.Items.Count >= _config.MinClusterSize)
            .OrderByDescending(c => c.Items.Count)
            .Take(_config.MaxItemsPerRun)
            .ToList();

        _logger.LogInformation(
            "Clustered {Total} items into {Clusters} clusters, {Valid} meet min size of {Min}",
            items.Count, clusters.Count, validClusters.Count, _config.MinClusterSize);

        return validClusters;
    }

    private static List<string> Tokenize(string text)
    {
        return text.ToLowerInvariant()
            .Split(new[] { ' ', '\t', '\n', '\r', '.', ',', '!', '?', ';', ':', '"', '\'', '(', ')', '[', ']', '{', '}', '-', '/', '\\' },
                StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 2)
            .ToList();
    }

    private static Dictionary<string, int> BuildVocabulary(List<List<string>> documents)
    {
        var vocab = new Dictionary<string, int>();
        int idx = 0;
        foreach (var doc in documents)
        {
            foreach (var token in doc.Distinct())
            {
                if (!vocab.ContainsKey(token))
                    vocab[token] = idx++;
            }
        }
        return vocab;
    }

    private static double[] ComputeTfIdf(List<string> document, List<List<string>> allDocuments, Dictionary<string, int> vocabulary)
    {
        var vector = new double[vocabulary.Count];
        var termCounts = document.GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count());
        int docCount = allDocuments.Count;

        foreach (var (term, count) in termCounts)
        {
            if (!vocabulary.TryGetValue(term, out int idx)) continue;

            double tf = (double)count / document.Count;
            int df = allDocuments.Count(d => d.Contains(term));
            double idf = Math.Log((double)(docCount + 1) / (df + 1)) + 1;
            vector[idx] = tf * idf;
        }

        return vector;
    }

    private static double CosineSimilarity(double[] a, double[] b)
    {
        double dot = 0, magA = 0, magB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }

        if (magA == 0 || magB == 0) return 0;
        return dot / (Math.Sqrt(magA) * Math.Sqrt(magB));
    }

    private static string GetDomain(string url)
    {
        try { return new Uri(url).Host.ToLowerInvariant(); }
        catch { return url; }
    }
}
