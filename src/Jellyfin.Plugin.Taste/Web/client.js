/*
 * Taste plugin for Jellyfin Web.
 * Watches the web client's own "playback stopped" report, asks the server whether this stop
 * was in the credits of a movie or a series finale, and if so shows a stars-and-thumb dialog.
 * Also adds Taste rows to the home screen and renders the Taste page (calibration, profile, home sections).
 */
(function () {
    'use strict';

    if (window.TasteUI) {
        return;
    }

    var SHELVES = [
        { shelf: 'movies', title: 'Movies for you' },
        { shelf: 'shows', title: 'Shows for you' },
        { shelf: 'new-movies', title: 'New movies to request', outside: true },
        { shelf: 'new-shows', title: 'New shows to request', outside: true },
        { shelf: 'request-movies', title: 'More movies to request', outside: true },
        { shelf: 'request-shows', title: 'More shows to request', outside: true }
    ];
    // Jellyfin Web's own home sections, as stored in the "usersettings" display preferences.
    var NATIVE_SECTIONS = [
        { key: 'smalllibrarytiles', title: 'My media' },
        { key: 'librarybuttons', title: 'My media (small)' },
        { key: 'activerecordings', title: 'Active recordings' },
        { key: 'resume', title: 'Continue watching' },
        { key: 'resumeaudio', title: 'Continue listening' },
        { key: 'resumebook', title: 'Continue reading' },
        { key: 'livetv', title: 'Live TV' },
        { key: 'nextup', title: 'Next up' },
        { key: 'latestmedia', title: 'Recently added media' }
    ];
    var DEFAULT_NATIVE = ['smalllibrarytiles', 'resume', 'resumeaudio', 'resumebook', 'livetv', 'nextup', 'latestmedia'];
    var SLOT_COUNT = 10;
    var PREFS_ID = 'usersettings';
    var PREFS_CLIENT = 'emby';
    var LAYOUT_PREF = 'taste-home';
    var LATEST_SKIP = ['playlists', 'livetv', 'boxsets', 'channels', 'folders'];
    var WEB_CACHE_DB = 'keyval-store';
    var WEB_CACHE_STORE = 'keyval';
    var WEB_CACHE_KEY = 'jellyfin-query-cache';
    var HOME_REFRESH_MS = 5 * 60 * 1000;
    var STOP_PATH = /\/Sessions\/Playing\/Stopped(\?|$)/i;
    var START_PATH = /\/Sessions\/Playing(\?|$)/i;
    var PROMPT_DELAY_MS = 1200;
    var pendingPrompt = null;

    // ---------- small helpers ----------

    function client() {
        return window.ApiClient;
    }

    function apiUrl(path, params) {
        return client().getUrl(path, params);
    }

    function getJson(path, params) {
        return client().getJSON(apiUrl(path, params));
    }

    function post(path, body, expectJson) {
        var request = {
            type: 'POST',
            url: apiUrl(path),
            data: JSON.stringify(body || {}),
            contentType: 'application/json'
        };
        if (expectJson) {
            request.dataType = 'json';
        }
        return client().ajax(request);
    }

    function el(tag, className, text) {
        var node = document.createElement(tag);
        if (className) {
            node.className = className;
        }
        if (text !== undefined && text !== null) {
            node.textContent = text;
        }
        return node;
    }

    function icon(name) {
        var span = el('span', 'material-icons taste-icon', name);
        span.setAttribute('aria-hidden', 'true');
        return span;
    }

    function button(label, className, onClick, iconName) {
        var b = el('button', 'taste-btn ' + (className || ''));
        b.type = 'button';
        if (iconName) {
            b.appendChild(icon(iconName));
        }
        if (label) {
            b.appendChild(el('span', null, label));
        }
        b.addEventListener('click', onClick);
        return b;
    }

    var statusRequest = null;
    function getStatus() {
        if (!statusRequest) {
            statusRequest = getJson('Taste/Status').catch(function (err) {
                statusRequest = null;
                throw err;
            });
        }
        return statusRequest;
    }

    function imageUrl(itemId, height) {
        return apiUrl('Items/' + itemId + '/Images/Primary', { maxHeight: height || 300, quality: 90 });
    }

    function toast(text) {
        var t = el('div', 'taste-toast', text);
        document.body.appendChild(t);
        setTimeout(function () { t.classList.add('taste-toast-out'); }, 2200);
        setTimeout(function () { t.remove(); }, 2700);
    }

    function undoToast(text, onUndo) {
        var t = el('div', 'taste-toast taste-toast-action');
        t.appendChild(el('span', null, text));
        var done = false;
        function close() {
            t.classList.add('taste-toast-out');
            setTimeout(function () { t.remove(); }, 500);
        }
        var undo = button('Undo', '', function () {
            if (done) {
                return;
            }
            done = true;
            close();
            onUndo();
        }, 'undo');
        t.appendChild(undo);
        document.querySelectorAll('.taste-toast-action').forEach(function (old) { old.remove(); });
        document.body.appendChild(t);
        setTimeout(function () {
            done = true;
            close();
        }, 8000);
    }

    function dismissTitle(rec, node, onRemoved) {
        var parent = node.parentNode;
        var next = node.nextSibling;
        post('Taste/NotInterested', { Key: rec.Key, Title: rec.Title, Year: rec.Year || null }).then(function () {
            node.remove();
            onRemoved();
            undoToast('Hidden "' + rec.Title + '". Find it later under Calibrate.', function () {
                post('Taste/NotInterested/Undo', { Key: rec.Key }).then(function () {
                    if (parent) {
                        parent.insertBefore(node, next && next.parentNode === parent ? next : null);
                    }
                    toast('Back in your list');
                }).catch(function () {
                    toast('Could not undo. Try the list under Calibrate.');
                });
            });
        }).catch(function () {
            toast('Could not hide that title');
        });
    }

    function injectStyles() {
        if (document.getElementById('taste-styles')) {
            return;
        }
        var style = el('style');
        style.id = 'taste-styles';
        style.textContent = [
            '.taste-overlay{position:fixed;inset:0;z-index:100000;background:rgba(0,0,0,.72);display:flex;align-items:center;justify-content:center;padding:1em;}',
            '.taste-dialog{background:#1c1c1e;color:#fff;border-radius:12px;max-width:26em;width:100%;padding:1.5em;box-shadow:0 10px 40px rgba(0,0,0,.6);text-align:center;font-family:inherit;}',
            '.taste-dialog img{max-height:12em;border-radius:8px;margin-bottom:.8em;}',
            '.taste-dialog h2{margin:.2em 0 .1em;font-size:1.35em;}',
            '.taste-sub{opacity:.7;margin:0 0 1em;font-size:.95em;}',
            '.taste-stars,.taste-thumbs{display:flex;justify-content:center;gap:.25em;margin:.6em 0;}',
            '.taste-btn{background:rgba(255,255,255,.08);color:inherit;border:1px solid rgba(255,255,255,.12);border-radius:8px;padding:.5em .9em;cursor:pointer;font:inherit;display:inline-flex;align-items:center;gap:.35em;}',
            '.taste-btn:hover,.taste-btn:focus{background:rgba(255,255,255,.18);outline:2px solid #00a4dc;outline-offset:1px;}',
            '.taste-btn.taste-on{background:#00a4dc;border-color:#00a4dc;color:#fff;}',
            '.taste-star{background:none;border:none;padding:.15em;font-size:1.9em;color:#ffc107;}',
            '.taste-star .taste-icon{font-size:1.15em;}',
            '.taste-star:hover,.taste-star:focus{background:none;outline:2px solid #00a4dc;}',
            '.taste-thumb{font-size:1.1em;}',
            '.taste-thumb.taste-on.taste-down{background:#c0392b;border-color:#c0392b;}',
            '.taste-actions{display:flex;justify-content:space-between;gap:.6em;margin-top:1.2em;}',
            '.taste-primary{background:#00a4dc;border-color:#00a4dc;}',
            '.taste-toast{position:fixed;bottom:2em;left:50%;transform:translateX(-50%);background:#00a4dc;color:#fff;padding:.6em 1.2em;border-radius:20px;z-index:100001;transition:opacity .4s;}',
            '.taste-toast-out{opacity:0;}',
            '.taste-toast-action{display:flex;align-items:center;gap:.8em;background:#333;padding:.4em .5em .4em 1.2em;}',
            '.taste-toast-action .taste-btn{margin:0;background:#00a4dc;color:#fff;}',
            '.taste-panel{position:fixed;inset:0;z-index:99999;background:#101010;color:#fff;overflow:auto;}',
            '.taste-panel-close{position:fixed;top:.8em;right:1em;z-index:100000;}',
            '.taste-page{padding:1.5em 3vw 4em;max-width:1600px;margin:0 auto;}',
            '.taste-tabs{display:flex;gap:.5em;margin:0 0 1.5em;flex-wrap:wrap;}',
            '.taste-row-title{font-size:1.25em;margin:1.4em 0 .5em;}',
            '.taste-notice{opacity:.7;margin:.4em 0 1em;}',
            '.taste-row{display:flex;gap:1em;overflow-x:auto;padding-bottom:.6em;}',
            '.taste-card{flex:0 0 12em;background:rgba(255,255,255,.05);border-radius:10px;overflow:hidden;display:flex;flex-direction:column;}',
            '.taste-card img,.taste-card .taste-noimg{width:100%;aspect-ratio:2/3;object-fit:cover;background:#222;display:block;}',
            '.taste-card .taste-noimg{display:flex;align-items:center;justify-content:center;opacity:.5;padding:1em;text-align:center;}',
            '.taste-card-body{padding:.6em;display:flex;flex-direction:column;gap:.35em;flex:1;}',
            '.taste-card-title{font-weight:600;}',
            '.taste-reason{font-size:.85em;opacity:.8;flex:1;}',
            '.taste-card-actions{display:flex;flex-wrap:wrap;gap:.3em;}',
            '.taste-card-actions .taste-btn{padding:.3em .55em;font-size:.85em;}',
            '.taste-card-poster{position:relative;}',
            '.taste-badge{position:absolute;top:.5em;left:.5em;background:#00a4dc;color:#fff;font-size:.75em;font-weight:600;padding:.15em .5em;border-radius:4px;}',
            '.taste-meta{font-size:.78em;opacity:.65;}',
            '.taste-overview{font-size:.8em;opacity:.75;display:-webkit-box;-webkit-line-clamp:4;-webkit-box-orient:vertical;overflow:hidden;}',
            '.taste-card-actions a.taste-btn{text-decoration:none;color:inherit;display:inline-flex;align-items:center;}',
            '.taste-card .taste-star{font-size:1.3em;}',
            '.taste-chips{display:flex;flex-wrap:wrap;gap:.4em;}',
            '.taste-chip{background:rgba(255,255,255,.08);border-radius:14px;padding:.3em .8em;font-size:.9em;}',
            '.taste-chip small{opacity:.6;margin-left:.4em;}',
            '.taste-chip.taste-neg{background:rgba(192,57,43,.25);}',
            '.taste-header-btn .taste-icon{font-size:24px;line-height:1;}',
            '.taste-home-card .cardImageContainer,.taste-home-card .cardOverlayContainer{cursor:pointer;}',
            '.taste-home-card .taste-badge{z-index:2;}',
            '.taste-home-noimg{display:flex;align-items:center;justify-content:center;text-align:center;padding:.5em;opacity:.7;}',
            '.taste-scroll-fallback{overflow-x:auto;padding-left:3.3%;padding-right:3.3%;}',
            '.taste-scroll-fallback .scrollSlider{display:flex;white-space:nowrap;}',
            '.taste-details{max-width:46em;text-align:left;display:flex;gap:1.3em;align-items:flex-start;}',
            '.taste-details img{width:12em;max-height:none;flex:0 0 auto;margin:0;}',
            '.taste-details-body{display:flex;flex-direction:column;gap:.5em;min-width:0;}',
            '.taste-details-body h2{margin:0;}',
            '.taste-details-body .taste-overview{-webkit-line-clamp:8;font-size:.92em;}',
            '.taste-details .taste-card-actions{margin-top:.5em;}',
            '.taste-sections{display:flex;flex-direction:column;gap:.4em;max-width:36em;}',
            '.taste-section-row{background:rgba(255,255,255,.05);border-radius:8px;padding:.35em .7em .35em .2em;}',
            '.taste-section-line{display:flex;align-items:center;gap:.6em;}',
            '.taste-section-libs{display:flex;flex-direction:column;gap:.35em;margin:.3em 0 .25em 2.4em;padding-left:.8em;border-left:2px solid rgba(255,255,255,.1);}',
            '.taste-section-lib{font-size:.92em;}',
            '.taste-section-off .taste-section-libs{opacity:.45;}',
            '.taste-section-dragging{background:rgba(0,164,220,.25);box-shadow:0 4px 16px rgba(0,0,0,.5);}',
            '.taste-section-handle{background:none;border:none;color:inherit;cursor:grab;touch-action:none;padding:.3em;display:flex;opacity:.7;}',
            '.taste-section-handle:focus{outline:2px solid #00a4dc;opacity:1;}',
            '.taste-section-dragging .taste-section-handle{cursor:grabbing;}',
            '.taste-section-name{flex:1;}',
            '.taste-section-name small{margin-left:.6em;background:#00a4dc;border-radius:4px;padding:.05em .45em;font-size:.72em;font-weight:600;vertical-align:middle;}',
            '.taste-section-off .taste-section-name{opacity:.45;}',
            '.taste-switch{position:relative;display:inline-block;width:2.6em;height:1.4em;flex:0 0 auto;}',
            '.taste-switch input{position:absolute;inset:0;width:100%;height:100%;margin:0;opacity:0;cursor:pointer;z-index:1;}',
            '.taste-switch-track{position:absolute;inset:0;background:rgba(255,255,255,.2);border-radius:1em;transition:background .2s;}',
            '.taste-switch-track::after{content:"";position:absolute;top:.2em;left:.2em;width:1em;height:1em;border-radius:50%;background:#fff;transition:transform .2s;}',
            '.taste-switch input:checked+.taste-switch-track{background:#00a4dc;}',
            '.taste-switch input:checked+.taste-switch-track::after{transform:translateX(1.2em);}',
            '.taste-switch input:focus-visible+.taste-switch-track{outline:2px solid #00a4dc;outline-offset:2px;}',
            '.taste-sections-actions{max-width:36em;}',
            '@media (max-width:40em){.taste-details{flex-direction:column;align-items:center;}.taste-details img{width:9em;}}'
        ].join('\n');
        document.head.appendChild(style);
    }

    // ---------- stars and thumbs control ----------

    function ratingControl(initialStars, initialThumb, compact) {
        var state = { stars: initialStars || null, thumb: initialThumb || null };
        var wrap = el('div', 'taste-rating');
        var stars = el('div', 'taste-stars');
        var starButtons = [];
        for (var i = 1; i <= 5; i++) {
            (function (value) {
                var b = el('button', 'taste-btn taste-star');
                b.type = 'button';
                b.setAttribute('aria-label', value + (value === 1 ? ' star' : ' stars'));
                b.appendChild(icon('star_border'));
                b.addEventListener('click', function () {
                    state.stars = state.stars === value ? null : value;
                    paint();
                });
                starButtons.push(b);
                stars.appendChild(b);
            })(i);
        }

        var thumbs = el('div', 'taste-thumbs');
        var up = button(compact ? '' : 'Thumbs up', 'taste-thumb', function () {
            state.thumb = state.thumb === 1 ? null : 1;
            paint();
        }, 'thumb_up');
        up.setAttribute('aria-label', 'Thumbs up');
        var down = button(compact ? '' : 'Thumbs down', 'taste-thumb taste-down', function () {
            state.thumb = state.thumb === -1 ? null : -1;
            paint();
        }, 'thumb_down');
        down.setAttribute('aria-label', 'Thumbs down');
        thumbs.appendChild(up);
        thumbs.appendChild(down);

        function paint() {
            starButtons.forEach(function (b, index) {
                var filled = state.stars !== null && index < state.stars;
                b.firstChild.textContent = filled ? 'star' : 'star_border';
                b.setAttribute('aria-pressed', filled ? 'true' : 'false');
            });
            up.classList.toggle('taste-on', state.thumb === 1);
            down.classList.toggle('taste-on', state.thumb === -1);
            up.setAttribute('aria-pressed', state.thumb === 1 ? 'true' : 'false');
            down.setAttribute('aria-pressed', state.thumb === -1 ? 'true' : 'false');
        }

        paint();
        wrap.appendChild(stars);
        wrap.appendChild(thumbs);
        return { node: wrap, state: state, firstFocus: starButtons[0] };
    }

    // ---------- the finish dialog ----------

    function closeDialog() {
        var existing = document.querySelector('.taste-overlay');
        if (existing) {
            existing.remove();
        }
    }

    function showDialog(decision) {
        injectStyles();
        closeDialog();

        var overlay = el('div', 'taste-overlay');
        overlay.setAttribute('role', 'dialog');
        overlay.setAttribute('aria-modal', 'true');
        overlay.setAttribute('aria-label', 'Rate ' + decision.Name);

        var dialog = el('div', 'taste-dialog');
        var img = el('img');
        img.alt = '';
        img.src = imageUrl(decision.TargetId, 360);
        img.onerror = function () { img.remove(); };
        dialog.appendChild(img);
        dialog.appendChild(el('h2', null, 'How was ' + decision.Name + '?'));
        dialog.appendChild(el('p', 'taste-sub', decision.MediaType === 'Series'
            ? 'You just finished the series.'
            : (decision.Year ? String(decision.Year) : 'You just finished it.')));

        var control = ratingControl(decision.Stars, decision.Thumb, false);
        dialog.appendChild(control.node);

        var finished = false;
        function finish(save) {
            if (finished) {
                return;
            }
            finished = true;
            var body = { ItemId: decision.TargetId, Stars: null, Thumb: null };
            if (save) {
                body.Stars = control.state.stars;
                body.Thumb = control.state.thumb;
            }
            overlay.remove();
            document.removeEventListener('keydown', onKey, true);
            post('Taste/Rate', body).then(function () {
                if (save && (body.Stars || body.Thumb)) {
                    toast('Thanks — saved.');
                }
            }).catch(function (err) {
                console.warn('Taste: could not save rating', err);
            });
        }

        function onKey(e) {
            if (e.key === 'Escape' || e.key === 'Backspace' && e.target === document.body) {
                e.preventDefault();
                e.stopPropagation();
                finish(false);
            }
        }

        var actions = el('div', 'taste-actions');
        actions.appendChild(button('Not now', '', function () { finish(false); }));
        actions.appendChild(button('Save', 'taste-primary', function () { finish(true); }, 'check'));
        dialog.appendChild(actions);

        overlay.appendChild(dialog);
        overlay.addEventListener('click', function (e) {
            if (e.target === overlay) {
                finish(false);
            }
        });
        document.addEventListener('keydown', onKey, true);
        document.body.appendChild(overlay);
        setTimeout(function () { control.firstFocus.focus(); }, 50);
    }

    function checkPrompt(itemId, positionTicks) {
        pendingPrompt = null;
        if (!client()) {
            return;
        }
        var params = { itemId: itemId };
        if (positionTicks !== undefined && positionTicks !== null) {
            params.positionTicks = positionTicks;
        }
        getJson('Taste/Prompt', params).then(function (decision) {
            if (decision && decision.Prompt) {
                showDialog(decision);
            }
        }).catch(function (err) {
            console.debug('Taste: prompt check failed', err);
        });
    }

    // ---------- playback stop detection ----------

    function onStopped(bodyText) {
        var data;
        try {
            data = JSON.parse(bodyText);
        } catch (e) {
            return;
        }
        var itemId = data.ItemId || data.itemId;
        if (!itemId) {
            return;
        }
        var position = data.PositionTicks !== undefined ? data.PositionTicks : data.positionTicks;
        clearTimeout(pendingPrompt);
        pendingPrompt = setTimeout(function () { checkPrompt(itemId, position); }, PROMPT_DELAY_MS);
    }

    function onStarted() {
        clearTimeout(pendingPrompt);
        pendingPrompt = null;
    }

    function inspect(url, method, body) {
        if (!url || String(method || 'GET').toUpperCase() !== 'POST') {
            return;
        }
        url = String(url);
        if (STOP_PATH.test(url)) {
            if (typeof body === 'string') {
                onStopped(body);
            }
        } else if (START_PATH.test(url)) {
            onStarted();
        }
    }

    var originalFetch = window.fetch;
    if (originalFetch) {
        window.fetch = function (input, init) {
            try {
                var url = typeof input === 'string' ? input : (input && input.url);
                var method = (init && init.method) || (input && input.method) || 'GET';
                inspect(url, method, init && init.body);
            } catch (e) {
                // never break the web client's own requests
            }
            return originalFetch.apply(this, arguments);
        };
    }

    var originalOpen = XMLHttpRequest.prototype.open;
    var originalSend = XMLHttpRequest.prototype.send;
    XMLHttpRequest.prototype.open = function (method, url) {
        this.__taste = { method: method, url: url };
        return originalOpen.apply(this, arguments);
    };
    XMLHttpRequest.prototype.send = function (body) {
        try {
            if (this.__taste) {
                inspect(this.__taste.url, this.__taste.method, body);
            }
        } catch (e) {
            // never break the web client's own requests
        }
        return originalSend.apply(this, arguments);
    };

    // ---------- the Taste page ----------

    function detailsHash(itemId) {
        return '#/details?id=' + encodeURIComponent(itemId) + '&serverId=' + encodeURIComponent(client().serverId());
    }

    function goToItem(itemId) {
        closePanel();
        window.location.hash = detailsHash(itemId);
    }

    function poster(card) {
        if (card.ItemId) {
            var img = el('img');
            img.loading = 'lazy';
            img.alt = '';
            img.src = imageUrl(card.ItemId, 360);
            return img;
        }
        if (card.ImageUrl) {
            var remote = el('img');
            remote.loading = 'lazy';
            remote.alt = '';
            remote.referrerPolicy = 'no-referrer';
            remote.src = card.ImageUrl;
            return remote;
        }
        return el('div', 'taste-noimg', card.Title);
    }

    function cardShell(card) {
        var node = el('div', 'taste-card');
        var art = el('div', 'taste-card-poster');
        art.appendChild(poster(card));
        if (card.Badge) {
            art.appendChild(el('span', 'taste-badge', card.Badge));
        }
        node.appendChild(art);
        var body = el('div', 'taste-card-body');
        body.appendChild(el('div', 'taste-card-title', card.Title + (card.Year ? ' (' + card.Year + ')' : '')));
        node.appendChild(body);
        return { node: node, body: body };
    }

    function recommendationCard(rec, status, onRemoved) {
        var shell = cardShell(rec);
        if (rec.Meta) {
            shell.body.appendChild(el('div', 'taste-meta', rec.Meta));
        }
        if (!rec.ItemId && rec.Overview) {
            var overview = el('div', 'taste-overview', rec.Overview);
            overview.title = rec.Overview;
            shell.body.appendChild(overview);
        }
        shell.body.appendChild(el('div', 'taste-reason', rec.Reason));
        shell.body.appendChild(recommendationActions(rec, status, function () {
            dismissTitle(rec, shell.node, onRemoved);
        }));
        return shell.node;
    }

    function requestTitle(rec, onDone) {
        return post('Taste/Request', { MediaType: rec.MediaType, TmdbId: parseInt(rec.TmdbId, 10) }, true).then(function (result) {
            toast(result.Message);
            if (result.Ok) {
                rec.Status = 'requested';
            }
            onDone(result.Ok);
        }).catch(function () {
            toast('Request failed');
            onDone(false);
        });
    }

    function recommendationActions(rec, status, onDismiss) {
        var actions = el('div', 'taste-card-actions');
        if (rec.ItemId) {
            actions.appendChild(button('Open', '', function () { goToItem(rec.ItemId); }, 'play_arrow'));
        } else if (rec.Status === 'requested') {
            var pending = button('Requested', '', function () {}, 'schedule');
            pending.disabled = true;
            actions.appendChild(pending);
        } else if (status.Seerr && rec.TmdbId) {
            var req = button('Request', '', function () {
                req.disabled = true;
                requestTitle(rec, function (ok) {
                    if (ok) {
                        req.replaceChildren(icon('schedule'), el('span', null, 'Requested'));
                    } else {
                        req.disabled = false;
                    }
                });
            }, 'add');
            actions.appendChild(req);
        }
        if (!rec.ItemId && rec.Link) {
            var details = el('a', 'taste-btn', 'Details');
            details.href = rec.Link;
            details.target = '_blank';
            details.rel = 'noopener noreferrer';
            actions.appendChild(details);
        }
        actions.appendChild(button('Not for me', '', onDismiss, 'block'));
        return actions;
    }

    function loadRow(container, title, shelf, status) {
        var heading = el('h2', 'taste-row-title', title);
        var notice = el('div', 'taste-notice', shelf === 'movies' || shelf === 'shows'
            ? 'Loading…'
            : 'Looking for titles you do not have yet… the very first time can take a little while.');
        var row = el('div', 'taste-row');
        container.appendChild(heading);
        container.appendChild(notice);
        container.appendChild(row);
        getJson('Taste/Recommendations', { shelf: shelf }).then(function (result) {
            notice.textContent = result.Notice || '';
            notice.style.display = result.Notice ? '' : 'none';
            result.Items.forEach(function (rec) {
                row.appendChild(recommendationCard(rec, status, function () {}));
            });
        }).catch(function () {
            notice.textContent = 'Could not load this row.';
        });
    }

    function renderForYou(view, status) {
        if (!status.Discover && status.DiscoverNotice && status.DiscoverNotice !== 'switched off') {
            view.appendChild(el('div', 'taste-notice', 'Titles you do not have yet will show here once an admin connects Seerr in the Taste plugin settings.'));
        }
        SHELVES.forEach(function (row) {
            if (!row.outside || status.Discover) {
                loadRow(view, row.title, row.shelf, status);
            }
        });
    }

    function seenCard(card, onAnswered) {
        var shell = cardShell(card);
        var rating = el('div');
        var actions = el('div', 'taste-card-actions');
        var answered = false;

        function send(seen, stars, thumb) {
            if (answered) {
                return;
            }
            answered = true;
            post('Taste/Calibration/Seen', {
                DeckKey: card.Key,
                Seen: seen,
                Stars: stars,
                Thumb: thumb,
                Title: card.Title,
                Year: card.Year
            }, true).then(function (outsideCount) {
                shell.node.style.opacity = '0.35';
                rating.replaceChildren();
                actions.replaceChildren(el('span', 'taste-reason', seen === true ? 'Saved' : seen === false ? 'Not seen' : 'Skipped'));
                onAnswered(outsideCount);
            }).catch(function () {
                answered = false;
                toast('Could not save that answer');
            });
        }

        function showChoices() {
            rating.replaceChildren();
            actions.replaceChildren(
                button('Seen it', '', showRating),
                button('Not seen', '', function () { send(false, null, null); }),
                button('Skip', '', function () { send(null, null, null); }));
        }

        function showRating() {
            var control = ratingControl(null, null, true);
            rating.replaceChildren(control.node);
            actions.replaceChildren(
                button('Save', 'taste-primary', function () {
                    send(true, control.state.stars, control.state.thumb);
                }, 'check'),
                button('Back', '', showChoices));
        }

        showChoices();
        shell.body.appendChild(rating);
        shell.body.appendChild(actions);
        return shell.node;
    }

    function finishedCard(card, onAnswered) {
        var shell = cardShell(card);
        var control = ratingControl(null, null, true);
        shell.body.appendChild(control.node);
        var actions = el('div', 'taste-card-actions');
        actions.appendChild(button('Save', 'taste-primary', function () {
            if (!control.state.stars && !control.state.thumb) {
                toast('Pick stars or a thumb first');
                return;
            }
            post('Taste/Rate', { ItemId: card.ItemId, Stars: control.state.stars, Thumb: control.state.thumb }).then(function () {
                shell.node.style.opacity = '0.35';
                actions.replaceChildren(el('span', 'taste-reason', 'Saved'));
                onAnswered();
            }).catch(function () {
                toast('Could not save that rating');
            });
        }, 'check'));
        shell.body.appendChild(actions);
        return shell.node;
    }

    function renderSeenPass(block) {
        block.replaceChildren();
        block.appendChild(el('h2', 'taste-row-title', 'Have you seen these?'));
        var notice = el('div', 'taste-notice', 'Loading…');
        var row = el('div', 'taste-row');
        var more = el('div', 'taste-actions');
        block.appendChild(notice);
        block.appendChild(row);
        block.appendChild(more);
        getJson('Taste/Calibration/Seen').then(function (set) {
            var remaining = set.Cards.length;
            notice.textContent = set.Cards.length === 0
                ? 'You have been through the whole deck.'
                : 'Seen it? Give it stars or a thumb. Not seen or skip moves on. (' + set.OutsideAnswers + ' of about ' + set.OutsideTarget + ' rated)';
            set.Cards.forEach(function (card) {
                row.appendChild(seenCard(card, function (outsideCount) {
                    remaining--;
                    notice.textContent = 'Seen it? Give it stars or a thumb. Not seen or skip moves on. (' + outsideCount + ' of about ' + set.OutsideTarget + ' rated)';
                    if (remaining === 0) {
                        more.replaceChildren(button(outsideCount >= set.OutsideTarget ? 'That is plenty — five more anyway' : 'Five more', '', function () {
                            renderSeenPass(block);
                        }, 'refresh'));
                    }
                }));
            });
        }).catch(function () {
            notice.textContent = 'Could not load the deck.';
        });
    }

    function renderFinishedPass(block) {
        block.replaceChildren();
        block.appendChild(el('h2', 'taste-row-title', 'Rate five you finished'));
        var notice = el('div', 'taste-notice', 'Loading…');
        var row = el('div', 'taste-row');
        var more = el('div', 'taste-actions');
        block.appendChild(notice);
        block.appendChild(row);
        block.appendChild(more);
        getJson('Taste/Calibration/Finished').then(function (set) {
            var remaining = set.Cards.length;
            notice.textContent = set.Cards.length === 0
                ? 'Everything you finished here already has a score.'
                : 'These are finished but never scored.';
            set.Cards.forEach(function (card) {
                row.appendChild(finishedCard(card, function () {
                    remaining--;
                    if (remaining === 0) {
                        more.replaceChildren(button('Five more', '', function () { renderFinishedPass(block); }, 'refresh'));
                    }
                }));
            });
        }).catch(function () {
            notice.textContent = 'Could not load your history.';
        });
    }

    function dismissedCard(card, onRestored) {
        var shell = cardShell(card);
        shell.body.appendChild(el('div', 'taste-reason', 'You said this is not for you. It stays out of every row and counts as a mild dislike.'));
        var actions = el('div', 'taste-card-actions');
        var undo = button('Undo', '', function () {
            undo.disabled = true;
            post('Taste/NotInterested/Undo', { Key: card.Key }).then(function () {
                shell.node.remove();
                onRestored();
                toast('"' + card.Title + '" can show up again');
            }).catch(function () {
                undo.disabled = false;
                toast('Could not undo');
            });
        }, 'undo');
        actions.appendChild(undo);
        shell.body.appendChild(actions);
        return shell.node;
    }

    function renderDismissed(block) {
        block.replaceChildren();
        block.appendChild(el('h2', 'taste-row-title', 'Not for me'));
        var notice = el('div', 'taste-notice', 'Loading…');
        var row = el('div', 'taste-row');
        block.appendChild(notice);
        block.appendChild(row);
        getJson('Taste/NotInterested').then(function (cards) {
            var remaining = cards.length;
            function update() {
                notice.textContent = remaining === 0
                    ? 'Nothing hidden. Titles you mark "Not for me" land here, in case you change your mind.'
                    : 'Hidden from every row. Undo if you clicked by mistake.';
            }
            update();
            cards.forEach(function (card) {
                row.appendChild(dismissedCard(card, function () {
                    remaining--;
                    update();
                }));
            });
        }).catch(function () {
            notice.textContent = 'Could not load hidden titles.';
        });
    }

    function renderCalibrate(view) {
        var seen = el('div');
        var finished = el('div');
        var dismissed = el('div');
        view.appendChild(seen);
        view.appendChild(finished);
        view.appendChild(dismissed);
        renderSeenPass(seen);
        renderFinishedPass(finished);
        renderDismissed(dismissed);
    }

    function chips(features, negative) {
        var wrap = el('div', 'taste-chips');
        features.forEach(function (f) {
            var chip = el('span', 'taste-chip' + (negative ? ' taste-neg' : ''), f.Label);
            chip.appendChild(el('small', null, f.Category));
            wrap.appendChild(chip);
        });
        return wrap;
    }

    function renderProfile(view) {
        var notice = el('div', 'taste-notice', 'Loading…');
        view.appendChild(notice);
        getJson('Taste/Profile').then(function (p) {
            notice.textContent = p.Signals + ' titles shape this profile: ' + p.Scored + ' with stars or a thumb, ' + p.Finished + ' finished.';
            view.appendChild(el('h2', 'taste-row-title', 'What you lean towards'));
            view.appendChild(p.Likes.length ? chips(p.Likes, false) : el('div', 'taste-notice', 'Nothing yet. Rate a few titles.'));
            if (p.Dislikes.length) {
                view.appendChild(el('h2', 'taste-row-title', 'What you steer away from'));
                view.appendChild(chips(p.Dislikes, true));
            }
        }).catch(function () {
            notice.textContent = 'Could not load your profile.';
        }).then(function () {
            view.appendChild(transferBlock());
        });
    }

    function transferBlock() {
        var block = el('div');
        block.appendChild(el('h2', 'taste-row-title', 'Move your ratings'));
        var notice = el('div', 'taste-notice', 'Export saves your stars, thumbs, "Not for me" titles, and calibration answers to a file. '
            + 'Import adds a file like that to your account on this server, matching titles by their TMDB, IMDb, or TVDB ids. '
            + 'Anything you have already rated here is kept as it is.');
        block.appendChild(notice);

        var picker = el('input');
        picker.type = 'file';
        picker.accept = '.json,application/json';
        picker.style.display = 'none';
        picker.addEventListener('change', function () {
            var file = picker.files && picker.files[0];
            picker.value = '';
            if (!file) {
                return;
            }
            file.text().then(function (text) {
                return post('Taste/Import', JSON.parse(text), true);
            }).then(function (r) {
                notice.textContent = 'Imported: ' + r.InLibrary + ' titles in this library, ' + r.Outside + ' titles you do not have, '
                    + r.Calibration + ' calibration answers. ' + r.AlreadyRated + ' were already rated here and left alone'
                    + (r.Skipped ? ', ' + r.Skipped + ' could not be matched.' : '.');
                toast('Ratings imported');
            }).catch(function () {
                toast('That file could not be imported');
            });
        });

        var actions = el('div', 'taste-card-actions');
        actions.appendChild(button('Export', '', function () {
            getJson('Taste/Export').then(function (data) {
                var link = el('a');
                link.href = URL.createObjectURL(new Blob([JSON.stringify(data, null, 2)], { type: 'application/json' }));
                link.download = 'taste-export-' + new Date().toISOString().slice(0, 10) + '.json';
                document.body.appendChild(link);
                link.click();
                link.remove();
                setTimeout(function () { URL.revokeObjectURL(link.href); }, 10000);
            }).catch(function () {
                toast('Could not export your ratings');
            });
        }, 'download'));
        actions.appendChild(button('Import', '', function () { picker.click(); }, 'upload'));
        block.appendChild(actions);
        block.appendChild(picker);
        return block;
    }

    // ---------- home section order ----------

    function tasteKey(row) {
        return 'taste:' + row.shelf;
    }

    function availableShelves(status) {
        if (!status.HomeRows) {
            return [];
        }
        return SHELVES.filter(function (row) { return !row.outside || status.Discover; });
    }

    function nativeSection(key) {
        for (var i = 0; i < NATIVE_SECTIONS.length; i++) {
            if (NATIVE_SECTIONS[i].key === key) {
                return NATIVE_SECTIONS[i];
            }
        }
        return null;
    }

    function parseLayout(value) {
        try {
            var parsed = JSON.parse(value || 'null');
            return parsed && Array.isArray(parsed.order) ? parsed : null;
        } catch (e) {
            return null;
        }
    }

    function getDisplayPrefs() {
        var api = client();
        return api.getDisplayPreferences(PREFS_ID, api.getCurrentUserId(), PREFS_CLIENT).then(function (prefs) {
            prefs.CustomPrefs = prefs.CustomPrefs || {};
            return prefs;
        });
    }

    // Mirrors how Jellyfin Web reads homesection0..9: a missing slot falls back to its default.
    function readSlots(prefs) {
        var custom = prefs.CustomPrefs || {};
        var slots = [];
        for (var i = 0; i < SLOT_COUNT; i++) {
            var value = custom['homesection' + i] || DEFAULT_NATIVE[i] || 'none';
            slots.push(value === 'folders' ? 'smalllibrarytiles' : value);
        }
        return slots;
    }

    function buildLayout(prefs, status) {
        var slots = readSlots(prefs);
        var enabled = slots.filter(function (key, index) {
            return nativeSection(key) && slots.indexOf(key) === index;
        });
        var shelves = availableShelves(status);
        var known = NATIVE_SECTIONS.map(function (s) { return s.key; }).concat(shelves.map(tasteKey));
        var saved = parseLayout(prefs.CustomPrefs[LAYOUT_PREF]);
        var order;
        if (saved) {
            order = saved.order.filter(function (key, index, all) {
                return known.indexOf(key) >= 0 && all.indexOf(key) === index;
            });
        } else {
            order = enabled.slice();
            var anchor = Math.max(order.indexOf('resume'), order.indexOf('nextup'), order.length ? 0 : -1);
            Array.prototype.splice.apply(order, [anchor + 1, 0].concat(shelves.map(tasteKey)));
        }
        known.forEach(function (key) {
            if (order.indexOf(key) < 0) {
                order.push(key);
            }
        });
        // Jellyfin's own Home screen settings may have reordered its sections since we last saved; follow them.
        var next = 0;
        order = order.map(function (key) {
            return enabled.indexOf(key) >= 0 ? enabled[next++] : key;
        });
        var off = saved && Array.isArray(saved.off) ? saved.off : [];
        return {
            slots: slots,
            items: order.map(function (key) {
                var native = nativeSection(key);
                var shelf = native ? null : shelves.filter(function (row) { return tasteKey(row) === key; })[0];
                return {
                    key: key,
                    title: native ? native.title : shelf.title,
                    taste: !native,
                    on: native ? enabled.indexOf(key) >= 0 : off.indexOf(key) < 0
                };
            })
        };
    }

    function saveLayout(items) {
        var api = client();
        return getDisplayPrefs().then(function (prefs) {
            var custom = prefs.CustomPrefs;
            var natives = items.filter(function (item) { return !item.taste && item.on; });
            for (var i = 0; i < SLOT_COUNT; i++) {
                custom['homesection' + i] = natives[i] ? natives[i].key : 'none';
            }
            // Keep rows this user cannot see right now (Seerr off, say) where they were.
            var keys = items.map(function (item) { return item.key; });
            var saved = parseLayout(custom[LAYOUT_PREF]) || { order: [], off: [] };
            var hidden = function (key) { return keys.indexOf(key) < 0; };
            custom[LAYOUT_PREF] = JSON.stringify({
                order: keys.concat(saved.order.filter(hidden)),
                off: items.filter(function (item) { return item.taste && !item.on; })
                    .map(function (item) { return item.key; })
                    .concat((saved.off || []).filter(hidden))
            });
            return api.updateDisplayPreferences(PREFS_ID, prefs, api.getCurrentUserId(), PREFS_CLIENT);
        });
    }

    // Libraries Jellyfin gives a "Latest" row inside Recently added media.
    function latestLibraries(views, user) {
        var excludes = (user.Configuration && user.Configuration.LatestItemsExcludes) || [];
        return (views.Items || []).filter(function (view) {
            return view.Id && LATEST_SKIP.indexOf(view.CollectionType || '') < 0;
        }).map(function (view) {
            return { id: view.Id, title: 'Latest ' + view.Name, on: excludes.indexOf(view.Id) < 0 };
        });
    }

    function saveLibraries(libraries) {
        var api = client();
        var userId = api.getCurrentUserId();
        return api.getUser(userId).then(function (user) {
            var config = user.Configuration;
            var ids = libraries.map(function (lib) { return lib.id; });
            config.LatestItemsExcludes = (config.LatestItemsExcludes || [])
                .filter(function (id) { return ids.indexOf(id) < 0; })
                .concat(libraries.filter(function (lib) { return !lib.on; }).map(function (lib) { return lib.id; }));
            return api.updateUserConfiguration(userId, config);
        });
    }

    // Jellyfin Web persists its query cache in IndexedDB (idb-keyval) and trusts it for a minute after a
    // reload, which brings back the old home sections. Dropping it makes the reload fetch fresh settings.
    function dropWebCache() {
        return new Promise(function (resolve) {
            var open;
            try {
                open = window.indexedDB.open(WEB_CACHE_DB);
            } catch (e) {
                resolve();
                return;
            }
            open.onupgradeneeded = function () {
                open.result.createObjectStore(WEB_CACHE_STORE);
            };
            open.onerror = open.onblocked = function () { resolve(); };
            open.onsuccess = function () {
                var db = open.result;
                try {
                    var tx = db.transaction(WEB_CACHE_STORE, 'readwrite');
                    tx.objectStore(WEB_CACHE_STORE).delete(WEB_CACHE_KEY);
                    tx.oncomplete = tx.onerror = tx.onabort = function () {
                        db.close();
                        resolve();
                    };
                } catch (e) {
                    db.close();
                    resolve();
                }
            };
        });
    }

    function switchControl(label, checked, onToggle) {
        var wrap = el('label', 'taste-switch');
        var box = el('input');
        box.type = 'checkbox';
        box.checked = checked;
        box.setAttribute('aria-label', label);
        box.addEventListener('change', function () { onToggle(box.checked); });
        wrap.appendChild(box);
        wrap.appendChild(el('span', 'taste-switch-track'));
        return wrap;
    }

    function sectionRow(item, list, onChange) {
        var row = el('div', 'taste-section-row');
        row.tasteItem = item;
        var line = el('div', 'taste-section-line');

        var handle = el('button', 'taste-section-handle');
        handle.type = 'button';
        handle.title = 'Drag to move';
        handle.setAttribute('aria-label', 'Move ' + item.title + ' with the arrow keys');
        handle.appendChild(icon('drag_indicator'));

        var name = el('div', 'taste-section-name', item.title);
        if (item.taste) {
            name.appendChild(el('small', null, 'Taste'));
        }

        function paint() {
            row.classList.toggle('taste-section-off', !item.on);
        }

        line.appendChild(handle);
        line.appendChild(name);
        line.appendChild(switchControl('Show ' + item.title, item.on, function (on) {
            item.on = on;
            paint();
            onChange();
        }));
        row.appendChild(line);
        paint();

        if (item.libraries && item.libraries.length) {
            var libs = el('div', 'taste-section-libs');
            item.libraries.forEach(function (lib) {
                var libLine = el('div', 'taste-section-line taste-section-lib');
                var libName = el('div', 'taste-section-name', lib.title);
                libLine.appendChild(libName);
                libLine.appendChild(switchControl('Show ' + lib.title, lib.on, function (on) {
                    lib.on = on;
                    libLine.classList.toggle('taste-section-off', !on);
                    onChange();
                }));
                libLine.classList.toggle('taste-section-off', !lib.on);
                libs.appendChild(libLine);
            });
            row.appendChild(libs);
        }

        handle.addEventListener('keydown', function (e) {
            var up = e.key === 'ArrowUp';
            var other = up ? row.previousElementSibling : e.key === 'ArrowDown' ? row.nextElementSibling : null;
            if (!other) {
                return;
            }
            e.preventDefault();
            list.insertBefore(row, up ? other : other.nextElementSibling);
            handle.focus();
            onChange();
        });

        handle.addEventListener('pointerdown', function (e) {
            if (e.button) {
                return;
            }
            e.preventDefault();
            row.classList.add('taste-section-dragging');
            var moved = false;

            function onMove(ev) {
                var before = null;
                var others = Array.prototype.filter.call(list.children, function (node) { return node !== row; });
                for (var i = 0; i < others.length; i++) {
                    var rect = others[i].getBoundingClientRect();
                    if (ev.clientY < rect.top + rect.height / 2) {
                        before = others[i];
                        break;
                    }
                }
                if (row.nextElementSibling !== before) {
                    list.insertBefore(row, before);
                    moved = true;
                }
            }

            // On the document, not the handle: moving the row in the DOM drops pointer capture.
            function onUp() {
                document.removeEventListener('pointermove', onMove);
                document.removeEventListener('pointerup', onUp);
                document.removeEventListener('pointercancel', onUp);
                row.classList.remove('taste-section-dragging');
                if (moved) {
                    onChange();
                }
            }

            document.addEventListener('pointermove', onMove);
            document.addEventListener('pointerup', onUp);
            document.addEventListener('pointercancel', onUp);
        });

        return row;
    }

    function renderSections(view, status) {
        view.appendChild(el('h2', 'taste-row-title', 'Home screen sections'));
        var notice = el('div', 'taste-notice', 'Loading…');
        var list = el('div', 'taste-sections');
        var actions = el('div', 'taste-actions taste-sections-actions');
        view.appendChild(notice);
        view.appendChild(list);
        view.appendChild(actions);

        var api = client();
        var userId = api.getCurrentUserId();
        Promise.all([getDisplayPrefs(), api.getUser(userId), api.getUserViews({}, userId)]).then(function (loaded) {
            var prefs = loaded[0];
            var user = loaded[1];
            var views = loaded[2];
            notice.textContent = 'Drag sections into the order you want and switch off the ones you do not. '
                + 'This is saved to your account, so the order follows you to other devices.'
                + (status.HomeRows ? '' : ' Taste rows are switched off by your admin.');

            var save = button('Save', 'taste-primary', function () {
                save.disabled = true;
                var items = Array.prototype.map.call(list.children, function (node) { return node.tasteItem; });
                var libraries = [];
                items.forEach(function (item) {
                    libraries = libraries.concat(item.libraries || []);
                });
                saveLayout(items).then(function () {
                    return libraries.length ? saveLibraries(libraries) : null;
                }).then(dropWebCache).then(function () {
                    toast('Saved. Reloading your home screen…');
                    window.location.reload();
                }).catch(function () {
                    save.disabled = false;
                    toast('Could not save your sections');
                });
            }, 'check');
            save.disabled = true;

            function changed() {
                save.disabled = false;
            }

            function fill(layout, allLibrariesOn) {
                list.replaceChildren();
                layout.items.forEach(function (item) {
                    if (item.key === 'latestmedia') {
                        item.libraries = latestLibraries(views, allLibrariesOn ? { Configuration: {} } : user);
                    }
                    list.appendChild(sectionRow(item, list, changed));
                });
            }

            var reset = button('Reset to default', '', function () {
                fill(buildLayout({ CustomPrefs: {} }, status), true);
                changed();
            }, 'restart_alt');

            fill(buildLayout(prefs, status), false);
            actions.appendChild(reset);
            actions.appendChild(save);
        }).catch(function () {
            notice.textContent = 'Could not load your home screen settings.';
        });
    }

    function mount(root) {
        if (!root || root.getAttribute('data-taste-mounted')) {
            return;
        }
        root.setAttribute('data-taste-mounted', 'true');
        injectStyles();
        root.replaceChildren();
        var page = el('div', 'taste-page');
        page.appendChild(el('h1', null, 'Taste'));
        var tabs = el('div', 'taste-tabs');
        var view = el('div');
        page.appendChild(tabs);
        page.appendChild(view);
        root.appendChild(page);

        getStatus().then(function (status) {
            var sections = [
                ['Calibrate', function () { renderCalibrate(view); }],
                ['Profile', function () { renderProfile(view); }],
                ['Sections', function () { renderSections(view, status); }]
            ];
            if (!status.HomeRows) {
                sections.unshift(['For you', function () { renderForYou(view, status); }]);
            }
            var buttons = [];
            sections.forEach(function (section, index) {
                var b = button(section[0], '', function () {
                    buttons.forEach(function (other) { other.classList.remove('taste-on'); });
                    b.classList.add('taste-on');
                    view.replaceChildren();
                    section[1]();
                });
                buttons.push(b);
                tabs.appendChild(b);
                if (index === 0) {
                    b.classList.add('taste-on');
                }
            });
            sections[0][1]();
        }).catch(function () {
            view.appendChild(el('div', 'taste-notice', 'The Taste plugin is not responding.'));
        });
    }

    // ---------- home screen rows ----------

    var EAGER_IMAGES = 8;
    var canHover = window.matchMedia && window.matchMedia('(hover: hover)').matches;
    var home = { user: null, layoutRequest: null, slots: null, loadedAt: 0, visible: false, refreshing: false };

    function setBackground(node, url) {
        node.style.backgroundImage = 'url("' + String(url).replace(/"/g, '%22') + '")';
    }

    var lazyImages = 'IntersectionObserver' in window
        ? new IntersectionObserver(function (entries) {
            entries.forEach(function (entry) {
                if (entry.isIntersecting) {
                    setBackground(entry.target, entry.target.getAttribute('data-taste-src'));
                    lazyImages.unobserve(entry.target);
                }
            });
        }, { rootMargin: '300px' })
        : null;

    function lazyBackground(node, url, eager) {
        if (eager || !lazyImages) {
            setBackground(node, url);
        } else {
            node.setAttribute('data-taste-src', url);
            lazyImages.observe(node);
        }
    }

    function overlayButton(iconName, label, onClick) {
        var b = el('button', 'cardOverlayButton cardOverlayButton-hover paper-icon-button-light');
        b.type = 'button';
        b.title = label;
        b.setAttribute('aria-label', label);
        b.appendChild(el('span', 'material-icons cardOverlayButtonIcon cardOverlayButtonIcon-hover', iconName));
        b.addEventListener('click', function (e) {
            e.preventDefault();
            e.stopPropagation();
            onClick();
        });
        return b;
    }

    function homeCard(rec, status, eager) {
        var card = el('div', 'card overflowPortraitCard card-hoverable taste-home-card');
        card.title = rec.Reason || '';
        var box = el('div', 'cardBox cardBox-bottompadded');
        var scalable = el('div', 'cardScalable');
        scalable.appendChild(el('div', 'cardPadder cardPadder-overflowPortrait'));

        var image = el(rec.ItemId ? 'a' : 'div', 'cardImageContainer coveredImage cardContent');
        var url = rec.ItemId ? imageUrl(rec.ItemId, 450) : rec.ImageUrl;
        if (url) {
            lazyBackground(image, url, eager);
        } else {
            image.classList.add('taste-home-noimg');
            image.textContent = rec.Title;
        }
        if (rec.ItemId) {
            image.href = detailsHash(rec.ItemId);
        } else {
            image.addEventListener('click', function () { showTitleDetails(rec, status, card); });
        }
        scalable.appendChild(image);

        var badge = el('span', 'taste-badge');
        scalable.appendChild(badge);
        var buttons = el('div', 'cardOverlayButton-br flex');

        function paint() {
            var text = rec.Status === 'requested' ? 'Requested' : rec.Badge;
            badge.textContent = text || '';
            badge.style.display = text ? '' : 'none';
            buttons.replaceChildren();
            if (!rec.ItemId && status.Seerr && rec.TmdbId && rec.Status !== 'requested') {
                buttons.appendChild(overlayButton('add', 'Request', function () {
                    requestTitle(rec, paint);
                }));
            }
            buttons.appendChild(overlayButton('block', 'Not for me', function () {
                dismissTitle(rec, card, function () {});
            }));
        }
        card.tastePaint = paint;
        paint();

        if (canHover) {
            var overlay = el('div', 'cardOverlayContainer');
            overlay.appendChild(buttons);
            scalable.appendChild(overlay);
        }
        box.appendChild(scalable);

        var footer = el('div', 'cardFooter');
        var first = el('div', 'cardText cardTextCentered cardText-first');
        first.appendChild(el('bdi', null, rec.Title));
        footer.appendChild(first);
        var second = el('div', 'cardText cardTextCentered cardText-secondary');
        second.appendChild(el('bdi', null, rec.Year ? String(rec.Year) : '\u00a0'));
        footer.appendChild(second);
        box.appendChild(footer);
        card.appendChild(box);
        return card;
    }

    function showTitleDetails(rec, status, card) {
        injectStyles();
        closeDialog();
        var overlay = el('div', 'taste-overlay');
        overlay.setAttribute('role', 'dialog');
        overlay.setAttribute('aria-modal', 'true');
        overlay.setAttribute('aria-label', rec.Title);
        var dialog = el('div', 'taste-dialog taste-details');
        dialog.appendChild(poster(rec));
        var body = el('div', 'taste-details-body');
        body.appendChild(el('h2', null, rec.Title + (rec.Year ? ' (' + rec.Year + ')' : '')));
        var meta = [rec.Badge, rec.Meta].filter(Boolean).join(' · ');
        if (meta) {
            body.appendChild(el('div', 'taste-meta', meta));
        }
        if (rec.Overview) {
            body.appendChild(el('div', 'taste-overview', rec.Overview));
        }
        if (rec.Reason) {
            body.appendChild(el('div', 'taste-reason', rec.Reason));
        }

        function close() {
            overlay.remove();
            document.removeEventListener('keydown', onKey, true);
            if (card.tastePaint) {
                card.tastePaint();
            }
        }

        function onKey(e) {
            if (e.key === 'Escape') {
                e.preventDefault();
                e.stopPropagation();
                close();
            }
        }

        var actions = recommendationActions(rec, status, function () {
            close();
            dismissTitle(rec, card, function () {});
        });
        actions.appendChild(button('Close', '', close, 'close'));
        body.appendChild(actions);
        dialog.appendChild(body);
        overlay.appendChild(dialog);
        overlay.addEventListener('click', function (e) {
            if (e.target === overlay) {
                close();
            }
        });
        document.addEventListener('keydown', onKey, true);
        document.body.appendChild(overlay);
    }

    function homeSection(row, items, status) {
        var section = el('div', 'verticalSection taste-home-section');
        section.setAttribute('data-taste-shelf', row.shelf);
        section.innerHTML = '<div class="sectionTitleContainer sectionTitleContainer-cards padded-left">'
            + '<h2 class="sectionTitle sectionTitle-cards"></h2></div>'
            + '<div is="emby-scroller" class="padded-top-focusscale padded-bottom-focusscale" data-centerfocus="true">'
            + '<div class="itemsContainer scrollSlider focuscontainer-x"></div></div>';
        section.querySelector('.sectionTitle').textContent = row.title;
        var slider = section.querySelector('.scrollSlider');
        // Jellyfin's home tab calls pause()/resume() on every .itemsContainer when you leave or return.
        slider.pause = function () {};
        slider.resume = function () { return Promise.resolve(); };
        items.forEach(function (rec, index) {
            slider.appendChild(homeCard(rec, status, index < EAGER_IMAGES));
        });
        return section;
    }

    function checkScrollers(block) {
        setTimeout(function () {
            block.querySelectorAll('[is="emby-scroller"]').forEach(function (scroller) {
                if (scroller.isConnected && !scroller.classList.contains('emby-scroller')) {
                    scroller.classList.add('taste-scroll-fallback');
                }
            });
        }, 1500);
    }

    // One slot per switched-on Taste row, keyed like the layout ("taste:movies").
    function loadHomeRows(layout, status, done) {
        var slots = {};
        var rows = availableShelves(status).filter(function (row) {
            return layout.items.some(function (item) { return item.key === tasteKey(row) && item.on; });
        });
        var pending = rows.length;
        if (!pending && done) {
            done();
        }
        rows.forEach(function (row) {
            var slot = el('div', 'taste-home-slot');
            slots[tasteKey(row)] = slot;
            getJson('Taste/Recommendations', { shelf: row.shelf }).then(function (result) {
                if (result.Items && result.Items.length) {
                    slot.replaceChildren(homeSection(row, result.Items, status));
                    checkScrollers(slot);
                }
            }).catch(function (err) {
                console.debug('Taste: home row ' + row.shelf + ' failed', err);
            }).then(function () {
                pending--;
                if (pending === 0 && done) {
                    done();
                }
            });
        });
        return slots;
    }

    function visibleHomeContainer() {
        var all = document.querySelectorAll('.homeSectionsContainer');
        for (var i = 0; i < all.length; i++) {
            if (all[i].offsetParent !== null) {
                return all[i];
            }
        }
        return null;
    }

    // Jellyfin renders homesection N into ".sectionN" in slot order; each Taste row goes right after the
    // section (or Taste row) before it in the user's layout.
    function placeHome(container, layout, slots) {
        var offset = 0;
        // The TV layout forces My media in front when the user has switched it off.
        if (document.documentElement.classList.contains('layout-tv')
            && layout.slots.indexOf('smalllibrarytiles') < 0 && layout.slots.indexOf('librarybuttons') < 0) {
            offset = 1;
        }
        var prev = offset ? container.querySelector(':scope > .section0') : null;
        layout.items.forEach(function (item) {
            if (!item.on) {
                return;
            }
            if (!item.taste) {
                var index = layout.slots.indexOf(item.key);
                var section = index >= 0 ? container.querySelector(':scope > .section' + (index + offset)) : null;
                if (section) {
                    prev = section;
                }
                return;
            }
            var slot = slots[item.key];
            if (!slot) {
                return;
            }
            var target = prev ? prev.nextSibling : container.firstChild;
            if (target !== slot) {
                container.insertBefore(slot, target);
                checkScrollers(slot);
            }
            prev = slot;
        });
    }

    function syncHome() {
        var api = client();
        var userId = api && api.getCurrentUserId ? api.getCurrentUserId() : null;
        var container = visibleHomeContainer();
        var cameBack = !!container && !home.visible;
        home.visible = !!container;
        if (!container || !userId) {
            return;
        }
        if (home.user !== userId) {
            home = { user: userId, layoutRequest: null, slots: null, loadedAt: 0, visible: true, refreshing: false };
        }
        // Re-read the layout each time Home is opened, in case Jellyfin's own Home screen settings changed it.
        if (cameBack || !home.layoutRequest) {
            home.layoutRequest = Promise.all([
                getStatus(),
                getDisplayPrefs().catch(function () { return { CustomPrefs: {} }; })
            ]).then(function (results) {
                return { status: results[0], layout: buildLayout(results[1], results[0]) };
            });
        }
        var state = home;
        state.layoutRequest.then(function (loaded) {
            var target = visibleHomeContainer();
            if (home !== state || !target) {
                return;
            }
            injectStyles();
            if (!state.slots) {
                state.loadedAt = Date.now();
                state.slots = loadHomeRows(loaded.layout, loaded.status);
            } else if (cameBack && !state.refreshing && Date.now() - state.loadedAt > HOME_REFRESH_MS) {
                state.refreshing = true;
                var fresh = loadHomeRows(loaded.layout, loaded.status, function () {
                    state.refreshing = false;
                    state.loadedAt = Date.now();
                    Object.keys(state.slots).forEach(function (key) {
                        var old = state.slots[key];
                        if (fresh[key] && old.parentNode) {
                            old.replaceWith(fresh[key]);
                            checkScrollers(fresh[key]);
                        } else {
                            old.remove();
                        }
                    });
                    state.slots = fresh;
                });
            }
            placeHome(target, loaded.layout, state.slots);
        }).catch(function (err) {
            if (home === state) {
                state.layoutRequest = null;
            }
            console.debug('Taste: home rows failed', err);
        });
    }

    function closePanel() {
        var panel = document.querySelector('.taste-panel');
        if (panel) {
            panel.remove();
        }
    }

    function openPanel() {
        injectStyles();
        closePanel();
        var panel = el('div', 'taste-panel');
        var close = button('Close', 'taste-panel-close', closePanel, 'close');
        panel.appendChild(close);
        var root = el('div');
        panel.appendChild(root);
        document.body.appendChild(panel);
        mount(root);
    }

    function headerButton(className) {
        var b = el('button', className + ' taste-header-btn');
        b.type = 'button';
        b.title = 'Taste';
        b.setAttribute('aria-label', 'Taste recommendations');
        b.appendChild(icon('thumbs_up_down'));
        b.addEventListener('click', openPanel);
        return b;
    }

    function addHeaderButton() {
        // Default layout: the React app bar. Appended after Search so React's own child order is untouched.
        var search = document.querySelector('header.MuiAppBar-root a[href="#/search"]');
        if (search && search.parentElement && !search.parentElement.querySelector('.taste-header-btn')) {
            search.parentElement.appendChild(headerButton(search.className));
        }

        // TV and legacy layouts.
        var legacy = document.querySelector('.skinHeader .headerRight');
        if (legacy && !legacy.querySelector('.taste-header-btn')) {
            legacy.insertBefore(headerButton('headerButton headerButtonRight paper-icon-button-light'), legacy.firstChild);
        }
    }

    var domTimer = null;
    new MutationObserver(function () {
        if (domTimer) {
            return;
        }
        domTimer = setTimeout(function () {
            domTimer = null;
            addHeaderButton();
            syncHome();
        }, 300);
    }).observe(document.body, { childList: true, subtree: true });
    addHeaderButton();
    syncHome();

    window.TasteUI = { mount: mount, open: openPanel, showDialog: showDialog };
})();
