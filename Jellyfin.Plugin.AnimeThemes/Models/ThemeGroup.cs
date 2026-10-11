using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AnimeThemes.Models;

/// <summary>
/// The group that accompanies an anime theme (e.g. "English Version"). Subset of the <c>ThemeGroup</c> GraphQL type.
/// </summary>
/// <param name="Id">The primary key of the resource.</param>
/// <param name="Name">The name of the group.</param>
public record ThemeGroup(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string? Name
);
