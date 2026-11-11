using System.Reflection;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.ImdbRatings;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public static Plugin? Instance { get; private set; }

    public static string DataDirectory { get; private set; } = string.Empty;

    private static string _legacyDataDirectory = string.Empty;

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        _legacyDataDirectory = applicationPaths.DataPath;
        DataDirectory = Path.Combine(applicationPaths.DataPath, "plugins", "MovieRatings");
        Directory.CreateDirectory(DataDirectory);
    }

    /// <summary>
    /// Returns a path inside the plugin's writable data directory. Existing files
    /// in Jellyfin's previous data-root location are moved once, without overwriting
    /// a newer file already present in the plugin directory.
    /// </summary>
    public static string DataFile(string fileName)
    {
        if (string.IsNullOrWhiteSpace(DataDirectory))
            throw new InvalidOperationException("The plugin data directory has not been initialized.");

        var safeName = Path.GetFileName(fileName);
        if (!string.Equals(safeName, fileName, StringComparison.Ordinal))
            throw new ArgumentException("Only file names are allowed.", nameof(fileName));

        var destination = Path.Combine(DataDirectory, safeName);
        if (!File.Exists(destination) && Instance is not null)
        {
            var legacy = Path.Combine(_legacyDataDirectory, safeName);
            if (File.Exists(legacy))
            {
                try
                {
                    File.Move(legacy, destination, overwrite: false);
                }
                catch (IOException)
                {
                    // Another task may have migrated the same file concurrently.
                }
            }
        }

        return destination;
    }

    public override string Name => "Movie Ratings (IMDb + RT)";

    public override Guid Id => new Guid("8db72461-cd14-4a3c-8093-5891cf02b8d0");

    public override string Description => "Syncs IMDb ratings, maintains a Top 250 playlist, and provides Rotten Tomatoes audience/critic data for the web UI. Information courtesy of IMDb (https://www.imdb.com). Used with permission.";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html"
        };
    }
}
