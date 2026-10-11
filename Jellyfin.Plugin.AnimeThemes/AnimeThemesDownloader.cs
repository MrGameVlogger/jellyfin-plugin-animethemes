using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AnimeThemes.Configuration;
using Jellyfin.Plugin.AnimeThemes.Exceptions;
using Jellyfin.Plugin.AnimeThemes.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using MediaType = Jellyfin.Plugin.AnimeThemes.Models.MediaType;
using Video = Jellyfin.Plugin.AnimeThemes.Models.Video;

namespace Jellyfin.Plugin.AnimeThemes;

/// <summary>
/// Class that is responsible for downloading themes.
/// </summary>
public class AnimeThemesDownloader : IDisposable
{
    private const string ThemeMusicFileName = "theme.mp3";
    private const string ThemeMusicDirectory = "theme-music";
    private const string ThemeVideoDirectory = "backdrops";

    /// <summary>
    /// Matches the theme file names created by this plugin: theme name, volume marker, optional
    /// upstream size marker and an optional temporary suffix. Only files matching this pattern are
    /// ever deleted, so files placed in these folders by hand are left alone.
    /// </summary>
    private static readonly Regex PluginThemeFilePattern = new(
        @"^.+__(?:n|\d+)(?:__\d+)?(?:\.tmp)?\.(?:mp3|webm)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Matches theme file names that already carry the type/sequence ordering prefix and captures
    /// the name they had before ordering was introduced.
    /// </summary>
    private static readonly Regex OrderedThemeFilePattern = new(
        @"^\d+-\d+_(?<legacy>.+)$",
        RegexOptions.Compiled);

    /// <summary>
    /// Paces media downloads so bursts of parallel work do not trip the CDN's rate limiting.
    /// </summary>
    private static readonly SemaphoreSlim DownloadGate = new(1, 1);

    private readonly HttpClient _client;
    private readonly AnimeThemesApi _api;
    private readonly ILogger<AnimeThemesDownloader> _logger;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="AnimeThemesDownloader"/> class.
    /// </summary>
    /// <param name="mediaEncoder">Media Encoder to convert OGG files.</param>
    /// <param name="clientFactory">Client factory.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="relations">Season relations used to prune franchise siblings.</param>
    public AnimeThemesDownloader(IMediaEncoder mediaEncoder, IHttpClientFactory clientFactory, ILogger<AnimeThemesDownloader> logger, ILibraryManager libraryManager, AnimeRelations relations)
    {
        _mediaEncoder = mediaEncoder;
        _logger = logger;
        _libraryManager = libraryManager;
        _client = clientFactory.CreateClient("AnimeThemesMedia");
        _api = new AnimeThemesApi(clientFactory, relations, logger);
    }

    /// <summary>
    /// Checks if this item should be processed.
    /// </summary>
    /// <param name="item">Item to check.</param>
    /// <returns>Whether item should be processed.</returns>
    public bool ShouldUpdate(BaseItem item)
    {
        // Every anime is re-checked on every run so newly added themes are picked up,
        // obsolete ones removed and replaced content re-downloaded.
        return TryGetAniDbId(item, out _);
    }

    /// <summary>
    /// Resolves a list of BaseItems to a list of BaseItems with their corresponding anime object.
    /// </summary>
    /// <param name="items">Chunk of items.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A filtered list of items to be processed with their corresponding anime.</returns>
    public async IAsyncEnumerable<ItemWithAnime> ResolveItems(BaseItem[] items, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Multi-season series: seasons can carry their own AniDB ids and count as lookup
        // sources for the series, so no season's themes are missed.
        var seasonIdsByItem = GetSeasonAniDbIds(items);

        var lookupIds = new HashSet<int>();
        foreach (var item in items)
        {
            if (TryGetAniDbId(item, out var primaryId))
            {
                lookupIds.Add(primaryId);
            }

            if (seasonIdsByItem.TryGetValue(item.Id, out var seasonIds))
            {
                lookupIds.UnionWith(seasonIds);
            }
        }

        var animeByAniDb = await _api.FindByAniDbId(lookupIds, cancellationToken).ConfigureAwait(false);

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            List<int> ids = [];
            if (TryGetAniDbId(item, out var primaryId))
            {
                ids.Add(primaryId);
            }

            if (seasonIdsByItem.TryGetValue(item.Id, out var seasonIds))
            {
                ids.AddRange(seasonIds);
            }

            // All matches count: the anime of the item itself and the other seasons of its
            // AnimeThemes series, de-duplicated.
            var matched = new Dictionary<int, Anime>();
            foreach (var id in ids)
            {
                foreach (var anime in animeByAniDb.GetValueOrDefault(id) ?? [])
                {
                    matched.TryAdd(anime.Id, anime);
                }
            }

            if (matched.Count == 0)
            {
                continue;
            }

            yield return new ItemWithAnime(item, new ReadOnlyCollection<Anime>([.. matched.Values]));
        }
    }

