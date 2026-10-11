using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AnimeThemes.Models;

/// <summary>
/// Types of overlap between episode and theme. Maps the <c>VideoOverlap</c> GraphQL enum.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OverlapType
{
    /// <summary>
    /// No overlap.
    /// </summary>
    None,

    /// <summary>
    /// Some overlap.
    /// </summary>
    [JsonStringEnumMemberName("TRANS")]
    Transition,

    /// <summary>
    /// Complete overlap.
    /// </summary>
    Over
}
