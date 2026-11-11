using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using ItemUpdateType = MediaBrowser.Controller.Library.ItemUpdateType;

namespace Jellyfin.Plugin.ImdbRatings;

public class ImdbRatingsTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;

    public ImdbRatingsTask(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    public string Name => "Refresh IMDb Ratings";
    public string Key => "ImdbRatingsRefresh";
    public string Description => "Updates movie community ratings from IMDb's official datasets.";
    public string Category => "IMDb";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null || !config.EnableRatingsTask)
            return;

        progress.Report(0);

        var cacheMaxAge = TimeSpan.FromHours(config.ChartCacheHours > 0 ? config.ChartCacheHours : 24);
        var ratings = await ImdbDatasetCache.GetRatingsAsync(cacheMaxAge, cancellationToken, forceRefresh: true).ConfigureAwait(false);
        if (ratings.Count == 0)
            return;

        progress.Report(30);

        var movies = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Movie },
            IsVirtualItem = false,
        });

        if (movies.Count == 0)
        {
            progress.Report(100);
            return;
        }

        int updated = 0;

        for (int i = 0; i < movies.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var movie = movies[i];
            var imdbId = movie.GetProviderId(MetadataProvider.Imdb);

            if (!string.IsNullOrEmpty(imdbId) &&
                ratings.TryGetValue(imdbId, out var data) &&
                (movie.CommunityRating == null || Math.Abs(movie.CommunityRating.Value - (float)data.Rating) > 0.01f))
            {
                movie.CommunityRating = (float)data.Rating;
                await _libraryManager.UpdateItemAsync(
                    movie,
                    movie.GetParent(),
                    ItemUpdateType.MetadataEdit,
                    cancellationToken).ConfigureAwait(false);
                updated++;
            }

            progress.Report(30 + (double)(i + 1) / movies.Count * 70);
        }
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(3.5).Ticks
        };
    }
}
