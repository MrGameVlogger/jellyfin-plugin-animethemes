using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AnimeThemes.Models;

/// <summary>
/// A collection of related anime (e.g. the seasons of a show). Subset of the <c>Series</c> GraphQL type.
/// </summary>
/// <param name="Id">The primary key of the resource.</param>
/// <param name="Name">The name of the series.</param>
/// <param name="Anime">The anime belonging to this series.</param>
public record AnimeSeries(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("anime")] AnimeConnection Anime
);
