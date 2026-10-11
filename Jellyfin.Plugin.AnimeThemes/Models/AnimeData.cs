using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AnimeThemes.Models;

/// <summary>
/// The data member of a GraphQL response.
/// </summary>
/// <param name="Anime">List of found anime.</param>
public record AnimeData(
    [property: JsonPropertyName("findAnimeByExternalSite")] Collection<Anime> Anime
);
