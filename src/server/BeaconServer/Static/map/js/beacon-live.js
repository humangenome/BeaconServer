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
  //   ?datasrc=<full-url>  — the exact map-state URL (SS panel proxy, same-origin)
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

  function apply(players) {
    var m = window.beaconMap;
    if (!m) return;
    if (!layer) {
      layer = new maptalks.VectorLayer('beacon-live-players').addTo(m);
      if (layer.bringToFront) layer.bringToFront();
    }
    var seen = {};
    (players || []).forEach(function (p) {
      var id = p.id || p.name;
      if (!id) return;
      seen[id] = true;
      var coord = [Number(p.x) || 0, Number(p.y) || 0];
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
    // Deferred — the map runs its own initial extent-fit during data load, so
    // we wait a beat to apply ours last instead of fighting it.
    if (!fitted && Object.keys(markers).length > 0) {
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
  }

  function poll() {
    fetch(dataUrl, { cache: 'no-store', mode: 'cors' })
      .then(function (r) { return r.ok ? r.json() : null; })
      .then(function (s) { if (s) apply(s.players); })
      .catch(function () { /* server offline / unreachable — keep last dots */ });
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
