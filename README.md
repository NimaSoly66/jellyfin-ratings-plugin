# Jellyfin Movie Ratings Plugin (IMDb + RT)

<p align="center">
  <img src="https://img.shields.io/github/v/release/jpedrofontes/jellyfin-ratings-plugin?style=flat-square" alt="Release" />
  <img src="https://img.shields.io/github/license/jpedrofontes/jellyfin-ratings-plugin?style=flat-square" alt="License" />
  <img src="https://img.shields.io/badge/jellyfin-12.0%2B-00a4dc?style=flat-square&logo=jellyfin" alt="Jellyfin 12.0+" />
</p>

A Jellyfin plugin that syncs **IMDb ratings**, fetches **Rotten Tomatoes audience and critic scores**, and maintains an **IMDb Top 250 playlist**.

## Features

- **IMDb Ratings** - Updates community ratings with real IMDb scores from official datasets
- **RT Scores Fetch** - Fetches Rotten Tomatoes audience (Popcornmeter) and critic (Tomatometer) scores, including Certified Fresh and Verified Hot status
- **Dual RT Badges** - Shows both tomato (critic) and popcorn (audience) icons side-by-side via custom JS injection
- **Top 250 Playlist** - Automatically creates and maintains an IMDb Top 250 Movies playlist using [IMDb's official non-commercial datasets](https://contribute.imdb.com/dataset)
- **Poster Provider** - Remote image provider using the OMDb API

## Installation

### From Plugin Repository (recommended)

1. Go to **Dashboard > Plugins > Repositories**
2. Click **+** and add:
   - **Name:** `Movie Ratings`
   - **URL:**
     ```
     https://raw.githubusercontent.com/jpedrofontes/jellyfin-ratings-plugin/main/manifest.json
     ```
3. Go to **Catalog**, find **Movie Ratings (IMDb + RT)**, click **Install**
4. Restart Jellyfin

### Manual

1. Download the latest `.zip` from [Releases](https://github.com/jpedrofontes/jellyfin-ratings-plugin/releases)
2. Extract it into your Jellyfin plugins directory:
   ```
   <jellyfin-data>/plugins/Movie Ratings/Jellyfin.Plugin.ImdbRatings.dll
   ```
3. Restart Jellyfin

## Configuration

Go to **Dashboard > Plugins > Movie Ratings (IMDb + RT)**:

| Setting | Description |
|---------|-------------|
| **OMDb API Key** | Optional. Only needed for the poster provider. Get a free key at [omdbapi.com](https://www.omdbapi.com/apikey.aspx). |
| **Enable Ratings Task** | Toggle the scheduled IMDb ratings sync on/off. |
| **Enable Playlist Task** | Toggle the Top 250 playlist sync on/off. |
| **Enable RT Scores Task** | Toggle the RT scores fetch on/off. |
| **Chart Cache (hours)** | How long to cache the IMDb chart before re-fetching (default: 24h). |

## RT Badges Setup (advanced)

The RT task stores its JSON cache under Jellyfin's application data directory at `plugins/MovieRatings`. The previous `/config/data` cache files are migrated automatically on first access when they are found in the Jellyfin data directory. The legacy web badge injection instructions below are Docker-specific and are not required for rating collection.

```yaml
# docker-compose.yml
services:
  jellyfin:
    volumes:
      - ./scripts/index.html:/jellyfin/jellyfin-web/index.html:ro
      - ./scripts/rt-popcorn.js:/jellyfin/jellyfin-web/rt-popcorn.js:ro
      - ./config/data/plugins/MovieRatings/rt_item_scores.json:/jellyfin/jellyfin-web/rt_item_scores.json:ro
      - ./config/data/plugins/MovieRatings/rt_certified_critics.json:/jellyfin/jellyfin-web/rt_certified_critics.json:ro
```

The `index.html` is a copy of Jellyfin's original with a single `<script defer src="rt-popcorn.js"></script>` tag added before `</body>`. The custom CSS for the rating icons goes in **Dashboard > Branding > Custom CSS**.

## How It Works

### IMDb Ratings
Runs daily at 3:30 AM. Downloads IMDb's official datasets (`title.ratings.tsv.gz`), and updates the community rating for every movie that has an IMDb ID.

### Top 250 Playlist
Runs every 6 hours. Downloads IMDb datasets (`title.basics.tsv.gz` and `title.ratings.tsv.gz`), computes a Bayesian-weighted ranking, and reorders the playlist. Optionally reads an external `imdb_chart.json` for the exact official chart ordering.

### RT Scores
Runs daily at 4:30 AM. For each movie in your library, looks up the Rotten Tomatoes page by title slug (with search fallback for non-standard slugs), and extracts:
- Audience score and Verified Hot status
- Critic Certified Fresh status

Results are saved as JSON files that the custom web JS reads to inject the popcorn badge.

## Data Sources

- **IMDb Ratings & Top 250**: [IMDb Non-Commercial Datasets](https://contribute.imdb.com/dataset)
- **Posters**: [OMDb API](https://www.omdbapi.com) (optional)
- **RT Scores**: Scraped from [rottentomatoes.com](https://www.rottentomatoes.com) public pages

> Information courtesy of [IMDb](https://www.imdb.com). Used with permission.

This plugin is not endorsed by or affiliated with IMDb or Rotten Tomatoes.

## Building from Source

```bash
dotnet build src/Jellyfin.Plugin.ImdbRatings.csproj -c Release -o ./output
```

The Jellyfin 12 build requires the .NET 10 SDK and references Jellyfin.Controller/Model 12.0.0. The output DLL is packaged without Jellyfin runtime assemblies; Jellyfin supplies those at load time.

## License

[MIT](LICENSE)
