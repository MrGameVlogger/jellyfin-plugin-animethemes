using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AnimeThemes.Models;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AnimeThemes;

/// <summary>
/// Tells seasons of a show apart from franchise spin-offs using AniList relations: seasons are
/// reachable from each other through prequel/sequel links, while spin-offs and remakes are not.
/// AniList ids come from the AnimeThemes external resources; results are cached on disk because
/// relations hardly ever change. Whenever a judgment cannot be made (no AniList id or no data),
/// the caller's own decision stands.
/// </summary>
public sealed class AnimeRelations
{
    /// <summary>
    /// AniList ids resolved per request. AniList rate-limits by query cost, so batches stay small.
    /// </summary>
    private const int QueryBatchSize = 15;

    private const string RelationsQuery = """
        query ($ids: [Int!]!) {
            Page(page: 1, perPage: 100) {
                media(id_in: $ids) {
                    id
                    relations {
                        edges {
                            relationType
                            node {
                                id
                            }
                        }
                    }
                }
            }
        }
        """;

    /// <summary>
    /// Delay between AniList requests.
    /// </summary>
    private static readonly TimeSpan RequestDelay = TimeSpan.FromMilliseconds(500);

    private readonly HttpClient _client;
    private readonly ILogger<AnimeRelations> _logger;
    private readonly string _cachePath;

    /// <summary>
    /// Season relations per AniList id of the anime they were fetched for. Key presence means
    /// "looked up"; an empty array means "looked up, no season relations".
    /// </summary>
    private readonly Dictionary<int, int[]> _cache = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="AnimeRelations"/> class.
    /// </summary>
    /// <param name="clientFactory">Client factory.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="applicationPaths">Application paths.</param>
    public AnimeRelations(IHttpClientFactory clientFactory, ILogger<AnimeRelations> logger, IApplicationPaths applicationPaths)
    {
        _client = clientFactory.CreateClient("AniList");
        _logger = logger;
        _cachePath = Path.Combine(applicationPaths.PluginConfigurationsPath, "animethemes-relations.json");
        LoadCache();
    }

    /// <summary>
    /// Keeps the root plus the candidates that are seasons of the root's show: reachable from the
    /// root through prequel/sequel relations. Candidates whose relations cannot be determined are
    /// kept.
    /// </summary>
    /// <param name="root">The anime matched for the item.</param>
    /// <param name="candidates">The root plus same-franchise candidates.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The root and its seasons.</returns>
    public async ValueTask<IReadOnlyList<Anime>> PruneToSeasonsAsync(Anime root, IReadOnlyList<Anime> candidates, CancellationToken cancellationToken)
    {
        var rootId = GetAniListId(root);
        if (rootId is null)
        {
            return candidates;
        }

        var ids = candidates.Select(GetAniListId).Where(it => it.HasValue).Select(it => it!.Value).Distinct().ToArray();
        await EnsureCachedAsync(ids, cancellationToken).ConfigureAwait(false);

        if (!_cache.ContainsKey(rootId.Value))
        {
            return candidates;
        }

        var reachable = ComputeReachable(rootId.Value);
        return candidates
            .Where(candidate => GetAniListId(candidate) is not int id || !_cache.ContainsKey(id) || reachable.Contains(id))
            .ToList();
    }

    /// <summary>
    /// Reads the AniList id of an anime from its external resources.
    /// </summary>
    /// <param name="anime">The anime to read.</param>
    /// <returns>The AniList id, or null when unknown.</returns>
    internal static int? GetAniListId(Anime anime)
    {
        return anime.Resources?.Nodes?
            .FirstOrDefault(it => string.Equals(it.Site, Sites.ANILIST, StringComparison.OrdinalIgnoreCase))?.ExternalId;
    }

