using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AnimeThemes.Models;

/// <summary>
/// Connection of external resources for an anime.
/// </summary>
/// <param name="Nodes">The related resources.</param>
public record ResourceConnection(
    [property: JsonPropertyName("nodes")] Collection<Resource> Nodes
);
