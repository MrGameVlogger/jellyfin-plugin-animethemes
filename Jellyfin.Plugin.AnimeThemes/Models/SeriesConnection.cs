using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AnimeThemes.Models;

/// <summary>
/// Connection of series an anime belongs to.
/// </summary>
/// <param name="Nodes">The related series.</param>
public record SeriesConnection(
    [property: JsonPropertyName("nodes")] Collection<AnimeSeries> Nodes
);
