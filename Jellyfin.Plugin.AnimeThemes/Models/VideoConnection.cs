using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AnimeThemes.Models;

/// <summary>
/// Connection of videos for an anime theme entry.
/// </summary>
/// <param name="Nodes">The related videos.</param>
public record VideoConnection(
    [property: JsonPropertyName("nodes")] Collection<Video> Nodes
);
