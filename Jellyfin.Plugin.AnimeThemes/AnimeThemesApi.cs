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
    private const string FindAnimeByExternalSiteQuery = """
        query ($ids: [Int!]!) {
            findAnimeByExternalSite(site: ANIDB, id: $ids) {
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
                                source
                                overlap
                                nc
                                link
                                audio {
                                    id
                                    path
                                    filename
                                    mimetype
                                    link
                                }
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
    /// Finds anime with all their themes by their AniDB Ids.
    /// </summary>
    /// <param name="ids">IDs on AniDB.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A dictionary with an entry for each passed <paramref name="ids"/>.</returns>
    public async ValueTask<Dictionary<int, Anime[]>> FindByAniDbId(IEnumerable<int> ids, CancellationToken cancellationToken = default)
    {
        var idList = ids.Distinct().ToArray();

        _logger.LogInformation("Looking up {Count} shows in the AnimeThemes db...", idList.Length);

        if (idList.Length > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(ids));
        }

        var payload = new { query = FindAnimeByExternalSiteQuery, variables = new { ids = idList } };

        _logger.LogInformation("Querying the AnimeThemes GraphQL API for {Count} AniDB ids", idList.Length);
        var result = await _client.PostAsJsonAsync("/", payload, cancellationToken).ConfigureAwait(false);
        result.EnsureSuccessStatusCode();

        var response = await result.Content.ReadFromJsonAsync<AnimeResponse>(cancellationToken: cancellationToken).ConfigureAwait(false);

        if (response?.Errors is { Count: > 0 })
        {
            _logger.LogError("AnimeThemes API returned errors: {Errors}", string.Join("; ", response.Errors.Select(it => it.Message)));
        }

        var found = response?.Data?.Anime ?? [];
        var animeByAniId = found
            .Where(a => a.AniDbId.HasValue)
            .GroupBy(a => a.AniDbId!.Value)
            .ToDictionary(a => a.Key, a => a.ToArray());

        _logger.LogInformation("Of a total of {TotalCount} shows, we were able to find entries for {FinalCount} shows", idList.Length, animeByAniId.Count);

        return idList.ToDictionary(id => id, id => animeByAniId.GetValueOrDefault(id) ?? []);
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
