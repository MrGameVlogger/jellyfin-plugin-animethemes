using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AnimeThemes.Models;

/// <summary>
/// Represents a GraphQL response envelope.
/// </summary>
/// <param name="Data">The response data, if any.</param>
/// <param name="Errors">The response errors, if any.</param>
public record AnimeResponse(
    [property: JsonPropertyName("data")] AnimeData? Data,
    [property: JsonPropertyName("errors")] Collection<GraphQLError>? Errors
);
