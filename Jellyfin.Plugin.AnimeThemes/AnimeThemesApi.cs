using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AnimeThemes.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AnimeThemes;

/// <summary>
/// Represents the AnimeThemes GraphQL API.
/// </summary>
/// <remarks>
/// See <see href="https://api-docs.animethemes.moe"/>. The API is limited to 90 requests per minute;
/// themes are therefore resolved in batched queries.
/// </remarks>
public sealed class AnimeThemesApi : IDisposable
{
    /// <summary>
    /// Number of AniDB ids resolved per request. Every match also pulls in the full theme tree of
    /// its series (the seasons of the show), so this is kept well below the API's query complexity
    /// limit while staying small enough that a run stays under the 90 requests/minute budget.
    /// </summary>
    private const int QueryBatchSize = 25;

    /// <summary>
    /// Largest premiere-year gap between a show and one of its seasons. Remakes of a show share
    /// its title but premiere many years later; no season of one show stands this far apart.
    /// </summary>
    private const int MaxSeasonYearGap = 10;

    private const string FindAnimeByExternalSiteQuery = """
        query ($ids: [Int!]!) {
            findAnimeByExternalSite(site: ANIDB, id: $ids) {
                ...AnimeDetails
                series {
                    nodes {
                        id
                        name
                        anime {
                            nodes {
                                ...AnimeDetails
                            }
                        }
                    }
                }
            }
        }

        fragment AnimeDetails on Anime {
            id
            slug
            year
            format
            title {
                romaji
            }
            resources {
                nodes {
                    site
                    externalId
                }
            }
            animethemes {
                id
                type
                sequence
                slug
                group {
                    name
                }
                animethemeentries {
                    id
                    version
                    episodes
                    nsfw
                    spoiler
                    videos {
                        nodes {
                            id
                            basename
                            filename
                            size
                            source
                            overlap
                            nc
                            link
                            audio {
                                id
                                path
                                filename
                                mimetype
                                size
                                link
                            }
                        }
                    }
                }
            }
        }
        """;

    private readonly HttpClient _client;
    private readonly AnimeRelations _relations;
    private readonly ILogger<AnimeThemesDownloader> _logger;