    /// <summary>
    /// Walks prequel/sequel links, treating them as undirected, and returns every AniList id
    /// reachable from the root.
    /// </summary>
    /// <param name="rootId">The root AniList id.</param>
    /// <returns>The reachable AniList ids.</returns>
    private HashSet<int> ComputeReachable(int rootId)
    {
        // AniList relations are mirrored (A prequel of B, B sequel of A), but build the reverse
        // edges anyway so a one-sided entry cannot break the walk.
        var neighbours = new Dictionary<int, HashSet<int>>();
        foreach (var (id, related) in _cache)
        {
            foreach (var other in related)
            {
                AddNeighbour(neighbours, id, other);
                AddNeighbour(neighbours, other, id);
            }
        }

        var reachable = new HashSet<int> { rootId };
        var queue = new Queue<int>();
        queue.Enqueue(rootId);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var next in neighbours.GetValueOrDefault(current) ?? [])
            {
                if (reachable.Add(next))
                {
                    queue.Enqueue(next);
                }
            }
        }

        return reachable;
    }

    private static void AddNeighbour(Dictionary<int, HashSet<int>> neighbours, int from, int to)
    {
        if (!neighbours.TryGetValue(from, out var set))
        {
            set = [];
            neighbours[from] = set;
        }

        set.Add(to);
    }

    /// <summary>
    /// Fetches and caches the relations of the given AniList ids that are not known yet.
    /// </summary>
    /// <param name="ids">AniList ids to look up.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that runs until the lookups are done.</returns>
    private async ValueTask EnsureCachedAsync(int[] ids, CancellationToken cancellationToken)
    {
        var missing = ids.Where(id => !_cache.ContainsKey(id)).ToArray();
        if (missing.Length == 0)
        {
            return;
        }

        foreach (var batch in missing.Chunk(QueryBatchSize))
        {
            // Politeness pacing: AniList rate-limits by query cost.
            await Task.Delay(RequestDelay, cancellationToken).ConfigureAwait(false);

            var seasonRelations = await QueryBatchAsync(batch, cancellationToken).ConfigureAwait(false);
            if (seasonRelations is null)
            {
                // Unavailable: leave the ids unknown so callers fall back to their own judgment.
                return;
            }

            foreach (var id in batch)
            {
                // Only ids the response actually covered are known; anything else stays
                // unknown so callers fall back to keeping the candidate.
                if (seasonRelations.TryGetValue(id, out var related))
                {
                    _cache[id] = related;
                }
            }

            SaveCache();
        }
    }

    /// <summary>
    /// Queries AniList for the prequel/sequel relations of a batch of anime.
    /// </summary>
    /// <param name="ids">AniList ids.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Season relations per id, or null when the lookup failed.</returns>
    private async ValueTask<Dictionary<int, int[]>?> QueryBatchAsync(int[] ids, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _client.PostAsJsonAsync("/", new { query = RelationsQuery, variables = new { ids } }, cancellationToken).ConfigureAwait(false);
            result.EnsureSuccessStatusCode();

            var payload = await result.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(payload);

            var relations = new Dictionary<int, int[]>();
            foreach (var media in document.RootElement.GetProperty("data").GetProperty("Page").GetProperty("media").EnumerateArray())
            {
                var id = media.GetProperty("id").GetInt32();
                var seasonIds = new List<int>();
                foreach (var edge in media.GetProperty("relations").GetProperty("edges").EnumerateArray())
                {
                    var relationType = edge.GetProperty("relationType").GetString();
                    if (relationType is not null && (relationType.Equals("PREQUEL", StringComparison.Ordinal) || relationType.Equals("SEQUEL", StringComparison.Ordinal)))
                    {
                        seasonIds.Add(edge.GetProperty("node").GetProperty("id").GetInt32());
                    }
                }

                relations[id] = [.. seasonIds];
            }

            return relations;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(e, "AniList relation lookup failed; falling back to title matching");
            return null;
        }
    }

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(_cachePath))
            {
                return;
            }

            var cached = JsonSerializer.Deserialize<Dictionary<int, int[]>>(File.ReadAllText(_cachePath));
            if (cached is not null)
            {
                foreach (var (id, related) in cached)
                {
                    _cache[id] = related;
                }
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "Could not read the AniList relations cache; starting empty");
        }
    }

    private void SaveCache()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            File.WriteAllText(_cachePath, JsonSerializer.Serialize(_cache));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "Could not write the AniList relations cache");
        }
    }
}
