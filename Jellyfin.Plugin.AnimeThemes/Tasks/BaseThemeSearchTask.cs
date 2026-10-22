using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AnimeThemes.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AnimeThemes.Tasks;

/// <summary>
/// Base class for search tasks.
/// </summary>
public abstract class BaseThemeSearchTask
{
    private const int ChunkSize = 50;
    private readonly ILogger _logger;
    private readonly AnimeThemesDownloader _downloader;
    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="BaseThemeSearchTask"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="downloader">Downloader.</param>
    /// <param name="logger">Logger.</param>
    protected BaseThemeSearchTask(ILibraryManager libraryManager, AnimeThemesDownloader downloader, ILogger logger)
    {
        _libraryManager = libraryManager;
        _downloader = downloader;
        _logger = logger;
    }

    /// <summary>
    /// Processes all relevant items and calls a callback function.
    /// </summary>
    /// <param name="progress">Progress indicator.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that runs until the process is done.</returns>
    protected async Task FindAndProcessAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting theme search");
        var configuration = Plugin.Instance!.Configuration;
        var applicable = GetApplicableItems(configuration);
        if (applicable.Count == 0)
        {
            _logger.LogInformation("No applicable items found -- aborting");
            return;
        }

        // @formatter:off
        var items = _libraryManager.GetItemList(
            new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Series, BaseItemKind.Movie],
                IsVirtualItem = false,
                Recursive = true,
            },
            applicable);
        // @formatter:on

        // Get the anime objects in chunks
        var candidates = items.Where(it => _downloader.ShouldUpdate(it)).ToList();
        var skipped = items.Count - candidates.Count;
        if (skipped > 0)
        {
            _logger.LogInformation(
                "Skipping {Skipped} of {Total} item(s) without an AniDB provider id -- install the AniDB plugin and refresh metadata to include them",
                skipped,
                items.Count);
        }

        var itemsWithAnime = await candidates
            .Chunk(ChunkSize)
            .ToAsyncEnumerable()
            .SelectMany((chunk) => _downloader.ResolveItems(chunk, cancellationToken))
            .ToListAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        using var semaphore = new SemaphoreSlim(1, 1);
        int counter = 0;
        int count = itemsWithAnime.Count;

        // Process in parallel
        await Parallel.ForEachAsync(itemsWithAnime, new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = Math.Max(1, configuration.DegreeOfParallelism) }, async (item, ct) =>
        {
            try
            {
                await _downloader.HandleAsync(item.Item, item.Anime, configuration, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Timeouts surface as TaskCanceledException too, but only real user cancellation
                // propagates; a single bad item must not abort the whole run.
                _logger.LogError(e, "[{Id}] Failed to process item", item.Item.Id);
            }
            finally
            {
                await semaphore.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    counter++;
                    progress.Report(counter / (double)count * 100);
                }
                finally
                {
                    semaphore.Release();
                }
            }
        }).ConfigureAwait(false);

        _logger.LogInformation("Ending theme search ({Count} items processed)", count);
    }

    private List<BaseItem> GetApplicableItems(PluginConfiguration configuration)
    {
        Regex includeRegex;
        Regex? excludeRegex;

        try
        {
            includeRegex = !string.IsNullOrWhiteSpace(configuration.IncludeLibraries)
                ? new Regex(configuration.IncludeLibraries, RegexOptions.IgnoreCase | RegexOptions.Compiled)
                : new Regex("^.*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

            excludeRegex = !string.IsNullOrWhiteSpace(configuration.ExcludeLibraries)
                ? new Regex(configuration.ExcludeLibraries, RegexOptions.IgnoreCase | RegexOptions.Compiled)
                : null;
        }
        catch (ArgumentException e)
        {
            _logger.LogError(e, "Invalid include/exclude library regex -- aborting");
            return [];
        }

        var libraries = _libraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.CollectionFolder] });

        _logger.LogInformation("Considering the following libraries: {Libraries}", libraries.Select(it => it.Name));

        var matches = libraries.Where(item => includeRegex.IsMatch(item.Name ?? string.Empty)).ToList();

        _logger.LogInformation("Matching libraries (Included): {Libraries}", matches.Select(i => i.Name));

        if (excludeRegex != null)
        {
            matches = matches.Where(item => !excludeRegex.IsMatch(item.Name ?? string.Empty)).ToList();
            _logger.LogInformation("Matching libraries (Without Excluded): {Libraries}", matches.Select(i => i.Name));
        }
        else
        {
            _logger.LogInformation("No exclusion defined -- proceeding");
        }

        return matches;
    }
}
