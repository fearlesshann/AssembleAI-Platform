/* Preserved drawing implementation from references/ui-v1/app.js login FX and specular rim.
   Only DOM scoping, reduced-motion detection, and mount/unmount ownership are adapted. */
export function mountReferenceLoginFx(root) {
  const cleanups = [];
  const shared = {};
  const byId = id => root.id === id ? root : root.querySelector(`[id="${id}"]`);
  const listen = (target, name, callback, options) => {
    target.addEventListener(name, callback, options);
    cleanups.push(() => target.removeEventListener(name, callback, options));
  };
  const MutationObserver = class extends globalThis.MutationObserver {
    constructor(callback) { super(callback); cleanups.push(() => this.disconnect()); }
  };
  const ResizeObserver = class extends globalThis.ResizeObserver {
    constructor(callback) { super(callback); cleanups.push(() => this.disconnect()); }
  };
  /* —— Login FX: LoginWaves-style contours + soft ether haze —— */
  (function initLoginFx() {
    var screen = byId("loginScreen");
    var wavesHost = byId("loginWaves");
    var wavesCanvas = byId("loginWavesCanvas");
    var etherCanvas = byId("loginEther");
    if (!screen || !wavesCanvas) return;

    var wavesCtx = wavesCanvas.getContext("2d");
    var etherCtx = etherCanvas ? etherCanvas.getContext("2d") : null;
    if (!wavesCtx) return;
    var active = false;
    var raf = 0;
    var previousTick = 0;
    var wavePhase = 0;
    var waveInitialized = false;
    var pointerActiveUntil = 0;
    var baselineFrameMs = 1000 / 60;

    var cfg = {
      lineColor: "#ffffff",
      waveSpeedX: 0.02,
      waveSpeedY: 0.01,
      waveAmpX: 40,
      waveAmpY: 20,
      friction: 0.9,
      tension: 0.01,
      maxCursorMove: 120,
      xGap: 12,
      yGap: 36
    };

    var bounding = { width: 0, height: 0, left: 0, top: 0 };
    var mouse = { x: -10, y: 0, lx: 0, ly: 0, sx: 0, sy: 0, v: 0, vs: 0, a: 0, set: false };
    var lines = [];
    var ether = { w: 0, h: 0, t0: performance.now(), mx: 0.5, my: 0.25, mtx: 0.5, mty: 0.25 };

    function Grad(x, y, z) { this.x = x; this.y = y; this.z = z; }
    Grad.prototype.dot2 = function (x, y) { return this.x * x + this.y * y; };

    function Noise(seed) {
      this.grad3 = [
        new Grad(1, 1, 0), new Grad(-1, 1, 0), new Grad(1, -1, 0), new Grad(-1, -1, 0),
        new Grad(1, 0, 1), new Grad(-1, 0, 1), new Grad(1, 0, -1), new Grad(-1, 0, -1),
        new Grad(0, 1, 1), new Grad(0, -1, 1), new Grad(0, 1, -1), new Grad(0, -1, -1)
      ];
      this.p = [151,160,137,91,90,15,131,13,201,95,96,53,194,233,7,225,140,36,103,30,69,142,8,99,37,240,21,10,23,190,6,148,247,120,234,75,0,26,197,62,94,252,219,203,117,35,11,32,57,177,33,88,237,149,56,87,174,20,125,136,171,168,68,175,74,165,71,134,139,48,27,166,77,146,158,231,83,111,229,122,60,211,133,230,220,105,92,41,55,46,245,40,244,102,143,54,65,25,63,161,1,216,80,73,209,76,132,187,208,89,18,169,200,196,135,130,116,188,159,86,164,100,109,198,173,186,3,64,52,217,226,250,124,123,5,202,38,147,118,126,255,82,85,212,207,206,59,227,47,16,58,17,182,189,28,42,223,183,170,213,119,248,152,2,44,154,163,70,221,153,101,155,167,43,172,9,129,22,39,253,19,98,108,110,79,113,224,232,178,185,112,104,218,246,97,228,251,34,242,193,238,210,144,12,191,179,162,241,81,51,145,235,249,14,239,107,49,192,214,31,181,199,106,157,184,84,204,176,115,121,50,45,127,4,150,254,138,236,205,93,222,114,67,29,24,72,243,141,128,195,78,66,215,61,156,180];
      this.perm = new Array(512);
      this.gradP = new Array(512);
      this.seed(seed);
    }
    Noise.prototype.seed = function (seed) {
      if (seed > 0 && seed < 1) seed *= 65536;
      seed = Math.floor(seed);
      if (seed < 256) seed |= seed << 8;
      for (var i = 0; i < 256; i += 1) {
        var value = (i & 1) ? this.p[i] ^ (seed & 255) : this.p[i] ^ ((seed >> 8) & 255);
        this.perm[i] = this.perm[i + 256] = value;
        this.gradP[i] = this.gradP[i + 256] = this.grad3[value % 12];
      }
    };
    Noise.prototype.fade = function (v) { return v * v * v * (v * (v * 6 - 15) + 10); };
    Noise.prototype.lerp = function (a, b, t) { return (1 - t) * a + t * b; };
    Noise.prototype.perlin2 = function (x, y) {
      var cellX = Math.floor(x);
      var cellY = Math.floor(y);
      x -= cellX; y -= cellY;
      cellX &= 255; cellY &= 255;
      var n00 = this.gradP[cellX + this.perm[cellY]].dot2(x, y);
      var n01 = this.gradP[cellX + this.perm[cellY + 1]].dot2(x, y - 1);
      var n10 = this.gradP[cellX + 1 + this.perm[cellY]].dot2(x - 1, y);
      var n11 = this.gradP[cellX + 1 + this.perm[cellY + 1]].dot2(x - 1, y - 1);
      var u = this.fade(x);
      return this.lerp(this.lerp(n00, n10, u), this.lerp(n01, n11, u), this.fade(y));
    };

    var noise = new Noise(Math.random());

    function motionOn() {
      return document.documentElement.dataset.motion !== "reduced" && !window.matchMedia("(prefers-reduced-motion: reduce)").matches;
    }
    function isLight() {
      return document.documentElement.getAttribute("data-theme") === "light";
    }
    function readCssColor(name, fallback) {
      try {
        var v = getComputedStyle(document.documentElement).getPropertyValue(name).trim();
        return v || fallback;
      } catch (e) {
        return fallback;
      }
    }
    function syncThemeColors() {
      /* Neutral contour: white on dark plates, near-black on light */
      cfg.lineColor = isLight() ? "#1a1b1d" : "#ffffff";
    }

    function setWavesSize() {
      var bounds = (wavesHost || wavesCanvas).getBoundingClientRect();
      bounding.width = Math.max(1, Math.round(bounds.width));
      bounding.height = Math.max(1, Math.round(bounds.height));
      bounding.left = bounds.left;
      bounding.top = bounds.top;
      wavesCanvas.width = bounding.width;
      wavesCanvas.height = bounding.height;
    }

    function setLines() {
      lines = [];
      waveInitialized = false;
      wavePhase = 0;
      var overscanWidth = bounding.width + 200;
      var overscanHeight = bounding.height + 30;
      var xGap = cfg.xGap;
      var yGap = cfg.yGap;
      var totalLines = Math.ceil(overscanWidth / xGap);
      var totalPoints = Math.ceil(overscanHeight / yGap);
      var xStart = (bounding.width - xGap * totalLines) / 2;
      var yStart = (bounding.height - yGap * totalPoints) / 2;
      for (var li = 0; li <= totalLines; li += 1) {
        var points = [];
        for (var pi = 0; pi <= totalPoints; pi += 1) {
          points.push({
            x: xStart + xGap * li,
            y: yStart + yGap * pi,
            wave: { x: 0, y: 0 },
            cursor: { x: 0, y: 0, vx: 0, vy: 0 }
          });
        }
        lines.push(points);
      }
    }

    function movePoints(time, waveStride, waveOffset) {
      for (var lineIndex = 0; lineIndex < lines.length; lineIndex += 1) {
        var points = lines[lineIndex];
        for (var i = 0; i < points.length; i += 1) {
          var point = points[i];
          if (lineIndex % waveStride === waveOffset) {
            var move = noise.perlin2(
              (point.x + time * cfg.waveSpeedX) * 0.002,
              (point.y + time * cfg.waveSpeedY) * 0.0015
            ) * 12;
            point.wave.x = Math.cos(move) * cfg.waveAmpX;
            point.wave.y = Math.sin(move) * cfg.waveAmpY;
          }
          var deltaX = point.x - mouse.sx;
          var deltaY = point.y - mouse.sy;
          var influenceRadius = Math.max(175, mouse.vs);
          var distanceSquared = deltaX * deltaX + deltaY * deltaY;
          var hasCursorMotion =
            Math.abs(point.cursor.x) + Math.abs(point.cursor.y) +
            Math.abs(point.cursor.vx) + Math.abs(point.cursor.vy) > 0.001;
          if (distanceSquared < influenceRadius * influenceRadius) {
            var distance = Math.sqrt(distanceSquared);
            var strength = 1 - distance / influenceRadius;
            var force = Math.cos(distance * 0.001) * strength;
            point.cursor.vx += Math.cos(mouse.a) * force * influenceRadius * mouse.vs * 0.00065;
            point.cursor.vy += Math.sin(mouse.a) * force * influenceRadius * mouse.vs * 0.00065;
          }
          if (distanceSquared >= influenceRadius * influenceRadius && !hasCursorMotion) continue;
          point.cursor.vx += (0 - point.cursor.x) * cfg.tension;
          point.cursor.vy += (0 - point.cursor.y) * cfg.tension;
          point.cursor.vx *= cfg.friction;
          point.cursor.vy *= cfg.friction;
          point.cursor.x += point.cursor.vx * 2;
          point.cursor.y += point.cursor.vy * 2;
          point.cursor.x = Math.min(cfg.maxCursorMove, Math.max(-cfg.maxCursorMove, point.cursor.x));
          point.cursor.y = Math.min(cfg.maxCursorMove, Math.max(-cfg.maxCursorMove, point.cursor.y));
        }
      }
    }

    function drawLines() {
      wavesCtx.clearRect(0, 0, bounding.width, bounding.height);
      wavesCtx.beginPath();
      wavesCtx.strokeStyle = cfg.lineColor;
      wavesCtx.globalAlpha = isLight() ? 0.28 : 0.22;
      wavesCtx.lineWidth = 1;
      for (var li = 0; li < lines.length; li += 1) {
        var points = lines[li];
        var first = points[0];
        wavesCtx.moveTo(
          Math.round((first.x + first.wave.x) * 10) / 10,
          Math.round((first.y + first.wave.y) * 10) / 10
        );
        for (var index = 0; index < points.length; index += 1) {
          var point = points[index];
          var isLast = index === points.length - 1;
          var cursorX = isLast ? 0 : point.cursor.x;
          var cursorY = isLast ? 0 : point.cursor.y;
          var x = Math.round((point.x + point.wave.x + cursorX) * 10) / 10;
          var y = Math.round((point.y + point.wave.y + cursorY) * 10) / 10;
          wavesCtx.lineTo(x, y);
          if (isLast) wavesCtx.moveTo(x, y);
        }
      }
      wavesCtx.stroke();
      wavesCtx.globalAlpha = 1;
    }

    function paintStaticWaves() {
      // one calm frame without cursor physics when reduced-motion
      var time = 0;
      for (var lineIndex = 0; lineIndex < lines.length; lineIndex += 1) {
        var points = lines[lineIndex];
        for (var i = 0; i < points.length; i += 1) {
          var point = points[i];
          var move = noise.perlin2(point.x * 0.002, point.y * 0.0015) * 12;
          point.wave.x = Math.cos(move) * cfg.waveAmpX;
          point.wave.y = Math.sin(move) * cfg.waveAmpY;
          point.cursor.x = point.cursor.y = point.cursor.vx = point.cursor.vy = 0;
        }
      }
      drawLines();
    }

    function resizeEther() {
      if (!etherCanvas || !etherCtx) return;
      var rect = etherCanvas.getBoundingClientRect();
      var dpr = Math.min(window.devicePixelRatio || 1, 1.5);
      ether.w = Math.max(1, Math.floor(rect.width));
      ether.h = Math.max(1, Math.floor(rect.height));
      etherCanvas.width = Math.floor(ether.w * dpr);
      etherCanvas.height = Math.floor(ether.h * dpr);
      etherCtx.setTransform(dpr, 0, 0, dpr, 0, 0);
    }

    function paintEther(now) {
      if (!etherCtx) return;
      var t = (now - ether.t0) / 1000;
      var moving = motionOn();
      if (moving) {
        ether.mx += (ether.mtx - ether.mx) * 0.08;
        ether.my += (ether.mty - ether.my) * 0.08;
      }
      etherCtx.clearRect(0, 0, ether.w, ether.h);
      etherCtx.globalCompositeOperation = "lighter";
      var cx = ether.w * (0.52 + (ether.mx - 0.5) * 0.34);
      var cy = ether.h * (0.24 + (ether.my - 0.25) * 0.26);
      var drift = moving ? Math.sin(t * 0.85) * 46 : 0;
      var drift2 = moving ? Math.cos(t * 0.62) * 32 : 0;
      var colors = isLight()
        ? (
            moving
              ? ["rgba(0,0,0,0.11)", "rgba(0,0,0,0.08)", "rgba(0,0,0,0.06)", "rgba(0,0,0,0.045)"]
              : ["rgba(0,0,0,0.05)", "rgba(0,0,0,0.035)", "rgba(0,0,0,0.03)", "rgba(0,0,0,0.02)"]
          )
        : (
            moving
              ? ["rgba(255,255,255,0.16)", "rgba(255,255,255,0.11)", "rgba(255,255,255,0.08)", "rgba(255,255,255,0.055)"]
              : ["rgba(255,255,255,0.06)", "rgba(255,255,255,0.045)", "rgba(255,255,255,0.035)", "rgba(255,255,255,0.025)"]
          );
      function blob(x, y, r, c) {
        var g = etherCtx.createRadialGradient(x, y, 0, x, y, r);
        g.addColorStop(0, c);
        g.addColorStop(1, "rgba(0,0,0,0)");
        etherCtx.fillStyle = g;
        etherCtx.beginPath();
        etherCtx.arc(x, y, r, 0, Math.PI * 2);
        etherCtx.fill();
      }
      blob(cx + drift, cy + drift2 * 0.25, ether.w * 0.62, colors[0]);
      blob(cx - ether.w * 0.18 - drift * 0.7, cy + ether.h * 0.1 + drift2 * 0.35, ether.w * 0.48, colors[1]);
      blob(cx + ether.w * 0.16 + drift2 * 0.4, cy - ether.h * 0.05 - drift * 0.2, ether.w * 0.38, colors[2]);
      blob(cx - ether.w * 0.02 + drift * 0.35, cy + ether.h * 0.18, ether.w * 0.3, colors[3]);
      etherCtx.globalCompositeOperation = "source-over";
    }

    function tick(time) {
      if (!active || document.hidden) {
        raf = 0;
        return;
      }
      if (!motionOn()) {
        paintStaticWaves();
        paintEther(time);
        raf = 0;
        return;
      }
      var elapsed = previousTick > 0 ? Math.min(Math.max(time - previousTick, 0), 100) : baselineFrameMs;
      var follow = 1 - Math.pow(0.9, elapsed / baselineFrameMs);
      previousTick = time;
      mouse.sx += (mouse.x - mouse.sx) * follow;
      mouse.sy += (mouse.y - mouse.sy) * follow;
      var deltaX = mouse.x - mouse.lx;
      var deltaY = mouse.y - mouse.ly;
      var distance = Math.hypot(deltaX, deltaY);
      mouse.v = distance;
      mouse.vs += (distance - mouse.vs) * follow;
      mouse.vs = Math.min(100, mouse.vs);
      mouse.lx = mouse.x;
      mouse.ly = mouse.y;
      mouse.a = Math.atan2(deltaY, deltaX);
      var waveStride = waveInitialized && time < pointerActiveUntil ? 2 : 1;
      movePoints(time, waveStride, wavePhase % waveStride);
      waveInitialized = true;
      wavePhase = (wavePhase + 1) % 2;
      drawLines();
      paintEther(time);
      raf = requestAnimationFrame(tick);
    }

    function updateMouse(clientX, clientY) {
      mouse.x = clientX - bounding.left;
      mouse.y = clientY - bounding.top;
      pointerActiveUntil = performance.now() + 140;
      if (!mouse.set) {
        mouse.sx = mouse.x;
        mouse.sy = mouse.y;
        mouse.lx = mouse.x;
        mouse.ly = mouse.y;
        mouse.set = true;
      }
      if (etherCanvas) {
        var er = etherCanvas.getBoundingClientRect();
        if (er.width && er.height) {
          ether.mtx = (clientX - er.left) / er.width;
          ether.mty = (clientY - er.top) / er.height;
        }
      }
    }

    function resizeAll() {
      syncThemeColors();
      setWavesSize();
      setLines();
      resizeEther();
      if (!motionOn()) {
        paintStaticWaves();
        paintEther(performance.now());
      }
    }

    function start() {
      if (active) return;
      active = true;
      previousTick = 0;
      resizeAll();
      if (raf) cancelAnimationFrame(raf);
      raf = requestAnimationFrame(tick);
    }

    function stop() {
      active = false;
      if (raf) cancelAnimationFrame(raf);
      raf = 0;
      previousTick = 0;
      if (wavesCtx) wavesCtx.clearRect(0, 0, bounding.width, bounding.height);
      if (etherCtx) etherCtx.clearRect(0, 0, ether.w, ether.h);
    }

    listen(window, "mousemove", function (e) {
      if (!active) return;
      updateMouse(e.clientX, e.clientY);
    }, { passive: true });
    listen(window, "touchmove", function (e) {
      if (!active || !e.touches || !e.touches[0]) return;
      updateMouse(e.touches[0].clientX, e.touches[0].clientY);
    }, { passive: true });
    listen(window, "resize", function () {
      if (!active) return;
      resizeAll();
    }, { passive: true });
    listen(document, "visibilitychange", function () {
      if (!active) return;
      if (document.hidden) {
        if (raf) cancelAnimationFrame(raf);
        raf = 0;
      } else if (!raf) {
        raf = requestAnimationFrame(tick);
      }
    });
    new MutationObserver(function () {
      if (!active) return;
      syncThemeColors();
      if (motionOn()) {
        if (!raf) raf = requestAnimationFrame(tick);
      } else {
        if (raf) cancelAnimationFrame(raf);
        raf = 0;
        paintStaticWaves();
        paintEther(performance.now());
      }
    }).observe(document.documentElement, { attributes: true, attributeFilter: ["data-theme", "data-motion"] });

    if (typeof ResizeObserver !== "undefined" && wavesHost) {
      new ResizeObserver(function () {
        if (!active) return;
        resizeAll();
      }).observe(wavesHost);
    }

    shared.fx = {
      setActive: function (on) {
        if (on) start();
        else stop();
      }
    };

    start();
  })();


  /* —— SpecularButton 2D rim (vanilla stand-in for OGL SpecularButton) —— */
  (function initSpecularButton() {
    var button = byId("loginSubmit");
    var effect = byId("loginSubmitEffect");
    if (!button || !effect) return;

    var PAD = 20;
    var canvas = document.createElement("canvas");
    var ctx = canvas.getContext("2d");
    effect.appendChild(canvas);
    if (!ctx) return;
    cleanups.push(() => canvas.remove());

    var size = { width: 1, height: 1 };
    var dpr = 1;
    var angle = 2.4;
    var idleAngle = 2.4;
    var brightness = 0;
    var pointerAngle = null;
    var proximity = 0;
    var raf = 0;
    var lastFrame = performance.now();
    var active = true;
    var PROXIMITY = 32;
    var INTENSITY = 1.35;
    var THICKNESS = 1.2;
    var RADIUS = 12;
    var LINE = [1, 1, 1];
    var BASE = [1, 1, 1]; // neutral specular rim

    function motionOn() {
      return document.documentElement.dataset.motion !== "reduced" && !window.matchMedia("(prefers-reduced-motion: reduce)").matches;
    }

    function resize() {
      var rect = button.getBoundingClientRect();
      size.width = Math.max(1, rect.width);
      size.height = Math.max(1, rect.height);
      dpr = Math.min(window.devicePixelRatio || 1, 2);
      canvas.width = Math.floor((size.width + PAD * 2) * dpr);
      canvas.height = Math.floor((size.height + PAD * 2) * dpr);
      canvas.style.width = size.width + PAD * 2 + "px";
      canvas.style.height = size.height + PAD * 2 + "px";
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    }

    function roundRectPath(x, y, w, h, r) {
      var rr = Math.min(r, w / 2, h / 2);
      ctx.beginPath();
      ctx.moveTo(x + rr, y);
      ctx.arcTo(x + w, y, x + w, y + h, rr);
      ctx.arcTo(x + w, y + h, x, y + h, rr);
      ctx.arcTo(x, y + h, x, y, rr);
      ctx.arcTo(x, y, x + w, y, rr);
      ctx.closePath();
    }

    function paint() {
      var w = size.width + PAD * 2;
      var h = size.height + PAD * 2;
      ctx.clearRect(0, 0, w, h);
      if (brightness < 0.02) return;

      var cx = PAD + size.width / 2;
      var cy = PAD + size.height / 2;
      var lx = Math.cos(angle);
      var ly = Math.sin(angle);
      // soft base rim
      ctx.save();
      roundRectPath(PAD, PAD, size.width, size.height, RADIUS);
      ctx.strokeStyle = "rgba(" + Math.round(BASE[0] * 255) + "," + Math.round(BASE[1] * 255) + "," + Math.round(BASE[2] * 255) + "," + (0.22 * brightness) + ")";
      ctx.lineWidth = 1.5;
      ctx.stroke();

      // specular hotspot along edge toward light
      var hx = cx + lx * (size.width * 0.42);
      var hy = cy - ly * (size.height * 0.42);
      var grad = ctx.createRadialGradient(hx, hy, 0, hx, hy, Math.max(size.width, size.height) * 0.55);
      grad.addColorStop(0, "rgba(255,255,255," + (0.55 * brightness * INTENSITY) + ")");
      grad.addColorStop(0.35, "rgba(255,255,255," + (0.18 * brightness) + ")");
      grad.addColorStop(1, "rgba(255,255,255,0)");

      ctx.save();
      roundRectPath(PAD, PAD, size.width, size.height, RADIUS);
      ctx.clip();
      // only keep a thin ring via destination-in after drawing stroke
      ctx.globalCompositeOperation = "source-over";
      ctx.lineWidth = THICKNESS + 2.2;
      ctx.strokeStyle = grad;
      ctx.stroke();
      // inner crisp white tick
      ctx.globalCompositeOperation = "lighter";
      ctx.lineWidth = THICKNESS;
      ctx.strokeStyle = "rgba(255,255,255," + (0.42 * brightness * INTENSITY) + ")";
      ctx.stroke();
      ctx.restore();
      ctx.restore();
    }

    function update(now) {
      if (!active) {
        raf = 0;
        return;
      }
      raf = requestAnimationFrame(update);
      if (!motionOn()) {
        brightness = 0.35;
        angle = 2.4;
        paint();
        cancelAnimationFrame(raf);
        raf = 0;
        return;
      }
      var delta = Math.min((now - lastFrame) / 1000, 0.05);
      lastFrame = now;
      idleAngle += 0.35 * delta;
      var follows = pointerAngle !== null && proximity > 0;
      var targetAngle = follows ? pointerAngle : idleAngle;
      var difference = ((targetAngle - angle + Math.PI * 3) % (Math.PI * 2)) - Math.PI;
      angle += difference * (1 - Math.exp(-delta * 7));
      var targetBrightness = Math.max(0.22, proximity);
      brightness += (targetBrightness - brightness) * (1 - Math.exp(-delta * 8));
      paint();
    }

    function onPointerMove(event) {
      var rect = button.getBoundingClientRect();
      var centerX = rect.left + rect.width / 2;
      var centerY = rect.top + rect.height / 2;
      var dx = Math.max(rect.left - event.clientX, 0, event.clientX - rect.right);
      var dy = Math.max(rect.top - event.clientY, 0, event.clientY - rect.bottom);
      var distance = Math.hypot(dx, dy);
      if (distance === 0) {
        var nx = (event.clientX - centerX) / (rect.width / 2 || 1);
        var ny = (centerY - event.clientY) / (rect.height / 2 || 1);
        pointerAngle = Math.atan2(2 / rect.height, -2 / rect.width) + nx * 0.3 + ny * 0.15;
      } else {
        pointerAngle = Math.atan2(centerY - event.clientY, event.clientX - centerX);
      }
      var t = Math.max(0, 1 - distance / Math.max(PROXIMITY, 1));
      proximity = t * t * (3 - 2 * t);
      if (!raf && motionOn() && document.documentElement.getAttribute("data-auth") !== "in") {
        lastFrame = performance.now();
        raf = requestAnimationFrame(update);
      }
    }

    function start() {
      active = true;
      resize();
      if (!raf) {
        lastFrame = performance.now();
        raf = requestAnimationFrame(update);
      }
    }

    function stop() {
      active = false;
      if (raf) cancelAnimationFrame(raf);
      raf = 0;
      ctx.clearRect(0, 0, canvas.width, canvas.height);
    }

    listen(window, "pointermove", onPointerMove, { passive: true });
    listen(window, "resize", function () {
      if (!active) return;
      resize();
      paint();
    }, { passive: true });
    if (typeof ResizeObserver !== "undefined") {
      new ResizeObserver(function () {
        if (!active) return;
        resize();
        paint();
      }).observe(button);
    }

    var prev = shared.fx && shared.fx.setActive;
    shared.fx = shared.fx || {};
    shared.fx.setActive = function (on) {
      if (typeof prev === "function") prev(on);
      if (on) start();
      else stop();
    };

    start();
  })();


  return () => { shared.fx?.setActive(false); cleanups.reverse().forEach(cleanup => cleanup()); };
}