    /// <summary>
    /// Collects the AniDB ids of the seasons of the given series.
    /// </summary>
    /// <param name="items">The items to inspect.</param>
    /// <returns>Season AniDB ids, keyed by the id of the parent item.</returns>
    private Dictionary<Guid, List<int>> GetSeasonAniDbIds(BaseItem[] items)
    {
        var result = new Dictionary<Guid, List<int>>();
        var series = items.Where(it => it.GetBaseItemKind() == BaseItemKind.Series).ToList();
        if (series.Count == 0)
        {
            return result;
        }

        var seasons = _libraryManager.GetItemList(
            new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Season],
                Recursive = true,
            },
            series);

        foreach (var season in seasons)
        {
            if (!TryGetAniDbProviderId(season, out var id))
            {
                continue;
            }

            if (!result.TryGetValue(season.ParentId, out var list))
            {
                list = [];
                result[season.ParentId] = list;
            }

            list.Add(id);
        }

        return result;
    }

    /// <summary>
    /// Processes an item, downloading its themes if applicable.
    /// </summary>
    /// <param name="item">The DB item to process.</param>
    /// <param name="animes">The anime belonging to the item, including the other seasons of its series.</param>
    /// <param name="configuration">Configuration of the plugin.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task that runts until item is done processing.</returns>
    public async ValueTask HandleAsync(BaseItem item, IReadOnlyList<Anime> animes, PluginConfiguration configuration, CancellationToken cancellationToken)
    {
        _logger.LogInformation("[{Id}] Attempting to filter themes for: {Name} (AniDB={AniDbId}, AnimeThemes={AnimeThemesId}, {MatchCount} anime match(es))", item.Id, item.Name, animes[0].AniDbId, animes[0].Id, animes.Count);

        bool isMovie = item.GetBaseItemKind() == BaseItemKind.Movie;
        var collectionTypeConfig = isMovie ? configuration.MovieSettings : new CollectionTypeConfiguration { AudioSettings = configuration.AudioSettings, VideoSettings = configuration.VideoSettings };

        // Process videos
        bool videoChanged = await ProcessMediaType(MediaType.Video, animes, item, collectionTypeConfig, cancellationToken).ConfigureAwait(false);

        // Process audios
        bool audioChanged = await ProcessMediaType(MediaType.Audio, animes, item, collectionTypeConfig, cancellationToken).ConfigureAwait(false);

        if (videoChanged || audioChanged)
        {
            _logger.LogInformation("[{Id}] Saving metadata", item.Id);
            try
            {
                await item.RefreshMetadata(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogError(e, "[{Id}] Metadata refresh failed", item.Id);
            }
        }
        else
        {
            _logger.LogInformation("[{Id}] Finished without changes", item.Id);
        }
    }

    private async ValueTask<bool> ProcessMediaType(MediaType type, IReadOnlyList<Anime> animes, BaseItem item, CollectionTypeConfiguration configuration, CancellationToken cancellationToken = default)
    {
        var settings = type == MediaType.Audio ? configuration.AudioSettings : configuration.VideoSettings;

        var distinctThemes = GetBestThemes(animes, settings).DistinctBy(it => it.Theme.Id);

        // Pick themes according to fetch type
        var requiredThemes = PickThemes(settings.FetchType, distinctThemes);

        // Turn them into downloadable links
        var links = ExtractLinks(type, requiredThemes, settings).ToArray();

        if (links.Length == 0)
        {
            var availableVideos = animes.Sum(anime => anime.Themes.Sum(theme => theme.Entries.Sum(entry => entry.Videos?.Nodes?.Count ?? 0)));
            if (availableVideos == 0)
            {
                _logger.LogInformation("[{Id}] {Type}: AnimeThemes has no themes for this anime yet", item.Id, type);
            }
            else
            {
                _logger.LogInformation("[{Id}] {Type}: none of the {Count} available theme video(s) match the configured filters", item.Id, type, availableVideos);
            }
        }

        bool changesMade = false;
        foreach (var (url, relativePath) in links)
        {
            // A file from before theme ordering was introduced is renamed into place instead
            // of being downloaded again.
            changesMade |= MigrateLegacyName(item, relativePath);

            // Download if needed
            changesMade |= await Download(type, url, item, relativePath, settings, cancellationToken).ConfigureAwait(false);
        }

        // Clean up only after the downloads so a failed download never removes existing themes.
        if (type == MediaType.Audio)
        {
            // We don't need this file because we're using the directory variant.
            changesMade |= RemoveFile(item, ThemeMusicFileName);
        }

        changesMade |= CleanDirectory(item, type, links.Select(it => Path.GetFileName(it.Filepath)));

        return changesMade;
    }

    private IEnumerable<FlattenedTheme> PickThemes(FetchType fetchType, IEnumerable<FlattenedTheme> themes)
    {
        switch (fetchType)
        {
            case FetchType.None:
                return [];
            case FetchType.Single:
                return themes.Take(1);
            case FetchType.All:
                return themes;
            default:
                throw new ArgumentOutOfRangeException($"Unknown fetch type: {fetchType}");
        }
    }

    private IEnumerable<(string Url, string Filepath)> ExtractLinks(MediaType type, IEnumerable<FlattenedTheme> themes, MediaTypeConfiguration settings)
    {
        bool isAudio = type == MediaType.Audio;

        foreach (var theme in themes)
        {
            var marker = settings.NormalizeLoudness ? "n" : string.Create(CultureInfo.InvariantCulture, $"{settings.Volume * 100:0}");
            var order = ThemeOrder(theme.Theme.Type, theme.Theme.Sequence);

            if (isAudio)
            {
                var audio = theme.Audio;
                var name = audio is null ? null : Path.GetFileName(audio.Filename);
                if (audio is null || string.IsNullOrEmpty(name))
                {
                    _logger.LogWarning("Skipping theme {Theme}: API response contained no usable audio file name", theme.Theme.Id);
                    continue;
                }

                yield return (audio.Link, CombineRelative(ThemeMusicDirectory, $"{order}_{name}__{marker}__{audio.Size ?? 0}.mp3"));
            }
            else
            {
                var name = Path.GetFileName(theme.Video.Filename);
                if (string.IsNullOrEmpty(name))
                {
                    _logger.LogWarning("Skipping theme {Theme}: API response contained no usable video file name", theme.Theme.Id);
                    continue;
                }

                yield return (theme.Video.Link, CombineRelative(ThemeVideoDirectory, $"{order}_{name}__{marker}__{theme.Video.Size ?? 0}.webm"));
            }
        }
    }

    /// <summary>
    /// Builds the sort prefix that makes openings play before insert songs and endings: the theme
    /// type rank followed by the sequence number. Jellyfin orders theme media by file name.
    /// </summary>
    /// <param name="type">The theme type.</param>
    /// <param name="sequence">The theme sequence.</param>
    /// <returns>A prefix like <c>0-01</c>.</returns>
    private static string ThemeOrder(ThemeType? type, int? sequence)
    {
        var rank = type switch
        {
            ThemeType.OP => 0,
            ThemeType.IN => 1,
            ThemeType.ED => 2,
            _ => 1,
        };

        return string.Create(CultureInfo.InvariantCulture, $"{rank}-{sequence ?? 0:00}");
    }

    /// <summary>
    /// Combines a root path with a relative path. The relative part must not be rooted,
    /// so untrusted file names cannot escape the intended directory.
    /// </summary>
    /// <param name="root">Root path.</param>
    /// <param name="relative">Relative path below <paramref name="root"/>.</param>
    /// <returns>The combined path.</returns>
    private static string CombineRelative(string root, string relative)
    {
        if (Path.IsPathRooted(relative))
        {
            throw new ArgumentException("Path must be relative.", nameof(relative));
        }

        return root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar + relative;
    }

    /// <summary>
    /// Removes theme files created by this plugin that are no longer wanted. Files not matching
    /// the plugin's naming scheme are left alone.
    /// </summary>
    /// <param name="series">The item the themes belong to.</param>
    /// <param name="mediaType">The media type of the themes.</param>
    /// <param name="allowedNames">The file names of the wanted themes.</param>
    /// <returns>Whether any file was removed.</returns>
    private bool CleanDirectory(BaseItem series, MediaType mediaType, IEnumerable<string> allowedNames)
    {
        var directory = mediaType == MediaType.Audio ? ThemeMusicDirectory : ThemeVideoDirectory;

        var path = CombineRelative(series.ContainingFolderPath, directory);
        if (!Directory.Exists(path))
        {
            return false;
        }

        var allowedNamesSet = allowedNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool removedAny = false;

        foreach (var filepath in Directory.EnumerateFiles(path))
        {
            var name = Path.GetFileName(filepath);
            if (allowedNamesSet.Contains(name) || !PluginThemeFilePattern.IsMatch(name))
            {
                continue;
            }

            if (TryDeleteFile(filepath))
            {
                _logger.LogInformation("[{Id}] Removing obsolete theme: {Theme}", series.Id, filepath);
                removedAny = true;
            }
        }

        return removedAny;
    }

    private bool RemoveFile(BaseItem series, string filename)
    {
        var path = CombineRelative(series.ContainingFolderPath, filename);
        if (TryDeleteFile(path))
        {
            _logger.LogInformation("[{Id}] Removing obsolete theme: {Path}", series.Id, path);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Renames a theme file created before theme ordering was introduced (one without the
    /// type/sequence prefix) into place, so upgrading never re-downloads unchanged files.
    /// </summary>
    /// <param name="item">The item the theme belongs to.</param>
    /// <param name="relativePath">The wanted file path, relative to the item folder.</param>
    /// <returns>Whether an existing file was renamed into place.</returns>
    private bool MigrateLegacyName(BaseItem item, string relativePath)
    {
        var path = CombineRelative(item.ContainingFolderPath, relativePath);
        if (File.Exists(path))
        {
            return false;
        }

        var match = OrderedThemeFilePattern.Match(Path.GetFileName(path));
        if (!match.Success)
        {
            return false;
        }

        var legacyName = match.Groups["legacy"].Value;

        // A name that itself looks ordered belongs to another (already ordered) theme, not to a
        // pre-ordering version of this one. Renaming it would steal that theme's file.
        if (OrderedThemeFilePattern.IsMatch(legacyName))
        {
            return false;
        }

        var legacyPath = CombineRelative(Path.GetDirectoryName(path)!, legacyName);
        if (!File.Exists(legacyPath))
        {
            return false;
        }

        try
        {
            File.Move(legacyPath, path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "Could not rename {Path}", legacyPath);
            return false;
        }

        _logger.LogInformation("[{Id}] Ordering theme file name: {From} -> {To}", item.Id, legacyName, Path.GetFileName(path));
        return true;
    }

    /// <summary>
    /// Deletes a file without throwing, so a single locked or unwritable file cannot abort a run.
    /// </summary>
    /// <param name="path">Path of the file to delete.</param>
    /// <returns>Whether the file was deleted.</returns>
    private bool TryDeleteFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "Could not delete {Path}", path);
            return false;
        }
    }

    private async ValueTask<bool> Download(MediaType type, string url, BaseItem item, string relativePath, MediaTypeConfiguration settings, CancellationToken cancellationToken = default)
    {
        var path = CombineRelative(item.ContainingFolderPath, relativePath);
        if (File.Exists(path))
        {
            // Nothing to do
            return false;
        }

        string? tempFile = null;
        string? tempOutput = null;

        try
        {
            tempFile = Path.GetTempFileName();

            // ffmpeg writes to a temporary name next to the destination and the result is only
            // moved into place on success, so a crash or failure can never leave a corrupt theme
            // behind.
            var extension = Path.GetExtension(path);
            tempOutput = path[..^extension.Length] + ".tmp" + extension;

            // Make sure directory exists
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            {
                _logger.LogInformation("[{Id}] Downloading {Url} to {Path}", item.Id, url, path);

                // Pace the network part: parallel workers queue here so the CDN sees at most
                // one request at a time, with a small gap between them.
                await DownloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
                    using var downloadStream = await _client.GetStreamAsync(url, cancellationToken).ConfigureAwait(false);
                    using var fileStream = File.OpenWrite(tempFile);

                    // Download file
                    await downloadStream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    DownloadGate.Release();
                }
            }

            // Convert OGG
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,

                    // Must consume both or ffmpeg may hang due to deadlocks.
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    FileName = _mediaEncoder.EncoderPath,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    ErrorDialog = false,
                    ArgumentList = { "-i", tempFile }
                },
                EnableRaisingEvents = true
            };

            // Update arguments
            var arguments = process.StartInfo.ArgumentList;
            if (type == MediaType.Video)
            {
                // Copy video stream
                arguments.Add("-c:v");
                arguments.Add("copy");
            }

            if (!settings.NormalizeLoudness && settings.Volume < 0.01 && type == MediaType.Video)
            {
                // Mute videos when volume is low enough
                arguments.Add("-an");
            }
            else if (settings.NormalizeLoudness)
            {
                // Normalize perceived loudness so themes sit at a consistent level next to
                // dialogue no matter how loud the source master is.
                arguments.Add("-filter:a");
                arguments.Add("loudnorm=I=-16:TP=-1.5:LRA=11");
            }
            else
            {
                arguments.Add("-filter:a");
                arguments.Add(string.Create(CultureInfo.InvariantCulture, $"volume={settings.Volume:0.00}"));
            }

            arguments.Add(tempOutput);

            process.Start();

            // Must consume both or ffmpeg may hang due to deadlocks.
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            await outputTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                var commandInfo = $"Command line: {process.StartInfo.FileName} {string.Join(" ", arguments)}";
                throw new ConversionException(process.ExitCode, commandInfo + "\n" + error);
            }

            File.Move(tempOutput, path, true);
            _logger.LogInformation("[{Id}] {Type}: theme downloaded successfully", item.Id, type);
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Timeouts surface as TaskCanceledException too, but only real user cancellation
            // propagates; everything else is a per-file failure.
            _logger.LogError(e, "Download failed");
            return false;
        }
        finally
        {
            if (tempFile is not null)
            {
                TryDeleteFile(tempFile);
            }

            if (tempOutput is not null)
            {
                TryDeleteFile(tempOutput);
            }
        }

        return true;
    }

    private bool TryGetAniDbId(BaseItem item, out int id)
    {
        // Only series and movies are processed on their own; season ids are collected as extra
        // lookup sources for their series.
        if (item.GetBaseItemKind() != BaseItemKind.Series && item.GetBaseItemKind() != BaseItemKind.Movie)
        {
            id = -1;
            return false;
        }

        return TryGetAniDbProviderId(item, out id);
    }

    /// <summary>
    /// Reads the AniDB provider id of any item, regardless of its type.
    /// </summary>
    /// <param name="item">The item to read.</param>
    /// <param name="id">The parsed AniDB id.</param>
    /// <returns>Whether the item has a valid AniDB id.</returns>
    private bool TryGetAniDbProviderId(BaseItem item, out int id)
    {
        id = -1;

        if (item.TryGetProviderId("AniDB", out var idAsString)
            && int.TryParse(idAsString, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedId))
        {
            id = parsedId;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Gets themes roughly sorted by relevance and filtered as needed.
    /// </summary>
    /// <param name="animes">The anime in question, including the other seasons of their series.</param>
    /// <param name="settings">The configuration that sets the rules.</param>
    /// <returns>A filtered and sorted and flattened enumerable of themes.</returns>
    private IEnumerable<FlattenedTheme> GetBestThemes(IReadOnlyList<Anime> animes, MediaTypeConfiguration settings)
    {
        return animes.SelectMany(anime => anime.Themes.SelectMany(theme => theme.Entries.SelectMany(entry => (entry.Videos?.Nodes ?? new Collection<Video>()).Select(video => Wrap(theme, entry, video)))))
            .Where(it =>
            {
                if (it.Audio is not null)
                {
                    return true;
                }

                _logger.LogWarning("Skipping theme {Theme}: API response contained no audio track", it.Theme.Id);
                return false;
            })
            .OrderBy(Rate)
            .Where(it => !settings.IgnoreOverlapping || it.Video.Overlap is null or OverlapType.None)
            .Where(it => !settings.IgnoreThemesWithCredits || it.Video.Creditless)
            .Where(it => !settings.IgnoreEDs || it.Theme.Type != ThemeType.ED)
            .Where(it => !settings.IgnoreOPs || it.Theme.Type != ThemeType.OP);
    }

    private FlattenedTheme Wrap(AnimeTheme theme, AnimeThemeEntry entry, Video video)
    {
        return new FlattenedTheme(theme, entry, video, video.Audio);
    }

    private double Rate(FlattenedTheme theme)
    {
        double score = 0;

        if (theme.Entry.Nsfw)
        {
            score += 10;
        }

        if (theme.Entry.Spoiler)
        {
            // Huge penalty for spoilers
            score += 50;
        }

        // Big penalties for overlap
        switch (theme.Video.Overlap)
        {
            case OverlapType.Over:
                score += 20;
                break;
            case OverlapType.Transition:
                score += 15;
                break;
        }

        // Small penalties for source
        switch (theme.Video.Source)
        {
            case VideoSource.LD:
            case VideoSource.VHS:
                score += 10;
                break;
            case VideoSource.WEB:
            case VideoSource.RAW:
                score += 5;
                break;
        }

        // Medium penalty for credits
        if (!theme.Video.Creditless)
        {
            score += 10;
        }

        return score;
    }

    /// <summary>
    /// Dispose.
    /// </summary>
    /// <param name="disposing">Whether we're disposing.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            _api.Dispose();
            _client.Dispose();
        }
    }

    /// <summary>
    /// Dispose.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}
