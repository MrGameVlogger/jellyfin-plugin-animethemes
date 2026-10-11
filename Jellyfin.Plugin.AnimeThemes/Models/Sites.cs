namespace Jellyfin.Plugin.AnimeThemes.Models;

/// <summary>
/// Contains a collection of well-known sites.
/// </summary>
public static class Sites
{
    /// <summary>
    /// Site for AniDB. Matches the <c>ANIDB</c> value of the <c>ResourceSite</c> GraphQL enum.
    /// </summary>
    public const string ANIDB = "ANIDB";

    /// <summary>
    /// Site for AniList. Matches the <c>ANILIST</c> value of the <c>ResourceSite</c> GraphQL enum.
    /// </summary>
    public const string ANILIST = "ANILIST";
}
