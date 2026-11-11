using System.Globalization;
using System.IO.Compression;
using System.Net.Http;

namespace Jellyfin.Plugin.ImdbRatings;

/// <summary>Shared cache for IMDb's official non-commercial datasets.</summary>
public static class ImdbDatasetCache
{
    private static readonly HttpClient _httpClient = new();
    private static Dictionary<string, (double Rating, int Votes)>? _ratingsCache;
    private static List<(string Id, double WeightedRating)>? _top250Cache;
    private static DateTime _cacheTime = DateTime.MinValue;
    private static readonly SemaphoreSlim _lock = new(1, 1);

    public static async Task<Dictionary<string, (double Rating, int Votes)>> GetRatingsAsync(
        TimeSpan maxAge, CancellationToken cancellationToken, bool forceRefresh = false)
    {
        if (!forceRefresh && _ratingsCache != null && DateTime.UtcNow - _cacheTime < maxAge)
            return _ratingsCache;

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && _ratingsCache != null && DateTime.UtcNow - _cacheTime < maxAge)
                return _ratingsCache;

            await RefreshCacheAsync(cancellationToken).ConfigureAwait(false);
            return _ratingsCache ?? new Dictionary<string, (double, int)>();
        }
        finally
        {
            _lock.Release();
        }
    }

    private static Dictionary<string, int>? _chartFileCache;
    private static DateTime _chartFileTime = DateTime.MinValue;

    /// <summary>Path to a local JSON file with the real IMDb chart (fetched externally).</summary>
    private static string ChartFilePath => Plugin.DataFile("imdb_chart.json");

    public static async Task<Dictionary<string, int>> GetTop250Async(
        TimeSpan maxAge, CancellationToken cancellationToken)
    {
        // Prefer local chart file if it exists and is fresh
        var chartFromFile = TryLoadChartFile(maxAge);
        if (chartFromFile != null)
            return chartFromFile;

        // Fall back to computed ranking from datasets
        if (_top250Cache != null && DateTime.UtcNow - _cacheTime < maxAge)
            return _top250Cache
                .Select((r, i) => (r.Id, Rank: i + 1))
                .ToDictionary(r => r.Id, r => r.Rank);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_top250Cache != null && DateTime.UtcNow - _cacheTime < maxAge)
                return _top250Cache
                    .Select((r, i) => (r.Id, Rank: i + 1))
                    .ToDictionary(r => r.Id, r => r.Rank);

            await RefreshCacheAsync(cancellationToken).ConfigureAwait(false);

            return _top250Cache?
                .Select((r, i) => (r.Id, Rank: i + 1))
                .ToDictionary(r => r.Id, r => r.Rank)
                ?? new Dictionary<string, int>();
        }
        finally
        {
            _lock.Release();
        }
    }

    private static Dictionary<string, int>? TryLoadChartFile(TimeSpan maxAge)
    {
        try
        {
            var chartFilePath = ChartFilePath;
            if (_chartFileCache != null && DateTime.UtcNow - _chartFileTime < maxAge)
                return _chartFileCache;

            if (!File.Exists(chartFilePath))
                return null;

            var fileAge = DateTime.UtcNow - File.GetLastWriteTimeUtc(chartFilePath);
            if (fileAge > TimeSpan.FromDays(7))
                return null; // too stale, fall back to datasets

            var json = File.ReadAllText(chartFilePath);
            var parsed = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, int>>(json);
            if (parsed != null && parsed.Count >= 200)
            {
                _chartFileCache = parsed;
                _chartFileTime = DateTime.UtcNow;
                return parsed;
            }
        }
        catch { }
        return null;
    }

    private static async Task RefreshCacheAsync(CancellationToken cancellationToken)
    {
        const string basicsUrl = "https://datasets.imdbws.com/title.basics.tsv.gz";
        const string ratingsUrl = "https://datasets.imdbws.com/title.ratings.tsv.gz";
        const int minVotes = 25_000;

        // Pass 1: collect non-adult movie tconsts
        var movieIds = new HashSet<string>(StringComparer.Ordinal);
        using (var request = new HttpRequestMessage(HttpMethod.Get, basicsUrl))
        {
            using var response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return;

            await using var gz = new GZipStream(
                await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                CompressionMode.Decompress);
            using var reader = new StreamReader(gz);
            await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false); // skip header

            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                // tconst \t titleType \t primaryTitle \t originalTitle \t isAdult \t ...
                var cols = line.Split('\t');
                if (cols.Length < 5) continue;
                if (cols[1] != "movie") continue;
                if (cols[4] == "1") continue; // skip adult
                movieIds.Add(cols[0]);
            }
        }

        // Pass 2: collect ratings for movies
        var allRatings = new Dictionary<string, (double Rating, int Votes)>(StringComparer.Ordinal);
        var qualifyingRatings = new List<(string Id, double Rating, int Votes)>();

        using (var request = new HttpRequestMessage(HttpMethod.Get, ratingsUrl))
        {
            using var response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return;

            await using var gz = new GZipStream(
                await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                CompressionMode.Decompress);
            using var reader = new StreamReader(gz);
            await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false); // skip header

            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                var parts = line.Split('\t');
                if (parts.Length < 3) continue;
                if (!movieIds.Contains(parts[0])) continue;
                if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var rating)) continue;
                if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var votes)) continue;

                allRatings[parts[0]] = (rating, votes);

                if (votes >= minVotes)
                    qualifyingRatings.Add((parts[0], rating, votes));
            }
        }

        _ratingsCache = allRatings;

        if (qualifyingRatings.Count > 0)
        {
            // Bayesian weighted rating: WR = (v/(v+m)) * R + (m/(v+m)) * C
            // Take top 350 to account for regional films IMDb's chart may exclude
            var meanRating = qualifyingRatings.Average(r => r.Rating);
            _top250Cache = qualifyingRatings
                .Select(r =>
                {
                    var wr = (r.Votes / (double)(r.Votes + minVotes)) * r.Rating
                           + (minVotes / (double)(r.Votes + minVotes)) * meanRating;
                    return (r.Id, WeightedRating: wr);
                })
                .OrderByDescending(r => r.WeightedRating)
                .Take(350)
                .ToList();
        }

        _cacheTime = DateTime.UtcNow;
    }
}
