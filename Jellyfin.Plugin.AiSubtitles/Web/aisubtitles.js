/*
 * AI Subtitle Translator – player integration for jellyfin-web.
 * Adds a "translate" button to the video OSD, lets the viewer pick a source subtitle
 * and a target language, and renders the translation live while it is being produced.
 */
(function () {
    'use strict';

    if (window.__aiSubtitles) {
        return;
    }
    window.__aiSubtitles = { version: '1.0.0' };

    var LOG = '[KI-Untertitel]';
    var PREFS_KEY = 'aiSubtitles.prefs.v1';
    var LOOKAHEAD_SECONDS = 150;   // translate this far ahead of the playhead
    var BEHIND_SECONDS = 5;
    var MAX_CLIENT_REQUESTS = 2;   // parallel batch requests from this player
    var RETRY_AFTER_MS = 15000;

    // ---------------------------------------------------------------- state

    var prefs = loadPrefs();          // { active, lang, streamByLang }
    var serverConfig = null;          // from /AiSubtitles/ClientConfig
    var playback = {
        api: null,
        itemId: null,
        mediaSourceId: null,
        subtitleStreamIndex: null,
        offset: 0,                    // media time = video.currentTime + offset (transcoding)
        offsetValid: false
    };
    var streams = null;               // translatable streams of the current item
    var session = null;               // active translation
    var inflight = 0;
    var ui = { overlay: null, text: null, status: null, menu: null, lastText: null };

    function loadPrefs() {
        try {
            var p = JSON.parse(localStorage.getItem(PREFS_KEY) || '{}');
            return { active: !!p.active, lang: p.lang || null, streamByLang: p.streamByLang || {} };
        } catch (e) {
            return { active: false, lang: null, streamByLang: {} };
        }
    }

    function savePrefs() {
        try {
            localStorage.setItem(PREFS_KEY, JSON.stringify(prefs));
        } catch (e) {
            // storage not available – preferences just won't persist
        }
    }

    // ---------------------------------------------------------------- server

    function api() {
        return playback.api || window.ApiClient;
    }

    function getJson(path, params) {
        var a = api();
        return a.getJSON(a.getUrl(path, params));
    }

    function postJson(path, params) {
        var a = api();
        return a.ajax({ type: 'POST', url: a.getUrl(path, params), dataType: 'json' });
    }

    function errorText(err) {
        if (!err) {
            return 'Unbekannter Fehler';
        }
        if (typeof err === 'string') {
            return err;
        }
        if (err.__aiMessage) {
            return err.__aiMessage;
        }
        if (err.status) {
            return 'HTTP ' + err.status + (err.statusText ? ' ' + err.statusText : '');
        }
        return err.message || String(err);
    }

    // jellyfin-apiclient rejects with the Response object; read our {error} body from it.
    function enrichError(err) {
        if (err && typeof err.json === 'function' && !err.bodyUsed) {
            return err.json().then(function (body) {
                err.__aiMessage = body && body.error ? body.error : null;
                throw err;
            }, function () {
                throw err;
            });
        }
        return Promise.reject(err);
    }

    function loadServerConfig() {
        if (serverConfig) {
            return Promise.resolve(serverConfig);
        }
        return getJson('AiSubtitles/ClientConfig').then(function (cfg) {
            serverConfig = cfg;
            return cfg;
        });
    }

    // ---------------------------------------------------------------- playback tracking

    // jellyfin-web reports the playback position through ApiClient on every timeupdate.
    // Hooking those calls gives us the item, media source and the exact media position
    // (including the transcoding offset) without depending on internal modules.
    function patchApiClient() {
        var ac = window.ApiClient;
        if (!ac) {
            setTimeout(patchApiClient, 500);
            return;
        }
        var proto = Object.getPrototypeOf(ac);
        if (proto.__aiSubsPatched) {
            return;
        }
        proto.__aiSubsPatched = true;
        wrap(proto, 'reportPlaybackStart', 'start');
        wrap(proto, 'reportPlaybackProgress', 'progress');
        wrap(proto, 'reportPlaybackStopped', 'stopped');
    }

    function wrap(target, name, kind) {
        var original = target[name];
        if (typeof original !== 'function') {
            console.warn(LOG, name + ' nicht gefunden – Player-Integration eingeschränkt');
            return;
        }
        target[name] = function (info) {
            try {
                onReport(kind, info || {}, this);
            } catch (e) {
                console.warn(LOG, e);
            }
            return original.apply(this, arguments);
        };
    }

    function getVideo() {
        return document.querySelector('.videoPlayerContainer video') || document.querySelector('video.htmlvideoplayer');
    }

    function onReport(kind, info, apiClient) {
        if (kind === 'stopped') {
            onStopped();
            return;
        }
        var video = getVideo();
        if (!video || !info.ItemId) {
            return; // audio playback or external player
        }
        playback.api = apiClient;
        var mediaSourceId = info.MediaSourceId || info.ItemId;
        if (info.ItemId !== playback.itemId || mediaSourceId !== playback.mediaSourceId) {
            playback.itemId = info.ItemId;
            playback.mediaSourceId = mediaSourceId;
            onItemChanged();
        }
        playback.subtitleStreamIndex = info.SubtitleStreamIndex;
        if (typeof info.PositionTicks === 'number') {
            playback.offset = info.PositionTicks / 1e7 - video.currentTime;
            playback.offsetValid = true;
        }
    }

    function currentPosition() {
        var video = getVideo();
        if (!video || !playback.offsetValid) {
            return null;
        }
        return video.currentTime + playback.offset;
    }

    function onItemChanged() {
        stopSession(false);
        streams = null;
        closeMenu();
        if (prefs.active && prefs.lang) {
            // Wait a moment for the player to settle, then continue with the same language.
            setTimeout(function () {
                if (prefs.active && !session) {
                    startTranslation(prefs.lang, null, true);
                }
            }, 1500);
        }
    }

    function onStopped() {
        stopSession(false);
        closeMenu();
        playback.itemId = null;
        playback.mediaSourceId = null;
        playback.offsetValid = false;
        streams = null;
    }

    function watchVideoElement() {
        var video = getVideo();
        if (video && !video.__aiSubsWatched) {
            video.__aiSubsWatched = true;
            // A new source (e.g. transcode restarted after a seek) invalidates the offset until the next report.
            video.addEventListener('loadstart', function () {
                playback.offsetValid = false;
            });
            video.addEventListener('timeupdate', render);
            video.addEventListener('seeked', function () {
                render();
                pump();
            });
        }
    }

    // ---------------------------------------------------------------- streams

    function loadStreams() {
        if (streams && streams.itemId === playback.itemId) {
            return Promise.resolve(streams);
        }
        var itemId = playback.itemId;
        return getJson('AiSubtitles/Streams/' + itemId, { mediaSourceId: playback.mediaSourceId })
            .catch(enrichError)
            .then(function (res) {
                streams = { itemId: itemId, mediaSourceId: res.mediaSourceId, list: res.streams || [] };
                return streams;
            });
    }

    function languageCodes(lang) {
        var map = {
            de: ['de', 'ger', 'deu'], en: ['en', 'eng'], fr: ['fr', 'fre', 'fra'], es: ['es', 'spa'], it: ['it', 'ita'],
            nl: ['nl', 'dut', 'nld'], pl: ['pl', 'pol'], pt: ['pt', 'por'], tr: ['tr', 'tur'], ru: ['ru', 'rus'],
            uk: ['uk', 'ukr'], ja: ['ja', 'jpn'], ko: ['ko', 'kor'], zh: ['zh', 'chi', 'zho'], ar: ['ar', 'ara']
        };
        var key = String(lang || '').toLowerCase().split('-')[0];
        return map[key] || [key];
    }

    function pickStream(list, lang) {
        if (!list.length) {
            return null;
        }
        var byIndex = function (index) {
            return list.filter(function (s) { return s.index === index; })[0] || null;
        };
        var remembered = prefs.streamByLang[playback.itemId + '|' + lang];
        if (remembered != null && byIndex(remembered)) {
            return byIndex(remembered);
        }
        var targetCodes = languageCodes(lang);
        var notTarget = list.filter(function (s) {
            return targetCodes.indexOf(String(s.language || '').toLowerCase()) < 0;
        });
        var pool = notTarget.length ? notTarget : list;
        var selected = playback.subtitleStreamIndex != null ? byIndex(playback.subtitleStreamIndex) : null;
        if (selected && pool.indexOf(selected) >= 0) {
            return selected;
        }
        var full = pool.filter(function (s) { return !s.isForced; });
        var candidates = full.length ? full : pool;
        var english = candidates.filter(function (s) {
            return ['en', 'eng'].indexOf(String(s.language || '').toLowerCase()) >= 0;
        })[0];
        return english || candidates[0];
    }

    // ---------------------------------------------------------------- translation session

    function startTranslation(lang, streamIndex, automatic) {
        if (!playback.itemId) {
            return Promise.resolve();
        }
        var itemId = playback.itemId;
        return loadServerConfig().then(function (cfg) {
            if (!cfg.enabled) {
                return;
            }
            return loadStreams().then(function (s) {
                if (itemId !== playback.itemId) {
                    return;
                }
                var stream = streamIndex != null
                    ? s.list.filter(function (x) { return x.index === streamIndex; })[0]
                    : pickStream(s.list, lang);
                if (!stream) {
                    if (!automatic) {
                        showToast('Für diesen Titel gibt es keine Text-Untertitel, die übersetzt werden können.');
                    }
                    return;
                }
                prefs.active = true;
                prefs.lang = lang;
                prefs.streamByLang[itemId + '|' + lang] = stream.index;
                savePrefs();
                stopSession(false);
                session = {
                    id: Math.random().toString(36).slice(2),
                    itemId: itemId,
                    mediaSourceId: s.mediaSourceId,
                    streamIndex: stream.index,
                    streamTitle: stream.title,
                    lang: lang,
                    langName: languageName(lang),
                    plan: null,
                    state: {},
                    cues: [],
                    error: null
                };
                document.documentElement.classList.add('aiSubs-active');
                setStatus('Lade Untertitel …');
                var current = session;
                return getJson('AiSubtitles/Plan/' + itemId, {
                    mediaSourceId: current.mediaSourceId,
                    streamIndex: current.streamIndex
                }).catch(enrichError).then(function (plan) {
                    if (session !== current) {
                        return;
                    }
                    current.plan = plan;
                    setStatus('');
                    pump();
                    render();
                    refreshMenu();
                });
            });
        }).catch(function (err) {
            console.error(LOG, err);
            if (session) {
                session.error = errorText(err);
            }
            setStatus('KI-Untertitel: ' + errorText(err), true);
            refreshMenu();
        });
    }

    function stopSession(userAction) {
        session = null;
        inflight = 0;
        document.documentElement.classList.remove('aiSubs-active');
        if (ui.overlay) {
            ui.overlay.style.display = 'none';
        }
        ui.lastText = null;
        if (userAction) {
            prefs.active = false;
            savePrefs();
        }
    }

    // Requests the batches around and ahead of the playhead.
    function pump() {
        var current = session;
        if (!current || !current.plan) {
            return;
        }
        var pos = currentPosition();
        if (pos == null) {
            return;
        }
        var batches = current.plan.batches;
        var now = Date.now();
        for (var i = 0; i < batches.length && inflight < MAX_CLIENT_REQUESTS; i++) {
            var b = batches[i];
            if (b.end < pos - BEHIND_SECONDS) {
                continue;
            }
            if (b.start > pos + LOOKAHEAD_SECONDS) {
                break;
            }
            var st = current.state[b.index];
            if (st && (st.status === 'done' || st.status === 'loading')) {
                continue;
            }
            if (st && st.status === 'error' && now - st.at < RETRY_AFTER_MS) {
                continue;
            }
            fetchBatch(current, b);
        }
    }

    function fetchBatch(current, batch) {
        current.state[batch.index] = { status: 'loading', at: Date.now() };
        inflight++;
        getJson('AiSubtitles/Batch/' + current.itemId, {
            mediaSourceId: current.mediaSourceId,
            streamIndex: current.streamIndex,
            lang: current.lang,
            batch: batch.index,
            batchSize: current.plan.batchSize
        }).catch(enrichError).then(function (res) {
            if (session !== current) {
                return;
            }
            current.state[batch.index] = { status: 'done', at: Date.now() };
            current.error = null;
            Array.prototype.push.apply(current.cues, res.cues || []);
            current.cues.sort(function (a, b) { return a.s - b.s; });
            updateMenuStatus();
        }, function (err) {
            if (session !== current) {
                return;
            }
            current.state[batch.index] = { status: 'error', at: Date.now() };
            current.error = errorText(err);
            console.warn(LOG, 'Batch ' + batch.index + ' fehlgeschlagen:', current.error);
            updateMenuStatus();
        }).then(function () {
            if (session === current) {
                inflight = Math.max(0, inflight - 1);
                pump();
                render();
            }
        });
    }

    // ---------------------------------------------------------------- rendering

    function ensureOverlay() {
        var container = document.querySelector('.videoPlayerContainer');
        if (!container) {
            return false;
        }
        if (!ui.overlay || !container.contains(ui.overlay)) {
            ui.overlay = document.createElement('div');
            ui.overlay.className = 'aiSubsOverlay';
            ui.text = document.createElement('div');
            ui.text.className = 'aiSubsText';
            ui.text.setAttribute('dir', 'auto');
            ui.status = document.createElement('div');
            ui.status.className = 'aiSubsStatus';
            ui.overlay.appendChild(ui.status);
            ui.overlay.appendChild(ui.text);
            container.appendChild(ui.overlay);
            ui.lastText = null;
        }
        return true;
    }

    function setStatus(text, isError) {
        if (!ensureOverlay()) {
            return;
        }
        ui.overlay.style.display = '';
        ui.status.textContent = text || '';
        ui.status.style.display = text ? '' : 'none';
        ui.status.classList.toggle('aiSubsStatus-error', !!isError);
    }

    function render() {
        var current = session;
        if (!current) {
            return;
        }
        if (!ensureOverlay()) {
            return;
        }
        ui.overlay.style.display = '';
        var pos = currentPosition();
        if (pos == null || !current.plan) {
            return;
        }

        var text = current.cues
            .filter(function (c) { return c.s <= pos && c.e >= pos; })
            .map(function (c) { return c.t; })
            .join('\n');
        if (text !== ui.lastText) {
            ui.lastText = text;
            ui.text.textContent = text;
            ui.text.style.display = text ? '' : 'none';
        }

        // Status: is the part we are watching still being translated?
        var batch = null;
        var batches = current.plan.batches;
        for (var i = 0; i < batches.length; i++) {
            if (batches[i].start <= pos + 1 && batches[i].end >= pos) {
                batch = batches[i];
                break;
            }
        }
        var st = batch ? current.state[batch.index] : null;
        if (st && st.status === 'error' && current.error) {
            setStatus('KI-Übersetzung fehlgeschlagen: ' + current.error, true);
        } else if (batch && (!st || st.status === 'loading')) {
            setStatus('KI übersetzt …');
        } else {
            setStatus('');
        }
    }

    function tick() {
        watchVideoElement();
        injectButton();
        if (session) {
            pump();
            render();
        }
    }

    // ---------------------------------------------------------------- OSD button + menu

    function injectButton() {
        var subtitleButton = document.querySelector('.videoOsdBottom .btnSubtitles');
        if (!subtitleButton || subtitleButton.parentNode.querySelector('.btnAiSubtitles')) {
            return;
        }
        var btn = document.createElement('button');
        btn.setAttribute('is', 'paper-icon-button-light');
        btn.className = 'btnAiSubtitles autoSize paper-icon-button-light';
        btn.title = 'KI-Untertitel';
        btn.setAttribute('aria-label', 'KI-Untertitel');
        btn.innerHTML = '<span class="xlargePaperIconButton material-icons translate" aria-hidden="true"></span>';
        btn.addEventListener('click', function (e) {
            e.preventDefault();
            e.stopPropagation();
            toggleMenu(btn);
        });
        subtitleButton.parentNode.insertBefore(btn, subtitleButton.nextSibling);
    }

    function toggleMenu(anchor) {
        if (ui.menu) {
            closeMenu();
            return;
        }
        openMenu(anchor);
    }

    function closeMenu() {
        if (ui.menu) {
            ui.menu.remove();
            ui.menu = null;
        }
        document.removeEventListener('pointerdown', onOutsidePointer, true);
    }

    function onOutsidePointer(e) {
        if (ui.menu && !ui.menu.contains(e.target) && !e.target.closest('.btnAiSubtitles')) {
            closeMenu();
        }
    }

    function openMenu(anchor) {
        var menu = document.createElement('div');
        menu.className = 'aiSubsMenu';
        ['click', 'pointerdown', 'mousedown', 'touchstart', 'keydown', 'keyup', 'wheel'].forEach(function (type) {
            menu.addEventListener(type, function (e) { e.stopPropagation(); });
        });
        document.body.appendChild(menu);
        ui.menu = menu;
        ui.menuAnchor = anchor;
        positionMenu();
        menu.innerHTML = '<div class="aiSubsMenu-title">KI-Untertitel</div><div class="aiSubsMenu-hint">Lade …</div>';
        setTimeout(function () {
            document.addEventListener('pointerdown', onOutsidePointer, true);
        }, 0);

        Promise.all([loadServerConfig(), playback.itemId ? loadStreams() : Promise.resolve(null)])
            .then(function () {
                refreshMenu();
            })
            .catch(function (err) {
                menu.innerHTML = '<div class="aiSubsMenu-title">KI-Untertitel</div>';
                var p = document.createElement('div');
                p.className = 'aiSubsMenu-error';
                p.textContent = errorText(err);
                menu.appendChild(p);
            });
    }

    function positionMenu() {
        if (!ui.menu || !ui.menuAnchor) {
            return;
        }
        var r = ui.menuAnchor.getBoundingClientRect();
        var right = Math.max(8, window.innerWidth - r.right - 40);
        ui.menu.style.right = right + 'px';
        ui.menu.style.bottom = Math.max(8, window.innerHeight - r.top + 8) + 'px';
    }

    function languageName(code) {
        var list = (serverConfig && serverConfig.languages) || [];
        for (var i = 0; i < list.length; i++) {
            if (list[i].code.toLowerCase() === String(code).toLowerCase()) {
                return list[i].name;
            }
        }
        return code;
    }

    function el(tag, className, text) {
        var e = document.createElement(tag);
        if (className) {
            e.className = className;
        }
        if (text != null) {
            e.textContent = text;
        }
        return e;
    }

    function progressText(current) {
        if (!current || !current.plan) {
            return '';
        }
        var total = current.plan.batches.length;
        var done = 0;
        Object.keys(current.state).forEach(function (k) {
            if (current.state[k].status === 'done') {
                done++;
            }
        });
        return done + ' von ' + total + ' Abschnitten übersetzt';
    }

    function refreshMenu() {
        var menu = ui.menu;
        if (!menu || !serverConfig) {
            return;
        }
        menu.innerHTML = '';
        menu.appendChild(el('div', 'aiSubsMenu-title', 'KI-Untertitel'));

        if (!serverConfig.configured) {
            menu.appendChild(el('div', 'aiSubsMenu-error', 'Im Plugin ist noch kein KI-Endpunkt eingerichtet (Dashboard → Plugins → AI Subtitle Translator).'));
        }

        if (!streams || !streams.list.length) {
            menu.appendChild(el('div', 'aiSubsMenu-hint', 'Keine übersetzbaren Text-Untertitel vorhanden (Bild-Untertitel wie PGS/VobSub gehen nicht).'));
            return;
        }

        // Off
        var off = el('button', 'aiSubsMenu-item' + (!session ? ' selected' : ''), 'Aus');
        off.addEventListener('click', function () {
            stopSession(true);
            refreshMenu();
        });
        menu.appendChild(off);

        // Source stream
        if (streams.list.length > 1) {
            menu.appendChild(el('div', 'aiSubsMenu-section', 'Quelle'));
            var select = el('select', 'aiSubsMenu-select');
            var preferred = session ? session.streamIndex : (pickStream(streams.list, prefs.lang || serverConfig.defaultLanguage) || {}).index;
            streams.list.forEach(function (s) {
                var o = el('option', null, s.title + (s.isExternal ? ' (extern)' : ''));
                o.value = String(s.index);
                if (s.index === preferred) {
                    o.selected = true;
                }
                select.appendChild(o);
            });
            select.addEventListener('change', function () {
                var lang = session ? session.lang : (prefs.lang || serverConfig.defaultLanguage);
                startTranslation(lang, parseInt(select.value, 10), false);
            });
            menu.appendChild(select);
        }

        // Target languages
        menu.appendChild(el('div', 'aiSubsMenu-section', 'Übersetzen nach'));
        var grid = el('div', 'aiSubsMenu-grid');
        serverConfig.languages.forEach(function (l) {
            var active = session && session.lang.toLowerCase() === l.code.toLowerCase();
            var b = el('button', 'aiSubsMenu-item' + (active ? ' selected' : ''), l.name);
            b.addEventListener('click', function () {
                startTranslation(l.code, selectedStreamIndex(menu), false);
            });
            grid.appendChild(b);
        });
        menu.appendChild(grid);

        // Free language input
        var row = el('div', 'aiSubsMenu-row');
        var input = el('input', 'aiSubsMenu-input');
        input.type = 'text';
        input.placeholder = 'Andere Sprache, z. B. „Schweizerdeutsch“';
        var go = el('button', 'aiSubsMenu-go', 'OK');
        var submit = function () {
            var v = input.value.trim();
            if (v) {
                startTranslation(v, selectedStreamIndex(menu), false);
            }
        };
        go.addEventListener('click', submit);
        input.addEventListener('keydown', function (e) {
            if (e.key === 'Enter') {
                submit();
            }
        });
        row.appendChild(input);
        row.appendChild(go);
        menu.appendChild(row);

        // Status (updated in place while batches arrive, so typing in the input is not interrupted)
        menu.appendChild(el('div', 'aiSubsMenu-status'));
        updateMenuStatus();

        // Save as subtitle / download
        if (session) {
            var actions = el('div', 'aiSubsMenu-row');
            if (serverConfig.canSave) {
                var save = el('button', 'aiSubsMenu-item', 'Als Untertitel speichern');
                save.title = 'Übersetzt den kompletten Untertitel und legt ihn dauerhaft am Titel ab.';
                save.addEventListener('click', function () {
                    saveTranslation(save);
                });
                actions.appendChild(save);
            }
            var dl = el('button', 'aiSubsMenu-item', 'SRT herunterladen');
            dl.addEventListener('click', function () {
                downloadTranslation(dl);
            });
            actions.appendChild(dl);
            menu.appendChild(actions);
        }
        positionMenu();
    }

    function updateMenuStatus() {
        var box = ui.menu && ui.menu.querySelector('.aiSubsMenu-status');
        if (!box) {
            return;
        }
        box.innerHTML = '';
        if (session) {
            box.appendChild(el('div', 'aiSubsMenu-hint', session.langName + ' aus „' + session.streamTitle + '“ · ' + progressText(session)));
            if (session.error) {
                box.appendChild(el('div', 'aiSubsMenu-error', session.error));
            }
        }
    }

    function selectedStreamIndex(menu) {
        var select = menu.querySelector('.aiSubsMenu-select');
        if (select) {
            return parseInt(select.value, 10);
        }
        return session ? session.streamIndex : null;
    }

    function saveTranslation(button) {
        var current = session;
        if (!current) {
            return;
        }
        button.disabled = true;
        button.textContent = 'Übersetze komplett …';
        postJson('AiSubtitles/Save/' + current.itemId, {
            mediaSourceId: current.mediaSourceId,
            streamIndex: current.streamIndex,
            lang: current.lang
        }).catch(enrichError).then(function (res) {
            showToast(res && res.message ? res.message : 'Gespeichert.');
            button.textContent = 'Gespeichert ✓';
        }, function (err) {
            showToast('Speichern fehlgeschlagen: ' + errorText(err));
            button.disabled = false;
            button.textContent = 'Als Untertitel speichern';
        });
    }

    function downloadTranslation(button) {
        var current = session;
        if (!current) {
            return;
        }
        var a = api();
        button.disabled = true;
        button.textContent = 'Übersetze komplett …';
        a.ajax({
            type: 'GET',
            url: a.getUrl('AiSubtitles/File/' + current.itemId, {
                mediaSourceId: current.mediaSourceId,
                streamIndex: current.streamIndex,
                lang: current.lang,
                format: 'srt'
            }),
            dataType: 'text'
        }).catch(enrichError).then(function (text) {
            var blob = new Blob([text], { type: 'application/x-subrip' });
            var link = document.createElement('a');
            link.href = URL.createObjectURL(blob);
            link.download = 'untertitel.' + String(current.lang).replace(/[^a-z0-9-]+/gi, '_') + '.ai.srt';
            document.body.appendChild(link);
            link.click();
            setTimeout(function () {
                URL.revokeObjectURL(link.href);
                link.remove();
            }, 1000);
            button.disabled = false;
            button.textContent = 'SRT herunterladen';
        }, function (err) {
            showToast('Download fehlgeschlagen: ' + errorText(err));
            button.disabled = false;
            button.textContent = 'SRT herunterladen';
        });
    }

    function showToast(text) {
        var t = el('div', 'aiSubsToast', text);
        document.body.appendChild(t);
        setTimeout(function () {
            t.classList.add('aiSubsToast-hide');
        }, 4000);
        setTimeout(function () {
            t.remove();
        }, 4600);
    }

    // ---------------------------------------------------------------- styles

    function injectStyles() {
        var css = [
            '.aiSubs-active .videoSubtitles, .aiSubs-active .libassjs-canvas-parent, .aiSubs-active .JASSUB { display: none !important; }',
            '.aiSubs-active video::cue { opacity: 0; background: transparent; }',
            '.aiSubsOverlay { position: absolute; left: 5%; right: 5%; bottom: 9%; z-index: 2; pointer-events: none; text-align: center; display: flex; flex-direction: column; align-items: center; gap: .4em; }',
            '.aiSubsText { white-space: pre-line; color: #fff; font-size: clamp(18px, 4.6vh, 56px); line-height: 1.25; font-weight: 500;',
            '  text-shadow: 0 0 4px #000, 0 0 3px #000, 1px 1px 2px #000, -1px -1px 2px #000; padding: .05em .3em; }',
            '.aiSubsStatus { color: #ddd; font-size: clamp(11px, 1.6vh, 16px); background: rgba(0,0,0,.55); border-radius: 1em; padding: .25em .9em; }',
            '.aiSubsStatus-error { color: #ffb4a9; }',
            '.aiSubsMenu { position: fixed; z-index: 100000; width: min(360px, calc(100vw - 16px)); max-height: 70vh; overflow-y: auto; background: rgba(28,28,30,.97);',
            '  color: #fff; border-radius: 12px; padding: 12px; box-shadow: 0 8px 32px rgba(0,0,0,.5); font-size: 14px; box-sizing: border-box; }',
            '.aiSubsMenu-title { font-size: 16px; font-weight: 600; margin-bottom: 8px; }',
            '.aiSubsMenu-section { margin: 12px 0 6px; font-size: 12px; text-transform: uppercase; letter-spacing: .05em; opacity: .65; }',
            '.aiSubsMenu-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 6px; }',
            '.aiSubsMenu-row { display: flex; gap: 6px; margin-top: 8px; flex-wrap: wrap; }',
            '.aiSubsMenu-item, .aiSubsMenu-go { background: rgba(255,255,255,.08); color: inherit; border: 1px solid transparent; border-radius: 8px; padding: 8px 10px;',
            '  font: inherit; cursor: pointer; text-align: left; }',
            '.aiSubsMenu > .aiSubsMenu-item { width: 100%; }',
            '.aiSubsMenu-item:hover, .aiSubsMenu-go:hover, .aiSubsMenu-item:focus-visible { background: rgba(255,255,255,.16); outline: none; }',
            '.aiSubsMenu-item.selected { border-color: #00a4dc; background: rgba(0,164,220,.2); }',
            '.aiSubsMenu-item:disabled { opacity: .6; cursor: progress; }',
            '.aiSubsMenu-select, .aiSubsMenu-input { width: 100%; box-sizing: border-box; background: rgba(255,255,255,.08); color: inherit; border: 1px solid rgba(255,255,255,.15);',
            '  border-radius: 8px; padding: 8px; font: inherit; }',
            '.aiSubsMenu-select option { background: #222; color: #fff; }',
            '.aiSubsMenu-row .aiSubsMenu-input { flex: 1; width: auto; min-width: 0; }',
            '.aiSubsMenu-hint { margin-top: 10px; opacity: .75; font-size: 13px; }',
            '.aiSubsMenu-error { margin-top: 10px; color: #ffb4a9; font-size: 13px; }',
            '.aiSubsToast { position: fixed; left: 50%; bottom: 12%; transform: translateX(-50%); z-index: 100001; background: rgba(28,28,30,.95); color: #fff;',
            '  padding: 10px 16px; border-radius: 8px; font-size: 14px; max-width: 90vw; transition: opacity .5s; }',
            '.aiSubsToast-hide { opacity: 0; }'
        ].join('\n');
        var style = document.createElement('style');
        style.setAttribute('data-ai-subtitles', 'styles');
        style.textContent = css;
        document.head.appendChild(style);
    }

    // ---------------------------------------------------------------- start

    function init() {
        injectStyles();
        patchApiClient();
        setInterval(tick, 250);
        window.addEventListener('resize', positionMenu);
        console.info(LOG, 'Player-Integration geladen');
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
