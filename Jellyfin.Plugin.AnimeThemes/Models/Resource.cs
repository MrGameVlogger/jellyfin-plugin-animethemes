using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AnimeThemes.Models;

/// <summary>
/// Represents an external resource. Subset of the <c>ExternalResource</c> GraphQL type.
///
/// For site types, see <see cref="Sites"/>.
/// </summary>
/// <param name="Id">The primary key of the resource.</param>
/// <param name="ExternalId">The numeric id on the external site, if any.</param>
/// <param name="Site">The site this resource belongs to.</param>
public record Resource(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("externalId")]
    int? ExternalId,
    [property: JsonPropertyName("site")] string Site
);
