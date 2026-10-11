using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
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
    private readonly ILogger<AnimeThemesDownloader> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AnimeThemesApi"/> class.
    /// </summary>
    /// <param name="clientFactory">Client factory.</param>
    /// <param name="logger">Logger.</param>
    public AnimeThemesApi(IHttpClientFactory clientFactory, ILogger<AnimeThemesDownloader> logger)
    {
        _logger = logger;
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

        // Every requested id maps to its matches plus the other seasons of their series.
        var result = new Dictionary<int, Anime[]>(idList.Length);
        foreach (var id in idList)
        {
            var expanded = new Dictionary<int, Anime>();
            foreach (var root in byAniId.GetValueOrDefault(id) ?? [])
            {
                foreach (var anime in ExpandWithSeries(root))
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
    /// Yields the anime itself plus every other anime of its series (the seasons of the show).
    /// </summary>
    /// <param name="anime">The matched anime.</param>
    /// <returns>The anime and its series siblings.</returns>
    private static IEnumerable<Anime> ExpandWithSeries(Anime anime)
    {
        yield return anime;

        foreach (var series in anime.Series?.Nodes ?? [])
        {
            foreach (var sibling in series.Anime?.Nodes ?? [])
            {
                yield return sibling;
            }
        }
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
