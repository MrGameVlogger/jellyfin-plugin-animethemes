using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AnimeThemes.Models;

/// <summary>
/// Represents an anime. Subset of the <c>Anime</c> GraphQL type.
/// </summary>
public record Anime(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("title")] AnimeTitle Title,
    [property: JsonPropertyName("slug")] string Slug,
    [property: JsonPropertyName("year")] int? Year,
    [property: JsonPropertyName("resources")]
    ResourceConnection Resources,
    [property: JsonPropertyName("animethemes")]
    Collection<AnimeTheme> Themes,
    [property: JsonPropertyName("series")]
    SeriesConnection? Series
)
{
    /// <summary>
    /// Gets the display title of this anime.
    /// </summary>
    [JsonIgnore]
    public string Name => Title?.Romaji ?? Slug;

    /// <summary>
    /// Gets the AniDB ID of this anime if available in payload.
    /// </summary>
    [JsonIgnore]
    public int? AniDbId => Resources?.Nodes?
        .FirstOrDefault(it => string.Equals(it.Site, Sites.ANIDB, System.StringComparison.OrdinalIgnoreCase))?.ExternalId;
}
