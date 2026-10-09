# Taste for Jellyfin

Taste is a Jellyfin server plugin that asks people what they thought of a movie or series when they finish it, builds a taste profile for each person, and puts personal recommendation rows on the Jellyfin Web home screen. With [Seerr](https://github.com/seerr-team/seerr) (Jellyseerr or Overseerr) connected, it also suggests titles you do not have yet and lets people request them in one click.

> **Please read this first**
>
> - This project was heavily "vibe coded": almost all of the code and documentation was written by AI coding agents, with a human steering, testing on a home server, and reviewing the result. Read the code if that matters to you.
> - It is not an official Jellyfin project and is not affiliated with Jellyfin, Seerr, TMDB, Trakt, or MDBList.
> - Expect rough edges. It has been used on one household's server, not on many different setups.
> - It comes with no warranty, as the [GPL](LICENSE) says.
> - Back up your Jellyfin configuration and data folders before installing. Taste writes ratings into Jellyfin's own user data and can change each person's home screen settings.

## What it does

**For everyone using Jellyfin Web:**

- **Rating dialog.** Leave a movie during its credits, or finish the last episode of a series, and a small dialog asks for 1 to 5 stars and a thumbs up or down. "Not now" closes it. Each title is asked once; episodes are never rated, a finale rates the series.
- **Rows on the home screen**, by default right after Continue Watching and Next Up:
  - *Movies for you* and *Shows for you*: unwatched titles already in your library.
  - *New movies to request* and *New shows to request*: released in the last five months, not in your library.
  - *More movies to request* and *More shows to request*: older titles not in your library.
  - Each card has a one-line reason, such as "Fans of Severance and Dark also liked this" or "You gave Arrival 5 stars, and this shares the director and genre". Library cards open the normal item page. Other cards open a small details card with the overview, **Request** (sends a Seerr request as you), **Details** (opens TMDB), and **Not for me**. With a mouse, hovering a card shows quick Request and Not for me buttons.
- **Not for me** hides a title from every row and counts as a mild dislike. An Undo button appears for a few seconds, and the Calibrate tab lists everything you have hidden.
- **The Taste page**, opened from the thumbs icon in the header (and from the menu if Plugin Pages is installed), has these tabs:
  - **Calibrate**: "Have you seen these?" (well-known titles to rate if you have seen them), "Rate five you finished" (titles you finished but never scored), and your hidden titles.
  - **Profile**: what you lean towards and what you steer away from, as genre, theme, people, studio, language, and era chips. **Export** and **Import** move your ratings to another Jellyfin server (see below).
  - **Sections**: every home screen section, Jellyfin's own (My media, Continue watching, Next up, Recently added media, and so on) and Taste's rows, with a drag handle (arrow keys work too) and an on/off switch. Recently added media also gets a switch per library ("Latest Movies", "Latest Shows", ...). Saving writes Jellyfin's own home screen settings for your account, so they show up under Settings, Home screen, too, and reloads the page.
  - **For you**: the recommendation rows, shown here only when an admin has switched home rows off.

**For admins:** a settings page under Dashboard, Plugins, Taste, and a scheduled task, *Refresh Taste discovery*, that keeps everyone's rows ready.

The dialog and rows only exist in Jellyfin Web (the browser, and desktop apps that wrap the web client). Native apps such as Android TV or Swiftfin do not run injected scripts, but ratings, favourites, and watch history from every client still feed the profile.

## Requirements

- **Jellyfin 12.1.x.** The plugin is built against the Jellyfin 12.1.0 packages and declares `targetAbi` 12.1.0.0, so Jellyfin will not load it on older servers. The .NET runtime ships with Jellyfin; nothing else to install.
- Nothing else for the web side. Taste adds its script to Jellyfin Web's page itself as the page is served, so it doesn't need JavaScript Injector or File Transformation and works with the read-only web folder in the official Docker image.
- **Plugin Pages** (optional) adds a "Taste" entry to Jellyfin Web's menu. The header button works without it.
- **A media segment provider** (optional) such as Intro Skipper, or Chapter Segments Provider for chapters named like credits. With an outro segment, "in the credits" is exact; without one, the last 10% of the runtime counts.

Optional services:

| Service | What it adds |
|---------|--------------|
| Seerr (Jellyseerr or Overseerr) | The rows of titles to request, the Request button, posters in calibration, and title data through Seerr's TMDB proxy. A person's Seerr requests for titles now in the library count as a light "liked" signal. |
| TMDB API key | Title data straight from TMDB instead of through Seerr. Not needed if Seerr is set up. |
| Trakt client ID (free) | A second "fans also liked" source, and Trakt's trending and popular charts as candidates. |
| MDBList API key (free tier) | Merged IMDb, Rotten Tomatoes, Metacritic, and Trakt scores as a quality gate, and MDBList's catalog of well-rated recent titles as candidates. |

Without any of them, only *Movies for you* and *Shows for you* appear.

## Installation

### From a release zip

1. Download `jellyfin-plugin-taste_<version>.zip` from the [Releases page](https://github.com/Bungu5/jellyfin-taste-plugin/releases) and check it against the `.md5` or `.sha256` file next to it.
2. Stop Jellyfin.
3. Unzip it into a new folder named `Taste_<version>` inside Jellyfin's `plugins` folder. Typical locations:
   - Debian or Ubuntu packages: `/var/lib/jellyfin/plugins/Taste_0.6.2.0/`
   - Official Docker image: `/config/plugins/Taste_0.6.2.0/` inside the container
   - Windows: `%PROGRAMDATA%\Jellyfin\Server\plugins\Taste_0.6.2.0\`

   The folder should contain `Jellyfin.Plugin.Taste.dll` and `meta.json`. If you are upgrading, remove the old `Taste_*` folder.
4. Make sure the Jellyfin service user can read the files, for example `sudo chown -R jellyfin:jellyfin /var/lib/jellyfin/plugins/Taste_0.6.2.0`.
5. Start Jellyfin. After the first page load, the log should show `Taste: added the web loader to Jellyfin Web`.
6. Open Dashboard, Plugins, Taste, and fill in the settings you want.
7. Reload Jellyfin Web in your browser (a hard refresh, Ctrl+F5, is safest).

### From a plugin repository

Each GitHub release also carries a Jellyfin repository `manifest.json`. In Dashboard, Plugins, Repositories, add

```text
https://github.com/Bungu5/jellyfin-taste-plugin/releases/latest/download/manifest.json
```

then install Taste from the catalog and restart. The manifest only lists the latest release.

### From source

You need Docker, or the .NET 10 SDK.

```bash
git clone <this repository>
cd jellyfin-plugin-taste
./build.sh
```

`build.sh` runs the tests, then writes `dist/jellyfin-plugin-taste_<version>.zip` and `dist/Taste_<version>/` (the DLL and `meta.json`, ready to copy into the plugins folder). It uses the .NET 10 SDK on your PATH, or the `mcr.microsoft.com/dotnet/sdk:10.0` container if there is none. The version comes from the project file; `VERSION=0.6.0.1 ./build.sh` overrides it.

### Uninstalling

Delete the `Taste_<version>` folder and restart Jellyfin. Stars stay in Jellyfin as normal user ratings, and home screen changes made in the Sections tab stay in each person's Jellyfin settings. Delete `taste.db` from Jellyfin's data folder to remove the rest.

## Configuration

All settings are on Dashboard, Plugins, Taste. API keys are stored only in Jellyfin's plugin configuration file (`plugins/configurations/Jellyfin.Plugin.Taste.xml` in Jellyfin's data folder), which only admins can read through the API. Taste does not include them in its log messages.

| Setting | Default | What it does |
|---------|---------|--------------|
| Credits window when there is no outro segment | 10% | How close to the end a stop counts as "in the credits" when a title has no outro segment (1-50%). |
| Show Taste rows on the home screen | On | Adds the Taste rows to the Jellyfin Web home screen. Off moves them to a "For you" tab on the Taste page. |
| TMDB API key or read access token | empty | Optional. A v3 API key or a v4 read access token from your TMDB account's API settings. When set, title data comes from TMDB directly instead of through Seerr. |
| Show titles you do not have yet | On | The four rows of titles to request. Needs Seerr or a TMDB key. Nothing is ever requested automatically. |
| Trakt client ID | empty | Create an API app on Trakt (Settings, Your API Apps, New application). Any https redirect URI works, for example `https://example.com/`; Taste never signs in to Trakt and only needs the client ID. |
| MDBList API key | empty | From your MDBList account preferences. The free tier allows 1,000 calls a day; Taste uses a few dozen per rebuild. |
| Seerr URL | empty | Seerr's address as Jellyfin can reach it, for example `http://seerr:5055`. |
| Seerr API key | empty | From Seerr, Settings, General. |
| Request every season of a series | Off | Series requests ask for season 1 unless this is on. |

Requests are sent to Seerr as the signed-in person, so their Seerr permissions, quotas, and approval rules apply. Taste finds the Seerr account linked to the Jellyfin account, or one with the same username, so each person needs to have signed in to Seerr with their Jellyfin account once.

## How it works

### Credits detection

When Jellyfin Web reports that playback stopped, the script waits a moment and asks the server whether to show the dialog. The server says yes when:

- the item is a movie, or the last regular episode of a series (or any episode, once every other regular episode is watched; specials never count),
- the stop was in the credits: at or after an outro segment that starts in the second half, or, with no outro segment, in the last part of the runtime set in the settings, or at the very end,
- and the person has not been asked about that title before and has not already given it both stars and a thumb.

"Not now" counts as asked.

### The profile

Every movie and series becomes a bag of ingredients: genres (Japanese animation also counts as "anime"), themes (Jellyfin tags, which the TMDB metadata provider fills with TMDB keywords), top five cast, directors and creators, writers, studios, original language, and decade. Each category gets a fixed share of the title, smaller when its values are common in your library (English, drama); inside a category, rarer values weigh more. Genres lead, so a long cast list cannot drown them.

A person's profile adds up the ingredients of the titles they gave signals for:

| Signal | Weight |
|--------|--------|
| 5 stars / 4 / 3 / 2 / 1 | +3.0 / +1.5 / +0.2 / -1.5 / -3.0 |
| Thumbs up / thumbs down | +2.0 / -2.5 (added to the stars) |
| Favourite, with no stars or thumb | +1.5 |
| Finished, with no stars or thumb (a movie played, or 80% of a series' episodes) | +0.4 |
| Requested in Seerr, now in the library, not watched yet | +0.4 |
| Not for me | -0.6 |

Stars are read from Jellyfin's own user rating (0-10), so ratings made in any client count. Leaving a show half-watched is not counted against it: dislikes only come from what a person said.

- **Dislikes land on what a title is.** A dislike counts against the title's genres and themes. An actor, director, writer, studio, language, or decade only counts against other titles once two separate dislikes share it, and then at about a third of the weight. One bad film does not bury everything its lead actor is in.
- **Older signals fade** with a one-year half-life. Stars and thumbs never fade below half their weight; favourites, finished titles, and Not for me never below a quarter; Seerr requests fade fully.
- **The Profile page** shows a genre once one title backs it, and themes, people, studios, language, and era once two titles do.

### Library rows

*Movies for you* and *Shows for you* rank every unwatched library title by how much its ingredients overlap the profile, plus a boost when fans of your favourites also liked it (see below), and small nudges for community rating, a release in the last three years, and being added in the last 60 days. Titles you have started, rated, thumbed down, marked Not for me, or said you have seen in calibration are left out, as are titles your account is not allowed to see. The top 20 are then picked so one franchise or director cannot fill the row.

### Fans also liked

For the ten titles a person likes most (and their four strongest stated dislikes), Taste collects TMDB's recommendations, which come from what TMDB members rate and list together, and, with a Trakt client ID, Trakt's related titles. A title listed by several favourites, near the top of a list, or by both TMDB and Trakt gets a stronger score; lists next to disliked titles count against.

### Rows of titles to request

- **Candidates:** fans-also-liked titles; Trakt trending and popular titles in the person's languages, filtered by IMDb rating (with Trakt); MDBList's catalog of recent well-scored titles (with MDBList); and TMDB discover by the person's top genres.
- **Taste gates:** anime only for people whose likes are at least 15% Japanese animation; animation only for people who watch some; a non-English original language only when it makes up 15% or more of their likes; no reality, talk, news, or soap TV unless they like that genre.
- **Quality gate:** enough TMDB votes, then MDBList's merged score (62 or more for new titles, 66 for older ones, with a minimum number of IMDb votes) when MDBList knows the title, otherwise a vote-count-aware TMDB rating of at least 6.6.
- **Score:** 40% fans-also-liked strength, 30% ingredient overlap (negative when a title leans into what the person dislikes), 25% quality, plus small nudges for freshness, several lists agreeing, and well-rated smaller titles.
- **Seerr status:** titles already available, partly available, or blocklisted in Seerr are left out; pending ones show as Requested.

### Caching and refresh

- The library index (every movie and series with its ingredients) is rebuilt every 30 minutes, or within 2 minutes after the library changes.
- Profiles are cached for 5 minutes, and dropped at once when the person rates, hides, or requests something.
- All four "to request" rows are built together and saved per person, in memory and in the plugin database, so the home screen opens straight away. A row older than 30 minutes, or built before the person's latest answers, still shows while a new one is built in the background. The first build for a person can take a little while.
- The *Refresh Taste discovery* scheduled task (Dashboard, Scheduled Tasks, every 6 hours by default) rebuilds everyone's rows ahead of time and prunes cached responses older than a month.
- Responses from outside services are cached in the plugin database: TMDB title details for 7 days, lists for 12 hours, MDBList scores for 3 days, Trakt id lookups for 30 days.

### Storage

- Stars are written to Jellyfin's own `UserData.Rating` (2, 4, 6, 8, or 10), so they survive removing the plugin.
- Everything else lives in one SQLite file, `taste.db`, in Jellyfin's data folder (for example `/var/lib/jellyfin/data/taste.db`, or `/config/data/taste.db` in Docker): thumbs, "already asked", Not for me, calibration answers, scores for titles outside the library, saved rows, and the response cache.
- The Sections tab writes each person's Jellyfin display preferences (`homesection0` to `homesection9`, plus a `taste-home` entry holding the full order including Taste rows) and their per-library "Latest" switches (`LatestItemsExcludes`).

### Moving to another server

`taste.db` can't simply be copied: item and user ids differ between servers. Instead, open **Taste → Profile** on the old server and click **Export** to download a JSON file with your stars, thumbs, Not for me titles, and calibration answers, each title carrying its TMDB, IMDb, and TVDB ids. On the new server, sign in as the person who should get them, open the same tab, and click **Import**.

Titles in the new library are matched by those ids and get their stars written to Jellyfin; the rest are kept as titles outside the library. Import only fills gaps: stars, thumbs, and Not for me already set on the new server are never replaced, so importing the same file twice is harmless. Watch history isn't included; Jellyfin has its own tools for that.

## Privacy

Taste has no telemetry and talks to nothing except the services you configure. Exactly what leaves your server:

- **Seerr** (the URL you set): the Seerr API key; requests for Seerr's user list (to find each person's Seerr account), each person's own requests, and the lists of pending and processing titles; TMDB lookups through Seerr's proxy (title details, recommendations, and discover queries made of genre ids, date ranges, and minimum ratings); and, when someone clicks Request, a request for that title as that person. Seerr then talks to TMDB itself.
- **TMDB** (`api.themoviedb.org`, only with a TMDB key): the same lookups as above, directly, with your key.
- **Trakt** (`api.trakt.tv`, only with a client ID): the TMDB ids of each person's strongest liked and disliked titles (to fetch related titles), and chart queries made of years, languages, and IMDb rating ranges.
- **MDBList** (`api.mdblist.com`, only with an API key): TMDB ids of candidate titles, in batches, to fetch scores, and catalog queries made of date ranges, minimum scores, and languages.

No usernames, ratings, or watch history are sent to TMDB, Trakt, or MDBList; they see your server's IP address, your key, and which titles it asks about, which reflect your household's taste. In the browser, posters for titles outside the library load straight from `image.tmdb.org`, and the Details button opens `themoviedb.org`.

## Troubleshooting

### The dialog or rows never appear

- Check the Jellyfin log for `Taste: added the web loader to Jellyfin Web` after opening Jellyfin Web. In the browser, view the page source: the end of the page should contain `<!-- Taste plugin -->`.
- If a reverse proxy or CDN caches `/web/index.html`, purge it after installing, or the old page without the loader keeps being served.
- Hard-refresh Jellyfin Web (Ctrl+F5) after installing or upgrading.
- Native apps never show the dialog or rows. Use Jellyfin Web.
- The rows only fill in once a person has some signal: rate a few titles in Taste, Calibrate. Empty rows are left off the home screen.

### The dialog did not show after a movie

It only shows once per title, and only when you stop in the credits. Without an outro segment, that means the last 10% of the runtime (or the window set in the settings); with one, it means at or after the outro. To see why, open `/Taste/Prompt?itemId=<item id>&positionTicks=<position>` while signed in; the `Why` field explains the decision.

### The "to request" rows are empty or missing

- They need Seerr or a TMDB key, and the "Show titles you do not have yet" setting.
- The first build per person can take a little while; the log shows a line like `Taste: discovery rows for <user>: new 18 movies / 20 shows, ...` when it is done. Running *Refresh Taste discovery* by hand builds everyone's rows.
- Look for `Seerr ... returned 401` (wrong API key), `Trakt ... returned 403` (wrong client ID), or `MDBList ... returned 403` in the log.

### MDBList returns 403 on `/catalog/...`

Your MDBList key or tier does not include the catalog endpoints. Scores still work if the key is valid; the catalog is just one candidate source among several, so the rows still fill without it. Remove the key if you want the warnings gone.

### Sections changes do not stick

Saving clears Jellyfin Web's own query cache (IndexedDB `keyval-store`, key `jellyfin-query-cache`) and reloads the page, because otherwise a reload within a minute shows the old sections. Other open tabs or devices keep the old layout until they reload. If a browser blocks IndexedDB, reload again after a minute. Changes to Jellyfin's own sections made under Settings, Home screen are picked up by the Sections tab the next time you open it.

### Request says "Sign in to Seerr once with this Jellyfin account"

Seerr has no account linked to that Jellyfin user. Sign in to Seerr with the Jellyfin account once, or import the user in Seerr.

## HTTP API

All routes are under `/Taste` and use the caller's Jellyfin session unless noted. They exist for the web script; there is no stability promise.

| Route | Purpose |
|-------|---------|
| `GET client.js`, `GET Page` | The web script and page fragment (anonymous, static) |
| `GET Status` | Which optional features are configured, and whether home rows are on |
| `GET Prompt?itemId&positionTicks` | Whether this stop should open the dialog, and why |
| `POST Rate` `{ItemId, Stars?, Thumb?}` | Save the dialog; no stars and no thumb means "Not now" |
| `GET Profile` | Signal counts and the top liked and disliked ingredients |
| `GET Recommendations?shelf=` | One row: `movies`, `shows`, `new-movies`, `new-shows`, `request-movies`, `request-shows` |
| `POST NotInterested` `{Key, Title?, Year?}` | Hide a title from every row |
| `GET NotInterested`, `POST NotInterested/Undo` `{Key}` | List hidden titles; undo one |
| `GET Calibration/Seen`, `POST Calibration/Seen` | The "Have you seen these?" cards, and an answer |
| `GET Calibration/Finished` | Finished titles without a score |
| `POST Request` `{MediaType, TmdbId}` | Request a title in Seerr as this person |
| `GET Export`, `POST Import` | Download this person's ratings as JSON; merge such a file into this person on this server |

## Development

```bash
./build.sh                                   # test and package
node --check src/Jellyfin.Plugin.Taste/Web/client.js
```

Layout:

- `src/Jellyfin.Plugin.Taste/`
  - `Api/` the HTTP controller
  - `Configuration/` settings and the admin page
  - `Data/` the SQLite store and the calibration deck
  - `Scoring/` ingredients, vector math, credits and finale rules, TMDB genre ids
  - `Services/` profile, recommendations, discovery, Seerr/TMDB/Trakt/MDBList clients, the scheduled task, the `index.html` loader (`WebInjectionFilter`), and the startup registration with Plugin Pages
  - `Web/` `client.js` (plain ES5-style JavaScript, no build step) and the Taste page fragment
- `tests/` xUnit tests for scoring, profile, discovery, parsing, and the store
- `build.sh`, `build/zip.proj` packaging; `.github/workflows/build.yml` CI and releases

To release: bump `AssemblyVersion` and `FileVersion` in the csproj, add a section to `CHANGELOG.md`, and push a tag `v<version>` (for example `v0.6.0.0`). The workflow builds, tests, and publishes the zip, its checksums, and `manifest.json`.

## Contributing

Issues and pull requests are welcome. This is a hobby project, so replies may be slow, and changes that make it harder to maintain may be declined. Please run `./build.sh` before sending a pull request.

## License

GPL-3.0-or-later. See [LICENSE](LICENSE). Taste links against Jellyfin's GPL-3.0 libraries.
