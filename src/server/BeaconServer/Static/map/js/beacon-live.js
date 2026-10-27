// beacon-live.js
// Beacon live-player overlay for the Subnautica 2 map (joric's map base).
// Polls the connected Beacon server's GET /api/v1/map/state and renders each
// online player as a live dot, updating in place every poll. This is a Beacon
// addition layered on top of joric's map; joric's own code is unchanged.
//
// The launcher opens the map with ?beacon=http://<server-ip>:<http-port> so the
// overlay knows which server to read. Without that param the overlay stays off
// and the map behaves exactly like joric's standalone version.
//
// index.html exposes the maptalks map as window.beaconMap after init.
(function () {
  'use strict';

  var qs = new URLSearchParams(location.search);
  // Two ways to point the overlay at the live data:
  //   ?datasrc=<full-url>  — the exact map-state URL (a hosting panel's relay, same-origin)
  //   ?beacon=<base>       — base http://ip:port (launcher, direct to the server)
  var dataUrl = qs.get('datasrc');
  var base = qs.get('beacon');
  if (!dataUrl && base) dataUrl = base.replace(/\/+$/, '') + '/api/v1/map/state';
  // Served straight off the game host (BeaconServer's /map/) with no param:
  // default to the same-origin state endpoint so live dots work out of the box.
  // file:// has no usable origin for that, so it stays opt-in via ?beacon.
  if (!dataUrl && (location.protocol === 'http:' || location.protocol === 'https:')) {
    dataUrl = location.origin + '/api/v1/map/state';
  }
  if (!dataUrl) {
    console.log('[beacon-live] no ?datasrc / ?beacon param; live overlay disabled');
    return;
  }

  var POLL_MS = 2000;
  var layer = null;
  var markers = {}; // id -> maptalks.Marker
  var fitted = false; // frame-to-players runs once
  // Missed answers in a row. One miss is a blip and keeps the dots; the second
  // clears them and says the server is offline, so a stopped server never
  // leaves its players standing on the map.
  var MISSES_OFFLINE = 2;
  var misses = 0;
  var chip = null;
  var chipText = null;

  // The status chip ("Server offline" / "No one online" / "1 player online,
  // location not shown yet"), drawn like the map's own controls
  // (search-control.css, markers-control.css: a white rounded pill,
  // sans-serif, the same soft shadow), centred at the bottom just above the
  // version picker (.version, bottom 8px), clear of the layer switcher, the
  // zoom buttons and the compass in the corners and of the marker pills that
  // wrap at the top on a phone. Plain text in a div: nothing to press. Its dot
  // is grey, red when the server is offline, and the players' own blue when
  // it counts players the map cannot place.
  // It never runs under those corner buttons (20px in, 28px wide on each side):
  // it keeps 60px from either edge (insets, not left:50% and a shift, which
  // would cap a wrapping chip at half the map), and a label too long for that
  // on a phone wraps, but only between its phrases (label: ['3 players online,',
  // 'locations not shown yet']), each phrase held together by no-break spaces,
  // so the last line is never one or two stray words.
  // Wrapped, the text would keep the whole width it was offered, with a gap
  // beside the shorter line: fitChip narrows it to its longest line.
  function fitChip() {
    if (!chipText || chip.style.display === 'none') return;
    chipText.style.width = '';
    var range = document.createRange();
    range.selectNodeContents(chipText);
    var rects = range.getClientRects();
    if (rects.length < 2) return;
    var widest = 0;
    for (var i = 0; i < rects.length; i++) widest = Math.max(widest, rects[i].width);
    chipText.style.width = Math.ceil(widest) + 'px';
  }

  function setStatus(text) {
    if (!text) {
      if (chip) chip.style.display = 'none';
      return;
    }
    if (!chip) {
      var css = document.createElement('style');
      css.textContent =
        '.beacon-live-chip{position:absolute;z-index:10001;left:60px;right:60px;bottom:44px;margin:0 auto;' +
        'width:-moz-fit-content;width:fit-content;' +
        'display:flex;align-items:center;gap:8px;box-sizing:border-box;' +
        'padding:7px 16px 7px 12px;border-radius:16px;background:#fff;color:rgba(0,0,0,0.8);' +
        'box-shadow:0 2px 2px rgba(0,0,0,0.2);font-family:sans-serif;font-size:14px;line-height:18px;' +
        'text-align:center;pointer-events:none;user-select:none}' +
        '.beacon-live-chip:before{content:"";flex:0 0 auto;width:10px;height:10px;border-radius:50%;' +
        'background:#9aa3ad;box-shadow:0 0 0 2px rgba(0,0,0,0.08)}' +
        '.beacon-live-chip.offline:before{background:#e0533d}' +
        '.beacon-live-chip.players:before{background:#36c6ff}';
      document.head.appendChild(css);
      chip = document.createElement('div');
      chip.className = 'beacon-live-chip';
      chip.setAttribute('role', 'status');
      chip.setAttribute('aria-live', 'polite');
      chipText = document.createElement('span');
      chip.appendChild(chipText);
      document.body.appendChild(chip);
      window.addEventListener('resize', fitChip);
    }
    chip.className = 'beacon-live-chip' + (text.offline ? ' offline' : '') + (text.players ? ' players' : '');
    chipText.textContent = [].concat(text.label).map(function (phrase) {
      return phrase.replace(/ /g, '\u00a0');
    }).join(' ');
    chip.style.display = 'flex';
    fitChip();
  }

  function symbolFor(name) {
    return {
      markerType: 'ellipse',
      markerWidth: 24, markerHeight: 24,
      markerFill: '#36c6ff', markerFillOpacity: 0.95,
      markerLineColor: '#ffffff', markerLineWidth: 3, markerLineOpacity: 1.0,
      textName: name || '',
      textFill: '#eaf6ff', textSize: 15, textWeight: 'bold',
      textHaloFill: '#0b1620', textHaloRadius: 3, textDy: -22
    };
  }

  // Where a player stands on the map, or null when the server does not know.
  // The game server cannot read a remote player's position (each player is
  // simulated on their own PC): only the player's own Beacon app sends it. With
  // no fresh position from any app, Beacon answers from the game's player list
  // instead, which has every player at exactly 0,0,0 (BeaconServer
  // ServeMapStateAsync). That spot is off the map's edge, so a dot there sat in
  // the corner and pulled the opening view with it. Such a player, or one with
  // no numbers at all, is counted in the chip instead and never drawn.
  function coordOf(p) {
    function num(v) {
      if (v === null || v === undefined || v === '') return NaN;
      var n = Number(v);
      return isFinite(n) ? n : NaN;
    }
    var x = num(p.x), y = num(p.y), z = num(p.z);
    if (isNaN(x) || isNaN(y)) return null;
    if (x === 0 && y === 0 && (isNaN(z) || z === 0)) return null;
    return [x, y];
  }

  // Draws the players the map can place and returns how many are drawn. A
  // player without a known spot is left out of `seen`, so a dot they already
  // had is removed below rather than slid to the corner.
  function apply(players) {
    var m = window.beaconMap;
    if (!m) return 0;
    if (!layer) {
      layer = new maptalks.VectorLayer('beacon-live-players').addTo(m);
      if (layer.bringToFront) layer.bringToFront();
    }
    var seen = {};
    (players || []).forEach(function (p) {
      var id = p.id || p.name;
      if (!id) return;
      var coord = coordOf(p);
      if (!coord) return;
      seen[id] = true;
      if (markers[id]) {
        // Glide from the current spot to the new one over the poll interval
        // instead of snapping, so movement reads as smooth swimming.
        var cur = markers[id].getCoordinates();
        markers[id]._from = { x: cur.x, y: cur.y };
        markers[id]._to = { x: coord[0], y: coord[1] };
        markers[id]._t0 = (window.performance && performance.now ? performance.now() : Date.now());
        markers[id].updateSymbol({ textName: p.name || '' });
      } else {
        var mk = new maptalks.Marker(coord, {
          symbol: symbolFor(p.name),
          properties: { beaconLive: true, name: p.name }
        });
        markers[id] = mk;
        layer.addGeometry(mk);
      }
    });
    // drop players who left
    Object.keys(markers).forEach(function (id) {
      if (!seen[id]) {
        layer.removeGeometry(markers[id]);
        delete markers[id];
      }
    });

    // One-time: frame the view on the players the first time any show up, so
    // the live map opens zoomed to the action instead of the whole world.
    // Only drawn players count: one the map cannot place never pulls the view.
    // Deferred — the map runs its own initial extent-fit during data load, so
    // we wait a beat to apply ours last instead of fighting it.
    var drawn = Object.keys(markers).length;
    if (!fitted && drawn > 0) {
      fitted = true;
      setTimeout(function () {
        try {
          var cs = Object.keys(markers).map(function (id) { return markers[id].getCoordinates(); });
          if (!cs.length) return;
          if (cs.length === 1) { m.setCenterAndZoom(cs[0], 4); return; }
          var xs = cs.map(function (c) { return c.x; });
          var ys = cs.map(function (c) { return c.y; });
          var pad = 900;
          var ext = new maptalks.Extent(
            Math.min.apply(null, xs) - pad, Math.min.apply(null, ys) - pad,
            Math.max.apply(null, xs) + pad, Math.max.apply(null, ys) + pad);
          if (m.fitExtent) m.fitExtent(ext, 0);
        } catch (e) { /* framing is best-effort */ }
      }, 1800);
    }
    return drawn;
  }

  // The chip for an answer with players in it: hidden while every one of them
  // is on the map, otherwise how many the map cannot place yet. They are on
  // the server (it lists them); only their spot has not reached the map.
  function playersStatus(total, drawn) {
    var n = total - drawn;
    if (n <= 0) return null;
    var where = (n === 1 ? 'location' : 'locations') + ' not shown yet';
    if (drawn > 0) return { label: [n + ' more online,', where], players: true };
    return { label: [n + (n === 1 ? ' player' : ' players') + ' online,', where], players: true };
  }

  // A miss: no answer, an error status, or an answer without a players list.
  // The first keeps the last dots; from the second on, the dots go and the
  // chip says the server is offline.
  function missed() {
    misses++;
    if (misses >= MISSES_OFFLINE) {
      apply([]);
      setStatus({ label: 'Server offline', offline: true });
    }
  }

  // 401/403: the server answered, but the map is private and this viewer is
  // not a player on it. That is not an outage, so it never says offline: the
  // dots clear and no status is shown.
  var REFUSED = {};
  function refused() {
    misses = 0;
    apply([]);
    setStatus(null);
  }

  function poll() {
    fetch(dataUrl, { cache: 'no-store', mode: 'cors' })
      .then(function (r) {
        if (r.status === 401 || r.status === 403) return REFUSED;
        return r.ok ? r.json() : null;
      })
      .then(function (s) {
        if (s === REFUSED) { refused(); return; }
        if (!s || !Array.isArray(s.players)) { missed(); return; }
        misses = 0;
        var drawn = apply(s.players);
        setStatus(s.players.length ? playersStatus(s.players.length, drawn) : { label: 'No one online' });
      })
      .catch(missed);
  }

  // Smoothly interpolate each dot from its last position to its newest one over
  // the poll interval, so a player reads as gliding instead of teleporting.
  function tweenStep() {
    var now = (window.performance && performance.now ? performance.now() : Date.now());
    Object.keys(markers).forEach(function (id) {
      var mk = markers[id];
      if (!mk._from || !mk._to) return;
      var t = (now - (mk._t0 || now)) / POLL_MS;
      if (t > 1) t = 1;
      if (t < 0) t = 0;
      var x = mk._from.x + (mk._to.x - mk._from.x) * t;
      var y = mk._from.y + (mk._to.y - mk._from.y) * t;
      mk.setCoordinates([x, y]);
    });
    requestAnimationFrame(tweenStep);
  }

  function start() {
    if (!window.beaconMap) { setTimeout(start, 400); return; }
    poll();
    setInterval(poll, POLL_MS);
    requestAnimationFrame(tweenStep);
    console.log('[beacon-live] polling ' + dataUrl + ' every ' + POLL_MS + 'ms');
  }

  if (document.readyState === 'complete') start();
  else window.addEventListener('load', start);
})();
