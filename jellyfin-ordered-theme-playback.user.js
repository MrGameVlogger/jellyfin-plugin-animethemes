// ==UserScript==
// @name         Jellyfin — ordered theme playback
// @namespace    https://github.com/MrGameVlogger/jellyfin-plugin-animethemes
// @version      1.0.0
// @description  Plays Jellyfin theme songs/videos in name order (openings first) instead of the web client's random shuffle.
// @author       jellyfin-plugin-animethemes contributors
// @match        *://*/*
// @grant        none
// @run-at       document-start
// @downloadURL  https://raw.githubusercontent.com/MrGameVlogger/jellyfin-plugin-animethemes/main/jellyfin-ordered-theme-playback.user.js
// @updateURL    https://raw.githubusercontent.com/MrGameVlogger/jellyfin-plugin-animethemes/main/jellyfin-ordered-theme-playback.user.js
// ==/UserScript==

/*
 * The Jellyfin web client's theme player requests theme media with sortBy=Random
 * (src/components/themeMediaPlayer.js), so the server shuffles the playlist and
 * endings statistically win more often than openings. No server plugin can change
 * that request — this script does it in the browser instead.
 *
 * It strips the sortBy parameter from theme media requests only, so themes play in
 * the order the AnimeThemes plugin numbers them:
 *
 *     0-01, 0-02 …  openings
 *     1-…          insert songs
 *     2-…          endings
 *
 * Everything else — library listings, other sorts, other sites — is untouched.
 *
 * Tip: tighten @match above to your own host (e.g. https://jellyfin.example.com/*)
 * if you do not want the script active on other sites.
 */

'use strict';

function withoutRandomSort(url) {
    if (typeof url !== 'string' || !url.includes('/ThemeMedia')) {
        return url;
    }

    // Drop ?sortBy=… or &sortBy=…; the server then falls back to name order.
    return url.replace(/[?&]sortBy=[^&]*/, (match) => (match.startsWith('?') ? '?' : '&'));
}

const nativeFetch = window.fetch;
window.fetch = (input, init) => {
    if (typeof input === 'string') {
        input = withoutRandomSort(input);
    } else if (input instanceof Request && input.method === 'GET' && input.url.includes('/ThemeMedia')) {
        // Requests are one-shot only if they carry a body; theme requests are GETs.
        input = new Request(withoutRandomSort(input.url), input);
    }

    return nativeFetch(input, init);
};

const nativeOpen = XMLHttpRequest.prototype.open;
XMLHttpRequest.prototype.open = function (method, url, ...rest) {
    return nativeOpen.call(this, method, withoutRandomSort(url), ...rest);
};
