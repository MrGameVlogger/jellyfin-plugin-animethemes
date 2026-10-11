using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AnimeThemes.Models;

/// <summary>
/// Connection of anime within a series.
/// </summary>
/// <param name="Nodes">The related anime.</param>
public record AnimeConnection(
    [property: JsonPropertyName("nodes")] Collection<Anime> Nodes
);