    /// <summary>
    /// Season markers found in anime titles ("2nd Season", "Season 3", "Part 2", "II", "The Final").
    /// </summary>
    private static readonly Regex SeasonMarkerPattern = new(
        @"\s+\d+(?:st|nd|rd|th)\s+Season\b"
        + @"|\s+Season\s+\d+\b"
        + @"|\s+(?:Part|Cour)\s+\d+\b"
        + @"|\s+(?:The\s+)?Final(?:\s+Season)?\b"
        + @"|\s+(?:X|IX|VI{0,3}|IV|V|I{1,3})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex WhitespacePattern = new(@"\s{2,}", RegexOptions.Compiled);

    /// <summary>
    /// Initializes a new instance of the <see cref="AnimeThemesApi"/> class.
    /// </summary>
    /// <param name="clientFactory">Client factory.</param>
    /// <param name="relations">Season relations used to prune franchise siblings.</param>
    /// <param name="logger">Logger.</param>
    public AnimeThemesApi(IHttpClientFactory clientFactory, AnimeRelations relations, ILogger<AnimeThemesDownloader> logger)
    {
        _logger = logger;
        _relations = relations;
        _client = clientFactory.CreateClient("AnimeThemes");
    }

    /// <summary>
    /// Finds anime with all their themes by their AniDB Ids. Each result also contains the other
    /// anime of the same AnimeThemes series (the seasons of the show).
    /// </summary>
    /// <param name="ids">IDs on AniDB.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A dictionary with an entry for each passed <paramref name="ids"/>.</returns>
    public async ValueTask<Dictionary<int, Anime[]>> FindByAniDbId(IEnumerable<int> ids, CancellationToken cancellationToken = default)
    {
        var idList = ids.Distinct().ToArray();

        _logger.LogInformation("Looking up {Count} shows in the AnimeThemes db...", idList.Length);

        var found = new List<Anime>();
        foreach (var batch in idList.Chunk(QueryBatchSize))
        {
            var response = await QueryBatchAsync(batch, cancellationToken).ConfigureAwait(false);

            if (response?.Errors is { Count: > 0 })
            {
                _logger.LogError("AnimeThemes API returned errors: {Errors}", string.Join("; ", response.Errors.Select(it => it.Message)));
            }

            found.AddRange(response?.Data?.Anime ?? []);
        }

        // Index the matches by the AniDB id they were found through.
        var byAniId = new Dictionary<int, List<Anime>>();
        foreach (var anime in found)
        {
            if (anime.AniDbId is not int key)
            {
                continue;
            }

            if (!byAniId.TryGetValue(key, out var list))
            {
                list = [];
                byAniId[key] = list;
            }

            list.Add(anime);
        }

        // Every requested id maps to its matches plus the seasons of their series.
        var result = new Dictionary<int, Anime[]>(idList.Length);
        foreach (var id in idList)
        {
            var expanded = new Dictionary<int, Anime>();
            foreach (var root in byAniId.GetValueOrDefault(id) ?? [])
            {
                // Series groups are franchise-wide: the title/format/year filter keeps same-show
                // candidates and AniList relations then separate seasons from the rest.
                var candidates = ExpandWithSeries(root).ToList();
                var seasons = await _relations.PruneToSeasonsAsync(root, candidates, cancellationToken).ConfigureAwait(false);

                foreach (var anime in seasons)
                {
                    expanded.TryAdd(anime.Id, anime);
                }
            }

            result[id] = [.. expanded.Values];
        }

        _logger.LogInformation("Of a total of {TotalCount} shows, we were able to find entries for {FinalCount} shows", idList.Length, result.Values.Count(it => it.Length > 0));

        return result;
    }

    /// <summary>
    /// Yields the anime itself plus the other anime of its series that are seasons of the same
    /// show. A series groups a whole franchise (spin-offs, movies, remakes), so siblings are
    /// filtered down to same-format entries whose title matches after season markers are stripped
    /// (or that extend the root title) and whose premiere year is close to the root's.
    /// </summary>
    /// <param name="anime">The matched anime.</param>
    /// <returns>The anime and its seasons.</returns>
    private static IEnumerable<Anime> ExpandWithSeries(Anime anime)
    {
        yield return anime;

        foreach (var series in anime.Series?.Nodes ?? [])
        {
            foreach (var sibling in (series.Anime?.Nodes ?? []).Where(sibling => IsSameShow(anime, sibling)))
            {
                yield return sibling;
            }
        }
    }

    /// <summary>
    /// Tells seasons of a show apart from franchise spin-offs and remakes.
    /// </summary>
    /// <param name="root">The matched anime.</param>
    /// <param name="sibling">A candidate from the same series.</param>
    /// <returns>Whether the candidate is a season of the root's show.</returns>
    private static bool IsSameShow(Anime root, Anime sibling)
    {
        if (sibling.Id == root.Id)
        {
            return true;
        }

        // Movies, OVAs, specials and shorts are spin-off material, not seasons.
        if (string.IsNullOrEmpty(root.Format)
            || !string.Equals(sibling.Format, root.Format, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!TitlesMatch(root.Title?.Romaji, sibling.Title?.Romaji))
        {
            return false;
        }

        // Remakes carry the same title but premiere many years later (Fruits Basket 2001 vs
        // 2019); seasons of one show never stand that far apart.
        return root.Year is not int ry
            || sibling.Year is not int sy
            || Math.Abs(sy - ry) <= MaxSeasonYearGap;
    }

    /// <summary>
    /// Whether two titles belong to the same show: equal once season markers are stripped, or one
    /// extends the other at a word boundary (e.g. a subtitle sequel).
    /// </summary>
    /// <param name="root">The root title.</param>
    /// <param name="sibling">The candidate title.</param>
    /// <returns>Whether the titles refer to the same show.</returns>
    private static bool TitlesMatch(string? root, string? sibling)
    {
        if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(sibling))
        {
            return false;
        }

        if (string.Equals(NormalizeTitle(root), NormalizeTitle(sibling), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var shorter = root.Length <= sibling.Length ? root : sibling;
        var longer = root.Length <= sibling.Length ? sibling : root;
        return longer.Length > shorter.Length
            && longer.StartsWith(shorter, StringComparison.OrdinalIgnoreCase)
            && !char.IsLetterOrDigit(longer[shorter.Length]);
    }

    /// <summary>
    /// Strips season markers ("2nd Season", "Part 2", "II", "The Final", …) from a title.
    /// </summary>
    /// <param name="title">The title to normalize.</param>
    /// <returns>The title without season markers.</returns>
    private static string NormalizeTitle(string title)
    {
        var normalized = SeasonMarkerPattern.Replace(title, string.Empty);
        normalized = WhitespacePattern.Replace(normalized, " ");
        return normalized.Trim().TrimEnd(':', '-', '–', '—').Trim();
    }

    private async ValueTask<AnimeResponse?> QueryBatchAsync(int[] ids, CancellationToken cancellationToken)
    {
        var payload = new { query = FindAnimeByExternalSiteQuery, variables = new { ids } };

        try
        {
            _logger.LogInformation("Querying the AnimeThemes GraphQL API for {Count} AniDB ids", ids.Length);
            var result = await _client.PostAsJsonAsync("/", payload, cancellationToken).ConfigureAwait(false);
            result.EnsureSuccessStatusCode();

            return await result.Content.ReadFromJsonAsync<AnimeResponse>(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // A lookup failure must not abort the whole run; the affected items are simply
            // skipped and picked up again on the next run. Timeouts surface as
            // TaskCanceledException too, but only real user cancellation propagates.
            _logger.LogError(e, "AnimeThemes API lookup failed");
            return null;
        }
    }

    /// <summary>
    /// Disposes of the client.
    /// </summary>
    /// <param name="disposing">Whether the object is currently being disposed.</param>
    private void Dispose(bool disposing)
    {
        if (disposing)
        {
            _client.Dispose();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
    }
}
