using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ItemUpdateType = MediaBrowser.Controller.Library.ItemUpdateType;

namespace Jellyfin.Plugin.ImdbRatings;

/// <summary>Fetches IMDb + RT ratings when a new movie is added to the library.</summary>
public class LibraryChangedHandler : IHostedService, IDisposable
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<LibraryChangedHandler> _logger;
    private static readonly HttpClient _httpClient = new();

    public LibraryChangedHandler(ILibraryManager libraryManager, ILogger<LibraryChangedHandler> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemAdded;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemAdded;
        return Task.CompletedTask;
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is not Movie movie) return;
        _ = Task.Run(() => ProcessNewMovie(movie));
    }

    private async Task ProcessNewMovie(Movie movie)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null) return;

        var imdbId = movie.GetProviderId(MetadataProvider.Imdb);
        if (string.IsNullOrEmpty(imdbId)) return;

        _logger.LogInformation("New movie added: {Name} ({ImdbId}), fetching ratings", movie.Name, imdbId);

        try
        {
            // Fetch IMDb rating from cached datasets
            if (config.EnableRatingsTask)
            {
                var cacheMaxAge = TimeSpan.FromHours(config.ChartCacheHours > 0 ? config.ChartCacheHours : 24);
                var ratings = await ImdbDatasetCache.GetRatingsAsync(cacheMaxAge, CancellationToken.None).ConfigureAwait(false);
                if (ratings.TryGetValue(imdbId, out var data) &&
                    (movie.CommunityRating == null || Math.Abs(movie.CommunityRating.Value - (float)data.Rating) > 0.01f))
                {
                    movie.CommunityRating = (float)data.Rating;
                    await _libraryManager.UpdateItemAsync(movie, movie.GetParent(), ItemUpdateType.MetadataEdit, CancellationToken.None).ConfigureAwait(false);
                    _logger.LogInformation("  IMDb rating: {Rating}", data.Rating);
                }
            }

            // Fetch RT scores
            if (config.EnableRtAudienceTask)
            {
                var rtResult = await FetchRtForMovie(movie.Name, movie.ProductionYear).ConfigureAwait(false);
                if (rtResult != null)
                {
                    // Append to rt_item_scores.json
                    var jfId = movie.Id.ToString("N");
                    await AppendRtScore(jfId, rtResult.Value.score, rtResult.Value.certified, rtResult.Value.criticScore, rtResult.Value.criticCertified, rtResult.Value.verifiedHot).ConfigureAwait(false);
                    _logger.LogInformation("  RT audience: {Score}%, certified: {Cert}", rtResult.Value.score, rtResult.Value.certified);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch ratings for {Name}", movie.Name);
        }
    }

    private static async Task<(int score, bool certified, int? criticScore, bool criticCertified, bool verifiedHot)?> FetchRtForMovie(string title, int? year)
    {
        var slugs = GetSlugVariants(title, year);
        foreach (var slug in slugs)
        {
            var result = await TryFetchSlug(slug).ConfigureAwait(false);
            if (result != null) return result;
            await Task.Delay(1000).ConfigureAwait(false);
        }
        // Search fallback
        return await TrySearchRt(title, year).ConfigureAwait(false);
    }

    private static async Task<(int score, bool certified, int? criticScore, bool criticCertified, bool verifiedHot)?> TryFetchSlug(string slug)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://www.rottentomatoes.com/m/{slug}");
            req.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
            using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var html = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            return ParseScorecard(html);
        }
        catch { return null; }
    }

    private static async Task<(int score, bool certified, int? criticScore, bool criticCertified, bool verifiedHot)?> TrySearchRt(string title, int? year)
    {
        try
        {
            var query = Uri.EscapeDataString(title);
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://www.rottentomatoes.com/search?search={query}");
            req.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
            using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var html = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            var pattern = @"""url"":""(/m/[^""]+)""\s*,\s*""name"":""[^""]*""\s*,\s*""year"":(\d+)";
            foreach (Match m in Regex.Matches(html, pattern))
            {
                var movieYear = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                if (year.HasValue && Math.Abs(movieYear - year.Value) > 1) continue;
                var slug = m.Groups[1].Value.Replace("/m/", "");
                await Task.Delay(1000).ConfigureAwait(false);
                var result = await TryFetchSlug(slug).ConfigureAwait(false);
                if (result != null) return result;
            }
        }
        catch { }
        return null;
    }

    private static (int score, bool certified, int? criticScore, bool criticCertified, bool verifiedHot)? ParseScorecard(string html)
    {
        var match = Regex.Match(html, @"<script[^>]*id=""media-scorecard-json""[^>]*>(.*?)</script>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        try
        {
            using var doc = JsonDocument.Parse(match.Groups[1].Value);
            var root = doc.RootElement;
            if (!root.TryGetProperty("audienceScore", out var aud)) return null;
            if (!aud.TryGetProperty("score", out var scoreEl)) return null;
            if (!int.TryParse(scoreEl.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var score))
                return null;
            var certified = aud.TryGetProperty("certified", out var ac) && ac.ValueKind == JsonValueKind.True;
            var verifiedHot = aud.TryGetProperty("verifiedHot", out var vh) && vh.ValueKind == JsonValueKind.True;
            if (!verifiedHot && aud.TryGetProperty("certifiedFresh", out var cf) &&
                cf.ValueKind == JsonValueKind.String &&
                string.Equals(cf.GetString(), "certified", StringComparison.OrdinalIgnoreCase))
                verifiedHot = true;
            if (!verifiedHot)
                verifiedHot = certified;
            int? criticScore = null;
            var criticCertified = false;
            if (root.TryGetProperty("criticsScore", out var crit))
            {
                if (crit.TryGetProperty("score", out var criticScoreEl) &&
                    int.TryParse(criticScoreEl.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedCriticScore))
                    criticScore = parsedCriticScore;
                criticCertified = crit.TryGetProperty("certified", out var cc) && cc.ValueKind == JsonValueKind.True;
            }
            return (score, certified, criticScore, criticCertified, verifiedHot);
        }
        catch { return null; }
    }

    private static async Task AppendRtScore(string jfId, int score, bool certified, int? criticScore, bool criticCertified, bool verifiedHot)
    {
        var itemScoresPath = Plugin.DataFile("rt_item_scores.json");
        var certCriticsPath = Plugin.DataFile("rt_certified_critics.json");

        // Update item scores
        var scores = new Dictionary<string, RtAudienceEntry>();
        if (File.Exists(itemScoresPath))
        {
            var json = await File.ReadAllTextAsync(itemScoresPath).ConfigureAwait(false);
            scores = JsonSerializer.Deserialize<Dictionary<string, RtAudienceEntry>>(json) ?? new();
        }
        scores[jfId] = new RtAudienceEntry
        {
            Score = score,
            Certified = certified,
            CriticScore = criticScore,
            CriticCertified = criticCertified,
            VerifiedHot = verifiedHot,
            CriticScoreFetched = true,
        };
        await File.WriteAllTextAsync(itemScoresPath, JsonSerializer.Serialize(scores)).ConfigureAwait(false);

        // Keep the legacy ID list in sync for existing web badge scripts.
        var critics = new List<string>();
        if (File.Exists(certCriticsPath))
        {
            var json = await File.ReadAllTextAsync(certCriticsPath).ConfigureAwait(false);
            critics = JsonSerializer.Deserialize<List<string>>(json) ?? new();
        }

        if (criticCertified && !critics.Contains(jfId))
            critics.Add(jfId);
        else if (!criticCertified)
            critics.Remove(jfId);

        await File.WriteAllTextAsync(certCriticsPath, JsonSerializer.Serialize(critics)).ConfigureAwait(false);
    }

    private static List<string> GetSlugVariants(string title, int? year)
    {
        var baseSlug = Slugify(title);
        var variants = new List<string> { baseSlug };
        if (baseSlug.StartsWith("the_"))
            variants.Add(baseSlug[4..]);
        if (year.HasValue)
        {
            variants.Add($"{baseSlug}_{year}");
            if (baseSlug.StartsWith("the_"))
                variants.Add($"{baseSlug[4..]}_{year}");
        }
        var altSlug = SlugifyKeepSeparators(title);
        if (altSlug != baseSlug)
            variants.Add(altSlug);
        return variants;
    }

    private static string Slugify(string title)
    {
        var normalized = title.Normalize(System.Text.NormalizationForm.FormKD);
        var ascii = new string(normalized.Where(c => c < 128).ToArray());
        ascii = Regex.Replace(ascii.ToLowerInvariant(), @"[^a-z0-9\s]", "");
        return Regex.Replace(ascii.Trim(), @"\s+", "_");
    }

    private static string SlugifyKeepSeparators(string title)
    {
        var normalized = title.Normalize(System.Text.NormalizationForm.FormKD);
        var ascii = new string(normalized.Where(c => c < 128).ToArray());
        ascii = Regex.Replace(ascii, @"[-:.]", " ");
        ascii = Regex.Replace(ascii.ToLowerInvariant(), @"[^a-z0-9\s]", "");
        return Regex.Replace(ascii.Trim(), @"\s+", "_");
    }

    public void Dispose() { }
}
