/**
 * The standalone flythrough viewer's player (editor/exportFlythrough.tsx): plain ES5-style JavaScript and CSS as strings,
 * carried inside the exported HTML file so it plays with no server, no network and no library. It is a hand-written port
 * of three modules: the camera (canvas/camera.ts), the playback state machine (editor/tourPlayer.ts) and the
 * presentation overlay's controls and keys (editor/PresentationStage.tsx). They are written twice on purpose, since
 * the file must be self-contained, so `flythroughPlayer.test.tsx` runs both over the same tours and holds them to the
 * same numbers; change one and that test names the other.
 *
 * The page it runs in holds the diagram's SVG (the export surface, always full detail) in #stage, a caption, and the
 * controls. The player takes the SVG's `g.world` group and its background rectangle, sizes the SVG's coordinate system to
 * the window on every resize, and sets the world's transform for the camera each frame, so what it draws is the export
 * exactly, not a second renderer. It also publishes `window.flythrough` (`next`, `previous`, `play`, `pause`, `toggle`,
 * `seek`, `jumpTo`, `state`), for a page that embeds the file and for the parity test.
 *
 * The JavaScript has no template placeholders and no backticks, so it is safe inside a TypeScript template literal.
 */

export const PLAYER_SCRIPT = String.raw`(function () {
  'use strict';
  var data = JSON.parse(document.getElementById('flythrough-data').textContent);
  var keys = data.keyframes;
  var count = keys.length;
  var loops = data.loop === true && count > 1;
  var end = loops ? count : Math.max(0, count - 1);
  var defaults = data.defaults;
  var EPS = 1e-9, MAX_STEPS = 64, MAX_FRAME = 100, IDLE = 2500;
  var RHO = Math.SQRT2, RHO2 = 2, RHO4 = 4, NEAR2 = 1e-12, MIN_ZOOM = 0.05, MAX_ZOOM = 8;
  var reduced = !!(window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches);

  function clampZoom(z) { return isFinite(z) ? Math.min(MAX_ZOOM, Math.max(MIN_ZOOM, z)) : 1; }
  function cameraForView(v, w, h) {
    var z = clampZoom(Math.min(w / v.w, h / v.h));
    return { x: v.x + v.w / 2, y: v.y + v.h / 2, w: w / z };
  }
  function viewportFor(c, w, h) {
    var z = clampZoom(w / c.w);
    return { zoom: z, x: w / 2 - c.x * z, y: h / 2 - c.y * z };
  }
  function fly(a, b, u) {
    if (!(u > 0)) return a;
    if (u >= 1) return b;
    var dx = b.x - a.x, dy = b.y - a.y, d2 = dx * dx + dy * dy;
    if (d2 < NEAR2) return { x: a.x, y: a.y, w: a.w * Math.pow(b.w / a.w, u) };
    var d1 = Math.sqrt(d2);
    var b0 = (b.w * b.w - a.w * a.w + RHO4 * d2) / (2 * a.w * RHO2 * d1);
    var b1 = (b.w * b.w - a.w * a.w - RHO4 * d2) / (2 * b.w * RHO2 * d1);
    var r0 = -Math.asinh(b0), r1 = -Math.asinh(b1);
    var s = u * ((r1 - r0) / RHO);
    var along = (a.w / (RHO2 * d1)) * (Math.cosh(r0) * Math.tanh(RHO * s + r0) - Math.sinh(r0));
    return { x: a.x + along * dx, y: a.y + along * dy, w: (a.w * Math.cosh(r0)) / Math.cosh(RHO * s + r0) };
  }
  function ease(name, t) {
    var s = Math.min(1, Math.max(0, typeof t === 'number' && isFinite(t) ? t : 0));
    switch (name) {
      case 'linear': return s;
      case 'easeIn': return s * s * s;
      case 'easeOut': return 1 - Math.pow(1 - s, 3);
      case 'cut': return s < 0.5 ? 0 : 1;
      default: return s < 0.5 ? 4 * s * s * s : 1 - Math.pow(-2 * s + 2, 3) / 2;
    }
  }
  function easingOf(k) { return k.easing === undefined ? defaults.easing : k.easing; }
  function durationOf(k) { return k.durationMs === undefined ? defaults.duration : k.durationMs; }
  function holdOf(k) { return k.holdMs === undefined ? defaults.hold : k.holdMs; }
  function cameraAt(p, w, h) {
    var progress = Math.min(end, Math.max(0, isFinite(p) ? p : 0));
    if (end === 0) return cameraForView(keys[0].view, w, h);
    var seg = Math.min(Math.floor(progress), end - 1);
    var to = keys[(seg + 1) % count];
    return fly(cameraForView(keys[seg].view, w, h), cameraForView(to.view, w, h), ease(easingOf(to), progress - seg));
  }

  var st = { p: 0, target: undefined, playing: false, hold: holdOf(keys[0]) };
  function holdAt(p) { return holdOf(keys[Math.round(p) % Math.max(1, count)]); }
  function wrapped(p) { return loops && p >= end - EPS ? 0 : p; }
  function segMs(seg) {
    var arriving = keys[(seg + 1) % count];
    return reduced || easingOf(arriving) === 'cut' ? 0 : durationOf(arriving);
  }
  function finished() { return !loops && st.target === undefined && st.p >= end - EPS; }
  function index() { return Math.round(st.p) % count; }

  function advance(dt) {
    var p = st.p, target = st.target, playing = st.playing, hold = st.hold;
    if (target === undefined) p = wrapped(p);
    var remaining = Math.max(0, isFinite(dt) ? dt : 0);
    for (var step = 0; step < MAX_STEPS; step++) {
      if (target !== undefined) {
        var direction = Math.sign(target - p);
        if (direction === 0 || Math.abs(target - p) < EPS) { p = wrapped(target); target = undefined; hold = holdAt(p); continue; }
        var seg = direction > 0 ? Math.floor(p + EPS) : Math.ceil(p - EPS) - 1;
        var segEnd = direction > 0 ? Math.min(target, seg + 1) : Math.max(target, seg);
        var duration = segMs(seg);
        var needed = Math.abs(segEnd - p) * duration;
        if (duration === 0 || remaining >= needed) { p = segEnd; remaining -= duration === 0 ? 0 : needed; continue; }
        p += (direction * remaining) / duration;
        break;
      }
      if (!playing) break;
      if (remaining < hold) { hold -= remaining; break; }
      remaining -= hold;
      hold = 0;
      if (p >= end - EPS) { playing = false; break; }
      target = Math.floor(p + EPS) + 1;
    }
    st.p = p; st.target = target; st.playing = playing; st.hold = hold;
  }
  function next() {
    if (end === 0) return;
    var p = st.p;
    if (p >= end - EPS) { if (!loops) return; p = 0; }
    var from = st.target !== undefined && st.target > p + EPS ? st.target : p;
    st.p = p;
    st.target = Math.min(end, Math.floor(from + EPS) + 1);
    advance(0);
  }
  function previous() {
    if (end === 0) return;
    var p = st.p;
    if (p <= EPS) { if (!loops) return; p = end; }
    var from = st.target !== undefined && st.target < p - EPS ? st.target : p;
    st.p = p;
    st.target = Math.max(0, Math.ceil(from - EPS) - 1);
    advance(0);
  }
  function jumpTo(i) {
    var p = Math.min(Math.max(0, count - 1), Math.max(0, Math.trunc(isFinite(i) ? i : 0)));
    st.p = p; st.target = undefined; st.hold = holdAt(p);
  }
  function seek(progress) {
    var p = Math.min(end, Math.max(0, isFinite(progress) ? progress : 0));
    st.p = p; st.target = undefined; st.hold = holdAt(p);
  }
  function play() {
    if (end > 0 && finished()) { st.p = 0; st.target = undefined; st.hold = holdAt(0); }
    st.playing = true;
  }
  function pause() { st.playing = false; }
  function toggle() { if (st.playing) pause(); else play(); }

  var root = document.getElementById('flythrough');
  var stage = document.getElementById('stage');
  var svg = stage.querySelector('svg');
  var world = svg.querySelector('g.world');
  var back = svg.querySelector('rect.canvas-background');
  var caption = document.getElementById('ft-caption');
  var range = document.getElementById('ft-range');
  var counter = document.getElementById('ft-counter');
  var playButton = document.getElementById('ft-play');
  var prevButton = document.getElementById('ft-prev');
  var nextButton = document.getElementById('ft-next');
  var fullButton = document.getElementById('ft-full');
  var size = { w: 0, h: 0 };
  var drawn = { caption: null, index: -1, playing: null, finished: null };
  var frame = 0;

  function measure() {
    size.w = Math.max(1, stage.clientWidth || window.innerWidth);
    size.h = Math.max(1, stage.clientHeight || window.innerHeight);
    svg.setAttribute('viewBox', '0 0 ' + size.w + ' ' + size.h);
    svg.removeAttribute('width');
    svg.removeAttribute('height');
    if (back) { back.setAttribute('width', size.w); back.setAttribute('height', size.h); }
  }
  function render() {
    var v = viewportFor(cameraAt(st.p, size.w, size.h), size.w, size.h);
    world.setAttribute('transform', 'translate(' + v.x + ' ' + v.y + ') scale(' + v.zoom + ')');
    var i = index(), done = finished();
    range.value = st.p;
    if (i !== drawn.index) {
      drawn.index = i;
      counter.textContent = (i + 1) + ' / ' + count;
      range.setAttribute('aria-valuetext', 'Keyframe ' + (i + 1) + ' of ' + count);
      var text = keys[i].caption || '';
      if (text !== drawn.caption) { drawn.caption = text; caption.textContent = text; caption.hidden = text === ''; }
      var ticks = document.querySelectorAll('.ft-tick');
      for (var t = 0; t < ticks.length; t++) ticks[t].toggleAttribute('data-current', t === i);
    }
    if (st.playing !== drawn.playing || done !== drawn.finished) {
      drawn.playing = st.playing; drawn.finished = done;
      var label = st.playing ? 'Pause' : done ? 'Replay' : 'Play';
      playButton.setAttribute('aria-label', label);
      playButton.setAttribute('title', label + ' (P)');
      playButton.setAttribute('data-state', st.playing ? 'playing' : done ? 'finished' : 'paused');
    }
    prevButton.setAttribute('aria-disabled', String(end === 0 || (st.p <= 0 && !loops)));
    nextButton.setAttribute('aria-disabled', String(end === 0 || (done && !loops)));
  }

  var last = 0;
  function tick(now) {
    frame = 0;
    advance(Math.min(Math.max(0, now - last), MAX_FRAME));
    last = now;
    render();
    schedule();
  }
  function animating() { return st.playing || st.target !== undefined; }
  function schedule() {
    if (!frame && animating()) { frame = requestAnimationFrame(tick); }
  }
  function changed() {
    reveal();
    if (!frame && animating()) { last = performance.now(); }
    render();
    schedule();
  }

  var idle = 0;
  function mayFade() { return st.playing || !!document.fullscreenElement; }
  function reveal() {
    root.setAttribute('data-chrome', 'shown');
    clearTimeout(idle);
    if (mayFade()) idle = setTimeout(function () { root.setAttribute('data-chrome', 'hidden'); }, IDLE);
  }

  prevButton.addEventListener('click', function () { if (prevButton.getAttribute('aria-disabled') !== 'true') { previous(); changed(); } });
  nextButton.addEventListener('click', function () { if (nextButton.getAttribute('aria-disabled') !== 'true') { next(); changed(); } });
  playButton.addEventListener('click', function () { toggle(); changed(); });
  range.addEventListener('input', function () { pause(); seek(range.valueAsNumber); changed(); });
  if (document.documentElement.requestFullscreen) {
    fullButton.addEventListener('click', function () {
      if (document.fullscreenElement) document.exitFullscreen(); else root.requestFullscreen();
    });
  } else { fullButton.hidden = true; }
  document.addEventListener('fullscreenchange', function () {
    fullButton.setAttribute('aria-pressed', String(!!document.fullscreenElement));
    reveal();
  });
  root.addEventListener('pointermove', reveal);
  root.addEventListener('pointerdown', reveal);
  root.addEventListener('focusin', reveal);
  window.addEventListener('resize', function () { measure(); render(); });

  window.addEventListener('keydown', function (e) {
    reveal();
    if (e.ctrlKey || e.metaKey || e.altKey) return;
    var onButton = e.target instanceof HTMLButtonElement, onRange = e.target === range;
    var handled = true;
    switch (e.key) {
      case 'ArrowRight': case 'ArrowDown': if (onRange) handled = false; else next(); break;
      case 'ArrowLeft': case 'ArrowUp': if (onRange) handled = false; else previous(); break;
      case 'PageDown': next(); break;
      case 'PageUp': case 'Backspace': previous(); break;
      case ' ': case 'Enter': if (onButton || onRange) handled = false; else next(); break;
      case 'Home': if (onRange) handled = false; else jumpTo(0); break;
      case 'End': if (onRange) handled = false; else jumpTo(count - 1); break;
      case 'p': case 'P': toggle(); break;
      case 'f': case 'F': if (document.documentElement.requestFullscreen) fullButton.click(); break;
      default:
        if (/^[1-9]$/.test(e.key) && Number(e.key) <= count) jumpTo(Number(e.key) - 1); else handled = false;
    }
    if (handled) { e.preventDefault(); changed(); }
  });

  window.flythrough = {
    next: function () { next(); changed(); },
    previous: function () { previous(); changed(); },
    play: function () { play(); changed(); },
    pause: function () { pause(); changed(); },
    toggle: function () { toggle(); changed(); },
    seek: function (p) { seek(p); changed(); },
    jumpTo: function (i) { jumpTo(i); changed(); },
    state: function () { return { p: st.p, target: st.target, playing: st.playing, holdLeftMs: st.hold }; },
    advance: function (dt) { advance(dt); render(); },
    cameraAt: cameraAt,
    setReducedMotion: function (value) { reduced = !!value; },
    resize: function (w, h) { size.w = w; size.h = h; svg.setAttribute('viewBox', '0 0 ' + w + ' ' + h); render(); }
  };

  measure();
  render();
  if (location.hash === '#play' || location.hash === '#autoplay') { play(); changed(); }
  root.setAttribute('data-ready', '');
})();
`;

