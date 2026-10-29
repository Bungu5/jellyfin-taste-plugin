# Changelog

Versions use Jellyfin's four-part plugin numbering.

## 0.6.3.0

- Home rows stay underneath a featured player such as MediaBar when they finish loading. If you have already scrolled, the page no longer jumps so the new rows paint over the player.

## 0.6.2.0

- The web script now loads on every page view. Taste adds its own loader to Jellyfin Web's page as it is served, instead of relying on JavaScript Injector, which on some servers never ran it: the home rows, home order, and header button only appeared after opening the Taste page, and were gone again after a refresh. JavaScript Injector and File Transformation are no longer needed; the old "Taste rating dialog" entry is removed from JavaScript Injector at startup.

## 0.6.1.0

- Export and import on the Profile tab: move your stars, thumbs, "Not for me" titles, and calibration answers to another server. Titles are matched by TMDB, IMDb, or TVDB id; import only fills gaps and never replaces a rating already on the server.

## 0.6.0.0

First public release. It sums up the private versions 0.1 to 0.5, which ran on a single home server.

- Rating dialog in Jellyfin Web when a movie is left in its credits or a series finale ends: 1-5 stars and a thumb. Stars go to Jellyfin's own user rating; each title is asked once.
- A taste profile per person from stars, thumbs, favourites, finished titles, Seerr requests, and "Not for me", with older signals fading over a year.
- Dislikes land on what a title is (genres and themes). People, studios, language, and decade only count against a title once two separate dislikes share them, and then weakly.
- Home screen rows: "Movies for you" and "Shows for you" from the library, plus "New movies / shows to request" and "More movies / shows to request" from outside it.
- "Fans also liked" picks from TMDB recommendations and, with a Trakt client ID, Trakt's related titles; Trakt and MDBList catalogs as extra candidates; MDBList scores as a quality gate.
- Discovery through Seerr alone (via its TMDB proxy) or a TMDB key; one-click Seerr requests as the signed-in person; rows saved per person and refreshed in the background and every six hours.
- "Not for me" on any card, with undo, and a list of hidden titles.
- Calibration: "Have you seen these?" and "Rate five you finished".
- Profile page showing what you lean towards and steer away from.
- Sections tab: each person can reorder and switch off Jellyfin's home sections, the Taste rows, and each library's "Latest" row.
- Cached responses older than a month are pruned, so the plugin database stays small.
