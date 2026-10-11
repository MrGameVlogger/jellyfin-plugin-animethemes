using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AnimeThemes.Models;

/// <summary>
/// A GraphQL error entry.
/// </summary>
/// <param name="Message">The error message.</param>
public record GraphQLError(
    [property: JsonPropertyName("message")] string Message
);