export const PLAYER_CSS = `
html, body { margin: 0; height: 100%; overflow: hidden; }
body { font: 14px/1.4 system-ui, -apple-system, 'Segoe UI', Roboto, sans-serif; }
#flythrough { position: fixed; inset: 0; }
#stage { position: absolute; inset: 0; overflow: hidden; }
#stage svg { display: block; width: 100%; height: 100%; }
#flythrough[data-chrome='hidden'] #stage { cursor: none; }
#ft-caption { position: absolute; left: 50%; bottom: 84px; box-sizing: border-box; max-width: min(720px, calc(100% - 32px)); margin: 0; padding: 8px 16px; transform: translateX(-50%); text-align: center; font-size: 18px; color: #f4f6fb; background: rgba(18, 23, 35, 0.92); border: 1px solid rgba(255, 255, 255, 0.18); border-radius: 6px; pointer-events: none; overflow-wrap: anywhere; }
#ft-caption[hidden] { display: none; }
#ft-chrome { transition: opacity 0.2s; }
#flythrough[data-chrome='hidden'] #ft-chrome { opacity: 0; pointer-events: none; }
@media (prefers-reduced-motion: reduce) { #ft-chrome { transition: none; } }
#ft-bar { position: absolute; left: 50%; bottom: 16px; box-sizing: border-box; display: flex; align-items: center; gap: 8px; width: min(640px, calc(100% - 32px)); padding: 8px 12px; transform: translateX(-50%); color: #f4f6fb; background: rgba(18, 23, 35, 0.92); border: 1px solid rgba(255, 255, 255, 0.18); border-radius: 6px; }
#ft-bar button { flex: none; display: inline-flex; align-items: center; justify-content: center; min-width: 36px; min-height: 36px; padding: 0; color: inherit; background: rgba(255, 255, 255, 0.08); border: 1px solid rgba(255, 255, 255, 0.22); border-radius: 6px; cursor: pointer; }
#ft-bar button[aria-disabled='true'] { cursor: not-allowed; opacity: 0.5; }
#ft-bar button[aria-pressed='true'] { background: #e04832; border-color: #e04832; }
#ft-bar button:focus-visible, #ft-range:focus-visible { outline: 2px solid #ff8a75; outline-offset: 1px; }
#ft-bar svg { display: block; }
#ft-play [data-icon] { display: none; }
#ft-play[data-state='paused'] [data-icon='play'], #ft-play[data-state='playing'] [data-icon='pause'], #ft-play[data-state='finished'] [data-icon='replay'] { display: block; }
#ft-scrubber { position: relative; flex: 1; min-width: 0; display: flex; align-items: center; }
#ft-range { width: 100%; margin: 0; accent-color: #e04832; }
#ft-ticks { position: absolute; left: 8px; right: 8px; bottom: -6px; height: 4px; pointer-events: none; }
.ft-tick { position: absolute; top: 0; width: 4px; height: 4px; margin-left: -2px; border-radius: 50%; background: rgba(255, 255, 255, 0.5); }
.ft-tick[data-current] { background: #ff8a75; }
#ft-counter { flex: none; min-width: 4.5ch; text-align: right; font-variant-numeric: tabular-nums; opacity: 0.75; }
`;
