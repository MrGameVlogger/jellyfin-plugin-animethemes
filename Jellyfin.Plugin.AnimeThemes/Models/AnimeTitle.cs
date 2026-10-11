using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AnimeThemes.Models;

/// <summary>
/// The localized titles of a resource. Subset of the <c>AnimeTitle</c> GraphQL type.
/// </summary>
/// <param name="Romaji">The romaji title.</param>
public record AnimeTitle(
    [property: JsonPropertyName("romaji")] string? Romaji
);
