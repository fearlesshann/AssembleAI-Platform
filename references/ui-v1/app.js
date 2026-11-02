(function () {
  /* —— Auth gate (demo login ↔ shell) —— */
  (function initAuthGate() {
    var AUTH_LOCAL = "mfg-ai-auth";
    var AUTH_SESSION = "mfg-ai-auth-session";
    var body = document.body;
    var loginScreen = document.getElementById("loginScreen");
    var appShell = document.getElementById("appShell");
    var form = document.getElementById("loginForm");
    var accountInput = document.getElementById("loginAccount");
    var passwordInput = document.getElementById("loginPassword");
    var rememberInput = document.getElementById("loginRemember");
    var formError = document.getElementById("loginFormError");
    var forgot = document.getElementById("loginForgot");
    var userMenu = document.getElementById("userMenu");
    var userMenuBtn = document.getElementById("userMenuBtn");
    var userMenuPop = document.getElementById("userMenuPop");
    var logoutBtn = document.getElementById("logoutBtn");

    function motionOn() {
      return document.documentElement.dataset.motion !== "reduced";
    }

    function authMotionMode() {
      var mode = document.documentElement.dataset.motion || "system";
      if (mode === "reduced") return "reduced";
      if (mode === "system") {
        try {
          if (window.matchMedia && window.matchMedia("(prefers-reduced-motion: reduce)").matches) {
            return "reduced";
          }
        } catch (e) {}
      }
      return "full";
    }

    function readAuthed() {
      try {
        if (localStorage.getItem(AUTH_LOCAL) === "1") return true;
        if (sessionStorage.getItem(AUTH_SESSION) === "1") return true;
      } catch (e) {}
      return false;
    }

    function writeAuthed(remember) {
      try {
        if (remember) {
          localStorage.setItem(AUTH_LOCAL, "1");
          sessionStorage.removeItem(AUTH_SESSION);
        } else {
          sessionStorage.setItem(AUTH_SESSION, "1");
          localStorage.removeItem(AUTH_LOCAL);
        }
      } catch (e) {}
    }

    function clearAuthed() {
      try {
        localStorage.removeItem(AUTH_LOCAL);
        sessionStorage.removeItem(AUTH_SESSION);
      } catch (e) {}
    }

    function clearFieldErrors() {
      if (!form) return;
      form.querySelectorAll(".login-input.is-invalid").forEach(function (el) {
        el.classList.remove("is-invalid");
      });
      form.querySelectorAll(".login-field-error").forEach(function (el) {
        el.hidden = true;
      });
      if (formError) {
        formError.hidden = true;
        formError.textContent = "";
      }
    }

    function showFieldError(name, message) {
      if (!form) return;
      var input = form.querySelector('[name="' + name + '"]');
      var err = form.querySelector('[data-error-for="' + name + '"]');
      if (input) input.classList.add("is-invalid");
      if (err) {
        err.textContent = message || err.textContent;
        err.hidden = false;
      }
    }

    var authTransitTimer = 0;
    var authBusy = false;

    function clearAuthTransitClasses() {
      try {
        document.documentElement.classList.remove("is-auth-leaving");
      } catch (e) {}
      if (body) body.classList.remove("is-auth-leaving");
      if (loginScreen) {
        loginScreen.classList.remove("is-exit", "is-exit-fade");
      }
      if (appShell) appShell.classList.remove("is-shell-enter", "is-shell-enter-fade");
    }

    function setLoggedIn(authed, opts) {
      opts = opts || {};
      if (!body) return;
      if (authTransitTimer) {
        clearTimeout(authTransitTimer);
        authTransitTimer = 0;
      }
      clearAuthTransitClasses();
      authBusy = false;
      body.classList.toggle("is-logged-in", !!authed);
      body.classList.toggle("is-logged-out", !authed);
      try {
        document.documentElement.setAttribute("data-auth", authed ? "in" : "out");
      } catch (err) {}
      if (loginScreen) {
        loginScreen.hidden = !!authed;
        loginScreen.classList.toggle("is-enter", !authed && !!opts.animate && motionOn());
        if (!authed) {
          requestAnimationFrame(function () {
            loginScreen.classList.add("is-ready");
          });
        } else {
          loginScreen.classList.remove("is-enter", "is-ready");
        }
      }
      if (appShell) appShell.hidden = !authed;
      if (window.__loginFx && window.__loginFx.setActive) {
        window.__loginFx.setActive(!authed);
      }
      if (userMenu) userMenu.classList.remove("is-open");
      if (userMenuBtn) userMenuBtn.setAttribute("aria-expanded", "false");
      if (userMenuPop) userMenuPop.hidden = true;
      if (!authed && accountInput) {
        clearFieldErrors();
        if (opts.focus !== false) {
          requestAnimationFrame(function () {
            accountInput.focus();
          });
        }
      }
    }

    function enterShellWithTransition() {
      if (authBusy) return;
      authBusy = true;
      var reduced = authMotionMode() === "reduced";
      var duration = reduced ? 160 : 340;
      var root = document.documentElement;
      root.classList.add("is-auth-leaving");
      if (body) body.classList.add("is-auth-leaving");
      if (loginScreen) {
        loginScreen.hidden = false;
        loginScreen.classList.remove("is-enter");
        loginScreen.classList.add(reduced ? "is-exit-fade" : "is-exit");
      }
      if (appShell) {
        appShell.hidden = false;
        appShell.classList.add(reduced ? "is-shell-enter-fade" : "is-shell-enter");
      }
      if (window.__loginFx && window.__loginFx.setActive) {
        window.__loginFx.setActive(false);
      }
      var submitBtn = document.getElementById("loginSubmit");
      if (submitBtn) submitBtn.disabled = true;

      var finished = false;
      function finish() {
        if (finished) return;
        finished = true;
        if (authTransitTimer) {
          clearTimeout(authTransitTimer);
          authTransitTimer = 0;
        }
        setLoggedIn(true);
        if (submitBtn) submitBtn.disabled = false;
        if (window.__showToast) window.__showToast("已登录", "success");
      }

      authTransitTimer = setTimeout(finish, duration + 40);
      if (loginScreen) {
        loginScreen.addEventListener("animationend", function onExitEnd(ev) {
          if (ev.target !== loginScreen) return;
          loginScreen.removeEventListener("animationend", onExitEnd);
          finish();
        });
      }
    }

    function closeUserMenu() {
      if (!userMenu) return;
      userMenu.classList.remove("is-open");
      if (userMenuBtn) userMenuBtn.setAttribute("aria-expanded", "false");
      if (userMenuPop) userMenuPop.hidden = true;
    }

    function openUserMenu() {
      if (!userMenu || !userMenuPop) return;
      userMenuPop.hidden = false;
      void userMenuPop.offsetWidth;
      userMenu.classList.add("is-open");
      if (userMenuBtn) userMenuBtn.setAttribute("aria-expanded", "true");
    }

    setLoggedIn(readAuthed(), { focus: false, animate: !readAuthed() });

    if (form) {
      form.addEventListener("submit", function (e) {
        e.preventDefault();
        clearFieldErrors();
        var account = (accountInput && accountInput.value.trim()) || "";
        var password = (passwordInput && passwordInput.value) || "";
        var ok = true;
        if (!account) {
          showFieldError("account", "请输入账号或邮箱");
          ok = false;
        }
        if (!password) {
          showFieldError("password", "请输入密码");
          ok = false;
        }
        if (!ok) {
          if (formError) {
            formError.textContent = "请完善登录信息";
            formError.hidden = false;
          }
          var firstInvalid = form.querySelector(".login-input.is-invalid");
          if (firstInvalid) firstInvalid.focus();
          return;
        }
        if (authBusy) return;
        writeAuthed(!!(rememberInput && rememberInput.checked));
        if (passwordInput) passwordInput.value = "";
        enterShellWithTransition();
      });

      ["input", "change"].forEach(function (evt) {
        form.addEventListener(evt, function (e) {
          var t = e.target;
          if (!t || !t.classList || !t.classList.contains("login-input")) return;
          t.classList.remove("is-invalid");
          var name = t.getAttribute("name");
          var err = name && form.querySelector('[data-error-for="' + name + '"]');
          if (err) err.hidden = true;
          if (formError) {
            formError.hidden = true;
            formError.textContent = "";
          }
        });
      });
    }

    if (forgot) {
      forgot.addEventListener("click", function (e) {
        e.preventDefault();
        if (window.__showToast) window.__showToast("演示样例：请联系管理员重置密码", "info");
      });
    }

    if (userMenuBtn) {
      userMenuBtn.addEventListener("click", function (e) {
        e.preventDefault();
        e.stopPropagation();
        if (userMenu && userMenu.classList.contains("is-open")) closeUserMenu();
        else openUserMenu();
      });
    }

    if (logoutBtn) {
      logoutBtn.addEventListener("click", function (e) {
        e.preventDefault();
        clearAuthed();
        closeUserMenu();
        setLoggedIn(false, { animate: true });
        if (window.__showToast) window.__showToast("已退出登录", "info");
      });
    }

    document.addEventListener("click", function (e) {
      if (!userMenu || !userMenu.classList.contains("is-open")) return;
      if (userMenu.contains(e.target)) return;
      closeUserMenu();
    });

    document.addEventListener("keydown", function (e) {
      if (e.key === "Escape") closeUserMenu();
    });

    window.__mfgAuth = {
      isAuthed: readAuthed,
      logout: function () {
        clearAuthed();
        setLoggedIn(false, { animate: true });
      }
    };
  })();


  /* —— Login FX: LoginWaves-style contours + soft ether haze —— */
  (function initLoginFx() {
    var screen = document.getElementById("loginScreen");
    var wavesHost = document.getElementById("loginWaves");
    var wavesCanvas = document.getElementById("loginWavesCanvas");
    var etherCanvas = document.getElementById("loginEther");
    if (!screen || !wavesCanvas) return;

    var wavesCtx = wavesCanvas.getContext("2d");
    var etherCtx = etherCanvas ? etherCanvas.getContext("2d") : null;
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
      return document.documentElement.dataset.motion !== "reduced";
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

    window.addEventListener("mousemove", function (e) {
      if (!active) return;
      updateMouse(e.clientX, e.clientY);
    }, { passive: true });
    window.addEventListener("touchmove", function (e) {
      if (!active || !e.touches || !e.touches[0]) return;
      updateMouse(e.touches[0].clientX, e.touches[0].clientY);
    }, { passive: true });
    window.addEventListener("resize", function () {
      if (!active) return;
      resizeAll();
    }, { passive: true });
    document.addEventListener("visibilitychange", function () {
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

    window.__loginFx = {
      setActive: function (on) {
        if (on) start();
        else stop();
      }
    };

    if (document.documentElement.getAttribute("data-auth") !== "in") start();
  })();


  /* —— SpecularButton 2D rim (vanilla stand-in for OGL SpecularButton) —— */
  (function initSpecularButton() {
    var button = document.getElementById("loginSubmit");
    var effect = document.getElementById("loginSubmitEffect");
    if (!button || !effect) return;

    var PAD = 20;
    var canvas = document.createElement("canvas");
    var ctx = canvas.getContext("2d");
    effect.appendChild(canvas);

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
      return document.documentElement.dataset.motion !== "reduced";
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

    window.addEventListener("pointermove", onPointerMove, { passive: true });
    window.addEventListener("resize", function () {
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

    var prev = window.__loginFx && window.__loginFx.setActive;
    window.__loginFx = window.__loginFx || {};
    window.__loginFx.setActive = function (on) {
      if (typeof prev === "function") prev(on);
      if (on) start();
      else stop();
    };

    if (document.documentElement.getAttribute("data-auth") !== "in") start();
  })();

  var navItems = document.querySelectorAll(".nav-item[data-view]");
  var views = document.querySelectorAll(".view");
  var breadcrumb = document.getElementById("breadcrumb");
  var appSearch = document.getElementById("appSearch");
  var appRows = document.querySelectorAll("#appList .app-row");
  var filterChips = document.querySelectorAll(".filter-chip");

  var crumbs = {
    issue: { id: "MFG-2048", title: "A2 回流焊温漂排查", status: true },
    apps: { id: "应用", title: "应用中心", status: false },
    admin: { id: "管理", title: "成员与权限", status: false }
  };

  function setBreadcrumb(name) {
    var c = crumbs[name] || crumbs.issue;
    if (!breadcrumb) return;
    breadcrumb.innerHTML =
      (c.status ? '<span class="crumb-status" aria-hidden="true"></span>' : "") +
      '<span class="crumb-id">' + c.id + "</span>" +
      '<span class="crumb-title">' + c.title + "</span>";
  }

  function prefersMotion() {
    return document.documentElement.dataset.motion !== "reduced";
  }

  function switchView(name) {
    var next = null;
    navItems.forEach(function (item) {
      item.classList.toggle("is-active", item.getAttribute("data-view") === name);
    });
    views.forEach(function (view) {
      var on = view.getAttribute("data-view") === name;
      view.classList.toggle("is-active", on);
      view.classList.remove("is-enter");
      if (on) next = view;
    });
    setBreadcrumb(name);
    if (next && prefersMotion()) {
      requestAnimationFrame(function () {
        next.classList.add("is-enter");
      });
    }
  }

  navItems.forEach(function (item) {
    item.addEventListener("click", function () {
      switchView(item.getAttribute("data-view"));
    });
  });

  var activeFilter = "全部";
  filterChips.forEach(function (chip) {
    chip.addEventListener("click", function () {
      filterChips.forEach(function (c) {
        c.classList.toggle("is-active", c === chip);
      });
      activeFilter = chip.getAttribute("data-filter") || chip.textContent.trim();
      applyAppFilters();
    });
  });

  if (appSearch) {
    appSearch.addEventListener("input", applyAppFilters);
  }

  function applyAppFilters() {
    var q = (appSearch && appSearch.value.trim().toLowerCase()) || "";
    var visible = 0;
    appRows.forEach(function (row) {
      var name = (row.getAttribute("data-name") || "").toLowerCase();
      var status = row.getAttribute("data-status");
      var matchQ = !q || name.indexOf(q) !== -1 || row.textContent.toLowerCase().indexOf(q) !== -1;
      var matchS =
        activeFilter === "全部" ||
        activeFilter === status ||
        (activeFilter === "运行中" && status === "running") ||
        (activeFilter === "试运行" && status === "trial") ||
        (activeFilter === "草稿" && status === "draft");
      var show = matchQ && matchS;
      row.style.display = show ? "" : "none";
      if (show) visible += 1;
    });
    var list = document.getElementById("appList");
    var empty = document.getElementById("appEmpty");
    if (list) list.classList.toggle("is-empty", visible === 0);
    if (empty) {
      empty.hidden = visible !== 0;
      empty.classList.toggle("is-visible", visible === 0);
    }
  }

  var composer = document.getElementById("composerInput");
  if (composer) {
    composer.addEventListener("keydown", function (e) {
      if (e.key === "Enter" && (e.metaKey || e.ctrlKey)) {
        e.preventDefault();
        composer.value = "";
      }
    });
  }






  /* —— Custom select —— */
  (function initUiSelect() {
    var openRoot = null;

    function motionOn() {
      return document.documentElement.dataset.motion !== "reduced";
    }

    function getValue(root) {
      return root ? (root.getAttribute("data-value") || "") : "";
    }

    function setValue(root, value, silent) {
      if (!root) return;
      var menu = root.querySelector(".ui-select__menu");
      var label = root.querySelector(".ui-select__value");
      var options = root.querySelectorAll(".ui-select__option");
      var matched = null;
      options.forEach(function (opt) {
        var on = opt.getAttribute("data-value") === value;
        opt.classList.toggle("is-selected", on);
        opt.setAttribute("aria-selected", on ? "true" : "false");
        if (on) matched = opt;
      });
      if (!matched && value && menu) {
        var btn = document.createElement("button");
        btn.type = "button";
        btn.className = "ui-select__option is-selected";
        btn.setAttribute("role", "option");
        btn.setAttribute("data-value", value);
        btn.setAttribute("aria-selected", "true");
        btn.textContent = value;
        menu.appendChild(btn);
        matched = btn;
      }
      root.setAttribute("data-value", value || "");
      if (label) label.textContent = matched ? matched.textContent : (value || "");
      if (!silent) {
        root.dispatchEvent(new CustomEvent("ui-change", { bubbles: true, detail: { value: value || "" } }));
      }
    }

    function close(root) {
      if (!root) return;
      var menu = root.querySelector(".ui-select__menu");
      var trigger = root.querySelector(".ui-select__trigger");
      root.classList.remove("is-open");
      if (trigger) trigger.setAttribute("aria-expanded", "false");
      if (menu) {
        if (motionOn()) {
          var t = setTimeout(function () {
            if (!root.classList.contains("is-open")) menu.hidden = true;
          }, 160);
          root._uiCloseTimer = t;
        } else {
          menu.hidden = true;
        }
      }
      if (openRoot === root) openRoot = null;
    }

    function open(root) {
      if (!root) return;
      if (openRoot && openRoot !== root) close(openRoot);
      if (window.__uiDateTime && window.__uiDateTime.closeAll) window.__uiDateTime.closeAll();
      if (window.__uiDateRange && window.__uiDateRange.closeAll) window.__uiDateRange.closeAll();
      if (root._uiCloseTimer) {
        clearTimeout(root._uiCloseTimer);
        root._uiCloseTimer = null;
      }
      var menu = root.querySelector(".ui-select__menu");
      var trigger = root.querySelector(".ui-select__trigger");
      if (menu) {
        menu.hidden = false;
        void menu.offsetWidth;
      }
      root.classList.add("is-open");
      if (trigger) trigger.setAttribute("aria-expanded", "true");
      openRoot = root;
    }

    function toggle(root) {
      if (root.classList.contains("is-open")) close(root);
      else open(root);
    }

    document.addEventListener("click", function (e) {
      var trigger = e.target.closest(".ui-select__trigger");
      if (trigger) {
        var root = trigger.closest("[data-ui-select]");
        if (!root) return;
        e.preventDefault();
        toggle(root);
        return;
      }
      var option = e.target.closest(".ui-select__option");
      if (option) {
        var root = option.closest("[data-ui-select]");
        if (!root) return;
        e.preventDefault();
        setValue(root, option.getAttribute("data-value") || "");
        close(root);
        return;
      }
      if (openRoot && !e.target.closest("[data-ui-select]")) close(openRoot);
    });

    document.addEventListener("keydown", function (e) {
      if (!openRoot) return;
      if (e.key === "Escape") {
        close(openRoot);
        return;
      }
      if (e.key === "ArrowDown" || e.key === "ArrowUp") {
        e.preventDefault();
        var opts = Array.prototype.slice.call(openRoot.querySelectorAll(".ui-select__option"));
        if (!opts.length) return;
        var idx = opts.findIndex(function (o) { return o.classList.contains("is-active") || o.classList.contains("is-selected"); });
        opts.forEach(function (o) { o.classList.remove("is-active"); });
        if (e.key === "ArrowDown") idx = Math.min(opts.length - 1, (idx < 0 ? -1 : idx) + 1);
        else idx = Math.max(0, (idx < 0 ? opts.length : idx) - 1);
        opts[idx].classList.add("is-active");
        opts[idx].scrollIntoView({ block: "nearest" });
      }
      if (e.key === "Enter") {
        var active = openRoot.querySelector(".ui-select__option.is-active") || openRoot.querySelector(".ui-select__option.is-selected");
        if (active) {
          e.preventDefault();
          setValue(openRoot, active.getAttribute("data-value") || "");
          close(openRoot);
        }
      }
    });

    window.__uiSelect = {
      get: getValue,
      set: function (root, value) { setValue(root, value, true); },
      closeAll: function () { if (openRoot) close(openRoot); }
    };
  })();

  /* —— Custom datetime (point-in-time) & daterange —— */
  (function initUiDateControls() {
    var WEEKDAYS = ["一", "二", "三", "四", "五", "六", "日"];
    var MONTHS = ["1月", "2月", "3月", "4月", "5月", "6月", "7月", "8月", "9月", "10月", "11月", "12月"];
    var openDt = null;
    var openDr = null;

    function motionOn() {
      return document.documentElement.dataset.motion !== "reduced";
    }

    function pad2(n) {
      return (n < 10 ? "0" : "") + n;
    }

    function toDateOnly(d) {
      return new Date(d.getFullYear(), d.getMonth(), d.getDate());
    }

    function parseDateTime(value) {
      if (value == null || value === "") return null;
      if (value instanceof Date && !isNaN(value.getTime())) {
        return new Date(value.getFullYear(), value.getMonth(), value.getDate(), value.getHours(), value.getMinutes(), 0, 0);
      }
      var s = String(value).trim().replace(" ", "T");
      var m = s.match(/^(\d{4})-(\d{2})-(\d{2})(?:T(\d{2}):(\d{2})(?::\d{2})?)?/);
      if (!m) {
        var d = new Date(s);
        if (isNaN(d.getTime())) return null;
        return new Date(d.getFullYear(), d.getMonth(), d.getDate(), d.getHours(), d.getMinutes(), 0, 0);
      }
      return new Date(+m[1], +m[2] - 1, +m[3], m[4] != null ? +m[4] : 0, m[5] != null ? +m[5] : 0, 0, 0);
    }

    function parseDateOnly(value) {
      if (value == null || value === "") return null;
      if (value instanceof Date && !isNaN(value.getTime())) return toDateOnly(value);
      var s = String(value).trim();
      var m = s.match(/^(\d{4})-(\d{2})-(\d{2})/);
      if (!m) {
        var d = new Date(s);
        if (isNaN(d.getTime())) return null;
        return toDateOnly(d);
      }
      return new Date(+m[1], +m[2] - 1, +m[3]);
    }

    function fmtDate(d) {
      if (!d) return "";
      return d.getFullYear() + "-" + pad2(d.getMonth() + 1) + "-" + pad2(d.getDate());
    }

    function fmtDateTimeDisplay(d) {
      if (!d) return "";
      return fmtDate(d) + " " + pad2(d.getHours()) + ":" + pad2(d.getMinutes());
    }

    function fmtDateTimeValue(d) {
      if (!d) return "";
      return fmtDate(d) + "T" + pad2(d.getHours()) + ":" + pad2(d.getMinutes());
    }

    function sameDay(a, b) {
      return a && b && a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate();
    }

    function dayKey(d) {
      return fmtDate(d);
    }

    function decadeBase(year) {
      return Math.floor(year / 10) * 10;
    }

    function positionPanel(panel, trigger) {
      if (!panel || !trigger) return;
      var rect = trigger.getBoundingClientRect();
      var w = panel.offsetWidth || 280;
      var h = panel.offsetHeight || 320;
      var top = rect.bottom + 4;
      var left = rect.left;
      if (top + h > window.innerHeight - 8) {
        top = Math.max(8, rect.top - h - 4);
      }
      if (left + w > window.innerWidth - 8) {
        left = Math.max(8, window.innerWidth - w - 8);
      }
      if (left < 8) left = 8;
      if (top < 8) top = 8;
      panel.style.top = Math.round(top) + "px";
      panel.style.left = Math.round(left) + "px";
      panel.style.width = "280px";
    }

    var WHEEL_ITEM_H = 32;
    // Feel params aligned with mature mobile pickers (Vant PickerColumn /
    // NutUI PickerView / better-scroll wheel) — zero new deps, hand-ported.
    var WHEEL_MOMENTUM_MS = 300;       // Vant MOMENTUM_TIME / BS momentumLimitTime
    var WHEEL_MOMENTUM_DIST = 15;      // Vant MOMENTUM_DISTANCE / BS momentumLimitDistance
    var WHEEL_DECEL = 0.003;           // Vant & NutUI: destination += speed / 0.003
    var WHEEL_SNAP_MS = 200;           // Vant DEFAULT_DURATION
    var WHEEL_SWIPE_MS = 900;          // ~Vant swipeDuration (no bounce)
    var WHEEL_CLICK_MS = 220;          // short settle on tap
    var WHEEL_WHEEL_SNAP_MS = 140;     // idle after last wheel event before settle
    var WHEEL_NOTCH_ITEMS = 0.55;      // mouse notch energy ≈ fraction of a row
    var WHEEL_FOLLOW_COARSE = 0.2;     // immediate offset fraction (mouse)
    var WHEEL_FOLLOW_FINE = 0.5;       // immediate offset fraction (trackpad)
    var WHEEL_TRACK_MAX_V = 1.35;      // px/ms coast cap
    var WHEEL_TRACK_DAMP = 210;        // exponential coast τ (ms) — longer, smoother
    var WHEEL_TRACK_STOP = 0.014;      // then snap
    var WHEEL_EASE = "cubic-bezier(0.23, 1, 0.32, 1)"; // ease-out, no overshoot

    function buildWheelHtml(kind, count, label) {
      var items = "";
      for (var i = 0; i < count; i++) {
        items +=
          '<button type="button" class="ui-wheel__item" role="option" data-wheel-val="' + i + '" tabindex="-1">' +
          pad2(i) +
          "</button>";
      }
      return (
        '<div class="ui-wheel" data-wheel="' + kind + '">' +
          '<div class="ui-wheel__highlight" aria-hidden="true"></div>' +
          '<div class="ui-wheel__viewport" data-wheel-scroll role="listbox" aria-label="' + label + '" tabindex="0">' +
            '<div class="ui-wheel__track">' +
              items +
            "</div>" +
          "</div>" +
        "</div>"
      );
    }

    function wheelItemHeight(vp) {
      var item = vp.querySelector(".ui-wheel__item");
      return item && item.offsetHeight ? item.offsetHeight : WHEEL_ITEM_H;
    }

    function wheelTrack(vp) {
      return vp._uiWheelTrack || (vp._uiWheelTrack = vp.querySelector(".ui-wheel__track"));
    }

    function wheelBaseOffset(vp) {
      var h = wheelItemHeight(vp);
      return (vp.clientHeight - h) / 2;
    }

    function clampWheelOffset(vp, offset, max) {
      var h = wheelItemHeight(vp);
      // Soft clamp like Vant (allow one item past ends while dragging; settle snaps back).
      var minO = -max * h;
      var maxO = 0;
      if (offset < minO - h) return minO - h;
      if (offset > maxO + h) return maxO + h;
      return offset;
    }

    function indexFromOffset(offset, h, max) {
      if (h <= 0) return 0;
      var idx = Math.round(-offset / h);
      if (idx < 0) return 0;
      if (idx > max) return max;
      return idx;
    }

    function markWheelSelected(vp, idx) {
      var items = vp.querySelectorAll(".ui-wheel__item");
      for (var i = 0; i < items.length; i++) {
        items[i].classList.toggle("is-selected", i === idx);
        items[i].setAttribute("aria-selected", i === idx ? "true" : "false");
      }
    }

    function applyWheelTransform(vp, offset, durationMs) {
      var track = wheelTrack(vp);
      if (!track) return;
      vp._uiWheelOffset = offset;
      var dur = durationMs > 0 && motionOn() ? durationMs : 0;
      track.style.transitionDuration = dur + "ms";
      track.style.transitionTimingFunction = WHEEL_EASE;
      track.style.transform =
        "translate3d(0, " + (offset + wheelBaseOffset(vp)) + "px, 0)";
    }

    function stopWheelAnim(vp) {
      if (vp._uiWheelRaf != null) {
        cancelAnimationFrame(vp._uiWheelRaf);
        vp._uiWheelRaf = null;
      }
      vp._uiWheelVel = 0;
      vp._uiWheelInertiaLast = 0;
      clearTimeout(vp._uiWheelSnapTimer);
      vp._uiWheelSnapTimer = null;
      // Capture mid-transition offset so a new drag starts from the visual position.
      var track = wheelTrack(vp);
      if (track && vp._uiWheelMoving) {
        var cs = window.getComputedStyle(track);
        var t = cs.transform;
        if (t && t !== "none") {
          var m = t.match(/matrix\(([^)]+)\)/);
          if (m) {
            var parts = m[1].split(",");
            var ty = parseFloat(parts[5]);
            if (!isNaN(ty)) vp._uiWheelOffset = ty - wheelBaseOffset(vp);
          }
        }
        track.style.transitionDuration = "0ms";
        applyWheelTransform(vp, vp._uiWheelOffset || 0, 0);
      }
      vp._uiWheelMoving = false;
      if (vp._uiWheelTransHandler) {
        track && track.removeEventListener("transitionend", vp._uiWheelTransHandler);
        vp._uiWheelTransHandler = null;
      }
    }

    function scrollWheelTo(vp, idx, smooth) {
      var kindMax = vp._uiWheelMax != null ? vp._uiWheelMax : 59;
      idx = Math.max(0, Math.min(kindMax, idx | 0));
      var h = wheelItemHeight(vp);
      var offset = -idx * h;
      stopWheelAnim(vp);
      var dur = smooth && motionOn() ? WHEEL_CLICK_MS : 0;
      if (dur) vp._uiWheelMoving = true;
      applyWheelTransform(vp, offset, dur);
      markWheelSelected(vp, idx);
      if (dur) {
        var track = wheelTrack(vp);
        var done = function (e) {
          if (e && e.target !== track) return;
          if (e && e.propertyName && e.propertyName !== "transform") return;
          vp._uiWheelMoving = false;
          if (track && vp._uiWheelTransHandler) {
            track.removeEventListener("transitionend", vp._uiWheelTransHandler);
            vp._uiWheelTransHandler = null;
          }
        };
        vp._uiWheelTransHandler = done;
        if (track) track.addEventListener("transitionend", done);
        clearTimeout(vp._uiWheelUnlock);
        vp._uiWheelUnlock = setTimeout(done, dur + 40);
      }
    }

    function syncWheelsToState(panel, state) {
      if (!panel) return;
      var h = state.value ? state.value.getHours() : 0;
      var m = state.value ? state.value.getMinutes() : 0;
      var hourVp = panel.querySelector('[data-wheel="hour"] [data-wheel-scroll]');
      var minVp = panel.querySelector('[data-wheel="minute"] [data-wheel-scroll]');
      if (hourVp) scrollWheelTo(hourVp, h, false);
      if (minVp) scrollWheelTo(minVp, m, false);
    }

    function commitWheelValue(root, kind, idx) {
      var state = getDtState(root);
      var base = ensureDtBaseDate(state);
      if (kind === "hour") base.setHours(idx);
      else base.setMinutes(idx);
      setDtValue(root, base, false, true);
    }

    function bindTimeWheels(root, panel) {
      if (!panel || panel._uiWheelsBound) return;
      panel._uiWheelsBound = true;
      var vps = panel.querySelectorAll("[data-wheel-scroll]");
      for (var i = 0; i < vps.length; i++) {
        (function (vp) {
          var wheel = vp.closest("[data-wheel]");
          if (!wheel) return;
          var kind = wheel.getAttribute("data-wheel");
          var max = kind === "hour" ? 23 : 59;
          vp._uiWheelMax = max;
          vp._uiWheelOffset = 0;
          vp._uiWheelVel = 0;
          var drag = null;
          var didDrag = false;
          var skipNextClick = false;
          var DRAG_THRESHOLD = 5;
          var samples = [];

          function currentIndex() {
            return indexFromOffset(vp._uiWheelOffset || 0, wheelItemHeight(vp), max);
          }

          function setOffsetLive(offset) {
            offset = clampWheelOffset(vp, offset, max);
            vp._uiWheelMoving = false;
            applyWheelTransform(vp, offset, 0);
            markWheelSelected(vp, indexFromOffset(offset, wheelItemHeight(vp), max));
          }

          function finishToIndex(idx, duration) {
            idx = Math.max(0, Math.min(max, idx));
            var h = wheelItemHeight(vp);
            var offset = -idx * h;
            vp._uiWheelMoving = duration > 0 && motionOn();
            applyWheelTransform(vp, offset, duration);
            markWheelSelected(vp, idx);
            commitWheelValue(root, kind, idx);
            if (vp._uiWheelMoving) {
              var track = wheelTrack(vp);
              var done = function (e) {
                if (e && e.target !== track) return;
                if (e && e.propertyName && e.propertyName !== "transform") return;
                vp._uiWheelMoving = false;
                if (track && vp._uiWheelTransHandler) {
                  track.removeEventListener("transitionend", vp._uiWheelTransHandler);
                  vp._uiWheelTransHandler = null;
                }
              };
              if (vp._uiWheelTransHandler && track) {
                track.removeEventListener("transitionend", vp._uiWheelTransHandler);
              }
              vp._uiWheelTransHandler = done;
              if (track) track.addEventListener("transitionend", done);
              clearTimeout(vp._uiWheelUnlock);
              vp._uiWheelUnlock = setTimeout(done, duration + 40);
            }
          }

          function snapNearest(duration) {
            finishToIndex(currentIndex(), duration == null ? WHEEL_SNAP_MS : duration);
          }

          // Vant/NutUI momentum: project by speed/0.003, then land on nearest index.
          function momentumTo(distance, duration) {
            var h = wheelItemHeight(vp);
            var speed = Math.abs(distance / Math.max(1, duration));
            var projected =
              (vp._uiWheelOffset || 0) + (speed / WHEEL_DECEL) * (distance < 0 ? -1 : 1);
            var idx = indexFromOffset(projected, h, max);
            var travel = Math.abs(idx - currentIndex());
            var dur = motionOn()
              ? Math.min(WHEEL_SWIPE_MS, Math.max(WHEEL_SNAP_MS, 180 + travel * 70))
              : 0;
            finishToIndex(idx, dur);
          }

          function scheduleWheelSnap() {
            clearTimeout(vp._uiWheelSnapTimer);
            vp._uiWheelSnapTimer = setTimeout(function () {
              vp._uiWheelSnapTimer = null;
              if (drag) return;
              // Do not kill an active friction coast — trackInertiaStep snaps when slow.
              if (vp._uiWheelRaf != null && Math.abs(vp._uiWheelVel || 0) >= WHEEL_TRACK_STOP) {
                return;
              }
              stopTrackInertia();
              snapNearest(motionOn() ? WHEEL_SNAP_MS : 0);
            }, WHEEL_WHEEL_SNAP_MS);
          }

          function stopTrackInertia() {
            if (vp._uiWheelRaf != null) {
              cancelAnimationFrame(vp._uiWheelRaf);
              vp._uiWheelRaf = null;
            }
            vp._uiWheelVel = 0;
            vp._uiWheelInertiaLast = 0;
          }

          // Cancel CSS settle only — keep coast velocity (unlike stopWheelAnim).
          function interruptCssSettle() {
            clearTimeout(vp._uiWheelSnapTimer);
            vp._uiWheelSnapTimer = null;
            if (!vp._uiWheelMoving) return;
            var track = wheelTrack(vp);
            if (track) {
              var cs = window.getComputedStyle(track);
              var t = cs.transform;
              if (t && t !== "none") {
                var m = t.match(/matrix\(([^)]+)\)/);
                if (m) {
                  var parts = m[1].split(",");
                  var ty = parseFloat(parts[5]);
                  if (!isNaN(ty)) vp._uiWheelOffset = ty - wheelBaseOffset(vp);
                }
              }
              if (vp._uiWheelTransHandler) {
                track.removeEventListener("transitionend", vp._uiWheelTransHandler);
                vp._uiWheelTransHandler = null;
              }
              track.style.transitionDuration = "0ms";
              applyWheelTransform(vp, vp._uiWheelOffset || 0, 0);
            }
            vp._uiWheelMoving = false;
          }

          function trackInertiaStep(time) {
            vp._uiWheelRaf = null;
            if (drag) {
              stopTrackInertia();
              return;
            }
            if (!motionOn()) {
              stopTrackInertia();
              snapNearest(0);
              return;
            }
            if (!vp._uiWheelInertiaLast) {
              vp._uiWheelInertiaLast = time;
              vp._uiWheelRaf = requestAnimationFrame(trackInertiaStep);
              return;
            }
            var elapsed = Math.min(32, Math.max(1, time - vp._uiWheelInertiaLast));
            vp._uiWheelInertiaLast = time;
            var next = (vp._uiWheelOffset || 0) + vp._uiWheelVel * elapsed;
            var h = wheelItemHeight(vp);
            var minO = -max * h;
            var maxO = 0;
            if (next < minO) {
              next = minO;
              vp._uiWheelVel = 0;
            } else if (next > maxO) {
              next = maxO;
              vp._uiWheelVel = 0;
            }
            setOffsetLive(next);
            vp._uiWheelVel *= Math.exp(-elapsed / WHEEL_TRACK_DAMP);
            if (Math.abs(vp._uiWheelVel) < WHEEL_TRACK_STOP) {
              stopTrackInertia();
              snapNearest(WHEEL_SNAP_MS);
              return;
            }
            vp._uiWheelRaf = requestAnimationFrame(trackInertiaStep);
          }

          function normalizeWheelDelta(e) {
            if (e.deltaMode === 1) return e.deltaY * 16;
            if (e.deltaMode === 2) return e.deltaY * vp.clientHeight * 0.85;
            return e.deltaY;
          }

          function isCoarseWheel(e) {
            if (e.deltaMode === 1 || e.deltaMode === 2) return true;
            return Math.abs(e.deltaY) >= 40;
          }

          function softenCoarseDelta(e) {
            var h = wheelItemHeight(vp) || WHEEL_ITEM_H;
            var sign = e.deltaY > 0 ? 1 : e.deltaY < 0 ? -1 : 0;
            if (!sign) return 0;
            return sign * h * WHEEL_NOTCH_ITEMS;
          }

          // Own the wheel so page inertial (capture) cannot steal.
          // Continuous offset + velocity coast (no per-tick snap / no stopWheelAnim zeroing vel).
          vp.addEventListener(
            "wheel",
            function (e) {
              if (e.ctrlKey) return;
              if (Math.abs(e.deltaX) > Math.abs(e.deltaY)) return;
              e.preventDefault();
              e.stopPropagation();

              if (drag) return;
              interruptCssSettle();

              var now = performance.now();
              var coarse = isCoarseWheel(e);
              var pixel = coarse ? softenCoarseDelta(e) : normalizeWheelDelta(e);
              // deltaY>0 → content up → offset decreases (higher index).
              var move = -pixel;

              if (!motionOn()) {
                stopTrackInertia();
                setOffsetLive((vp._uiWheelOffset || 0) + move);
                scheduleWheelSnap();
                return;
              }

              var dt = vp._uiWheelLastEvt
                ? Math.min(64, Math.max(8, now - vp._uiWheelLastEvt))
                : 16;
              vp._uiWheelLastEvt = now;

              var follow = coarse ? WHEEL_FOLLOW_COARSE : WHEEL_FOLLOW_FINE;
              setOffsetLive((vp._uiWheelOffset || 0) + move * follow);

              var boost = (move * (1 - follow)) / dt;
              if (
                vp._uiWheelVel &&
                Math.sign(vp._uiWheelVel) !== Math.sign(boost) &&
                Math.abs(boost) > 0.008
              ) {
                vp._uiWheelVel *= 0.12;
              }
              vp._uiWheelVel += boost;
              if (vp._uiWheelVel > WHEEL_TRACK_MAX_V) vp._uiWheelVel = WHEEL_TRACK_MAX_V;
              if (vp._uiWheelVel < -WHEEL_TRACK_MAX_V) vp._uiWheelVel = -WHEEL_TRACK_MAX_V;

              if (vp._uiWheelRaf == null) {
                vp._uiWheelInertiaLast = 0;
                vp._uiWheelRaf = requestAnimationFrame(trackInertiaStep);
              }
              // Idle timer only; must not abort coast (see scheduleWheelSnap).
              scheduleWheelSnap();
            },
            { passive: false }
          );

          function pushSample(y, t) {
            samples.push({ y: y, t: t });
            while (samples.length > 6) samples.shift();
            while (samples.length > 2 && t - samples[0].t > WHEEL_MOMENTUM_MS) {
              samples.shift();
            }
          }

          function sampleVelocity() {
            if (samples.length < 2) return { distance: 0, duration: 0 };
            var a = samples[0];
            var b = samples[samples.length - 1];
            return { distance: b.y - a.y, duration: Math.max(1, b.t - a.t) };
          }

          function selectItemByIndex(idx) {
            stopWheelAnim(vp);
            stopTrackInertia();
            clearTimeout(vp._uiWheelSnapTimer);
            finishToIndex(idx, motionOn() ? WHEEL_CLICK_MS : 0);
          }

          function unbindGestureDocs() {
            document.removeEventListener("pointermove", onGestureMove, true);
            document.removeEventListener("pointerup", onGestureEnd, true);
            document.removeEventListener("pointercancel", onGestureEnd, true);
          }

          function onGestureMove(e) {
            if (!drag || e.pointerId !== drag.id) return;
            var dy = e.clientY - drag.y;
            if (!didDrag && Math.abs(dy) < DRAG_THRESHOLD) return;
            if (!didDrag) {
              didDrag = true;
              try {
                vp.setPointerCapture(e.pointerId);
                drag.captured = true;
              } catch (err) {}
            }
            pushSample(e.clientY, Date.now());
            // Follow finger: list moves with pointer (Vant startOffset + deltaY).
            setOffsetLive(drag.offset + dy);
            if (e.cancelable) e.preventDefault();
          }

          function onGestureEnd(e) {
            if (!drag || (e && e.pointerId !== drag.id)) return;
            var id = drag.id;
            var item = drag.item;
            var wasDrag = didDrag;
            var captured = drag.captured;
            var isCancel = e && e.type === "pointercancel";
            drag = null;
            unbindGestureDocs();
            if (captured) {
              try {
                vp.releasePointerCapture(id);
              } catch (err) {}
            }
            if (wasDrag) {
              skipNextClick = true;
              didDrag = false;
              if (isCancel || !motionOn()) {
                snapNearest(0);
                return;
              }
              var vel = sampleVelocity();
              // Vant: flick if last 300ms window moved > 15px.
              if (
                vel.duration < WHEEL_MOMENTUM_MS &&
                Math.abs(vel.distance) > WHEEL_MOMENTUM_DIST
              ) {
                momentumTo(vel.distance, vel.duration);
              } else {
                snapNearest(WHEEL_SNAP_MS);
              }
              samples = [];
              return;
            }
            samples = [];
            if (!isCancel && item && vp.contains(item)) {
              var idx = +item.getAttribute("data-wheel-val");
              if (!isNaN(idx)) {
                selectItemByIndex(idx);
                skipNextClick = true;
              }
            }
          }

          vp.addEventListener("pointerdown", function (e) {
            if (e.pointerType === "mouse" && e.button !== 0) return;
            stopWheelAnim(vp);
            stopTrackInertia();
            clearTimeout(vp._uiWheelSnapTimer);
            didDrag = false;
            skipNextClick = false;
            samples = [];
            pushSample(e.clientY, Date.now());
            var item = e.target.closest ? e.target.closest(".ui-wheel__item") : null;
            drag = {
              id: e.pointerId,
              y: e.clientY,
              offset: vp._uiWheelOffset || 0,
              item: item && vp.contains(item) ? item : null,
              captured: false
            };
            document.addEventListener("pointermove", onGestureMove, true);
            document.addEventListener("pointerup", onGestureEnd, true);
            document.addEventListener("pointercancel", onGestureEnd, true);
          });

          vp.addEventListener("click", function (e) {
            if (skipNextClick || didDrag) {
              skipNextClick = false;
              didDrag = false;
              e.preventDefault();
              e.stopPropagation();
              return;
            }
            var item = e.target.closest(".ui-wheel__item");
            if (!item || !vp.contains(item)) return;
            e.preventDefault();
            e.stopPropagation();
            var idx = +item.getAttribute("data-wheel-val");
            if (isNaN(idx)) return;
            selectItemByIndex(idx);
          });

          // Initial paint at 00 if not yet synced.
          applyWheelTransform(vp, 0, 0);
          markWheelSelected(vp, 0);
        })(vps[i]);
      }
    }

    function buildCalSkeleton(panel, withTime, withRangeFoot) {
      panel.innerHTML =
        '<div class="ui-cal__head">' +
          '<button type="button" class="ui-cal__nav" data-cal-prev aria-label="上一段">' +
            '<svg width="12" height="12" viewBox="0 0 12 12" fill="none" aria-hidden="true"><path d="M7.5 3L4.5 6L7.5 9" stroke="currentColor" stroke-width="1.2" stroke-linecap="round" stroke-linejoin="round"/></svg>' +
          '</button>' +
          '<button type="button" class="ui-cal__title" data-cal-title aria-label="切换年月"></button>' +
          '<button type="button" class="ui-cal__nav" data-cal-next aria-label="下一段">' +
            '<svg width="12" height="12" viewBox="0 0 12 12" fill="none" aria-hidden="true"><path d="M4.5 3L7.5 6L4.5 9" stroke="currentColor" stroke-width="1.2" stroke-linecap="round" stroke-linejoin="round"/></svg>' +
          '</button>' +
        '</div>' +
        '<div class="ui-cal__weekdays" data-cal-weekdays>' +
          WEEKDAYS.map(function (w) { return '<div class="ui-cal__weekday">' + w + '</div>'; }).join("") +
        '</div>' +
        '<div class="ui-cal__grid" data-cal-grid></div>' +
        (withTime
          ? '<div class="ui-datetime__time" data-time-row>' +
              '<span class="ui-datetime__time-label">时间</span>' +
              '<div class="ui-datetime__wheels">' +
                buildWheelHtml("hour", 24, "小时") +
                '<span class="ui-datetime__time-sep">:</span>' +
                buildWheelHtml("minute", 60, "分钟") +
              '</div>' +
            '</div>'
          : "") +
        (withRangeFoot
          ? '<div class="ui-daterange__foot">' +
              '<span class="ui-daterange__summary" data-range-summary>选择起止日期</span>' +
              '<button type="button" class="ui-daterange__clear" data-range-clear>清除</button>' +
            '</div>'
          : "");
    }

    function renderDayGrid(grid, viewYear, viewMonth, opts) {
      opts = opts || {};
      var today = toDateOnly(new Date());
      var first = new Date(viewYear, viewMonth, 1);
      var startDow = first.getDay();
      var mondayOffset = startDow === 0 ? 6 : startDow - 1;
      var cursor = new Date(viewYear, viewMonth, 1 - mondayOffset);
      var html = "";
      for (var i = 0; i < 42; i++) {
        var d = new Date(cursor.getFullYear(), cursor.getMonth(), cursor.getDate());
        var outside = d.getMonth() !== viewMonth;
        var classes = ["ui-cal__day"];
        if (outside) classes.push("is-outside");
        if (sameDay(d, today)) classes.push("is-today");
        if (opts.isSelected && opts.isSelected(d)) classes.push("is-selected");
        if (opts.isRangeStart && opts.isRangeStart(d)) classes.push("is-range-start");
        if (opts.isRangeEnd && opts.isRangeEnd(d)) classes.push("is-range-end");
        if (opts.isInRange && opts.isInRange(d)) classes.push("is-in-range");
        html +=
          '<button type="button" class="' + classes.join(" ") + '" data-day="' + dayKey(d) + '"' +
          (outside ? ' data-outside="1"' : "") +
          ">" + d.getDate() + "</button>";
        cursor.setDate(cursor.getDate() + 1);
      }
      grid.className = "ui-cal__grid";
      grid.innerHTML = html;
    }

    function renderMonthsGrid(grid, viewYear, selectedMonth, selectedYear) {
      var now = new Date();
      var html = "";
      for (var m = 0; m < 12; m++) {
        var classes = ["ui-cal__cell"];
        if (selectedYear === viewYear && selectedMonth === m) classes.push("is-selected");
        if (now.getFullYear() === viewYear && now.getMonth() === m) classes.push("is-today");
        html +=
          '<button type="button" class="' + classes.join(" ") + '" data-pick-month="' + m + '">' +
          MONTHS[m] + "</button>";
      }
      grid.className = "ui-cal__grid ui-cal__grid--months";
      grid.innerHTML = html;
    }

    function renderYearsGrid(grid, base, selectedYear) {
      var nowY = new Date().getFullYear();
      var html = "";
      for (var i = 0; i < 12; i++) {
        var y = base + i;
        var classes = ["ui-cal__cell"];
        if (selectedYear === y) classes.push("is-selected");
        if (nowY === y) classes.push("is-today");
        html +=
          '<button type="button" class="' + classes.join(" ") + '" data-pick-year="' + y + '">' +
          y + "</button>";
      }
      grid.className = "ui-cal__grid ui-cal__grid--years";
      grid.innerHTML = html;
    }

    function syncCalView(panel, state, dayOpts) {
      if (!panel) return;
      var title = panel.querySelector("[data-cal-title]");
      var weekdays = panel.querySelector("[data-cal-weekdays]");
      var grid = panel.querySelector("[data-cal-grid]");
      var mode = state.mode || "day";
      var prevBtn = panel.querySelector("[data-cal-prev]");
      var nextBtn = panel.querySelector("[data-cal-next]");

      if (title) title.classList.toggle("is-terminal", mode === "year");
      if (mode === "day") {
        if (title) {
          title.textContent = state.viewYear + "年" + (state.viewMonth + 1) + "月";
          title.setAttribute("aria-label", "选择月份");
        }
        if (weekdays) weekdays.hidden = false;
        if (prevBtn) prevBtn.setAttribute("aria-label", "上个月");
        if (nextBtn) nextBtn.setAttribute("aria-label", "下个月");
        if (grid) renderDayGrid(grid, state.viewYear, state.viewMonth, dayOpts || {});
      } else if (mode === "month") {
        if (title) {
          title.textContent = state.viewYear + "年";
          title.setAttribute("aria-label", "选择年份");
        }
        if (weekdays) weekdays.hidden = true;
        if (prevBtn) prevBtn.setAttribute("aria-label", "上一年");
        if (nextBtn) nextBtn.setAttribute("aria-label", "下一年");
        if (grid) {
          var selM = dayOpts && dayOpts.selectedMonth;
          var selY = dayOpts && dayOpts.selectedYear;
          renderMonthsGrid(grid, state.viewYear, selM, selY);
        }
      } else {
        var base = state.decadeStart != null ? state.decadeStart : decadeBase(state.viewYear);
        state.decadeStart = base;
        if (title) {
          title.textContent = base + " – " + (base + 11);
          title.setAttribute("aria-label", "当前年段");
        }
        if (weekdays) weekdays.hidden = true;
        if (prevBtn) prevBtn.setAttribute("aria-label", "上一段年份");
        if (nextBtn) nextBtn.setAttribute("aria-label", "下一段年份");
        if (grid) {
          var selYear = dayOpts && dayOpts.selectedYear;
          renderYearsGrid(grid, base, selYear);
        }
      }
    }

    function calNav(state, dir) {
      var mode = state.mode || "day";
      if (mode === "day") {
        state.viewMonth += dir;
        if (state.viewMonth < 0) { state.viewMonth = 11; state.viewYear -= 1; }
        if (state.viewMonth > 11) { state.viewMonth = 0; state.viewYear += 1; }
      } else if (mode === "month") {
        state.viewYear += dir;
      } else {
        var base = state.decadeStart != null ? state.decadeStart : decadeBase(state.viewYear);
        state.decadeStart = base + dir * 10;
      }
    }

    function calTitleClick(state) {
      var mode = state.mode || "day";
      if (mode === "day") {
        state.mode = "month";
      } else if (mode === "month") {
        state.mode = "year";
        state.decadeStart = decadeBase(state.viewYear);
      }
      // year view: title is non-escalating
    }

    /* —— datetime —— */
    function getDtState(root) {
      if (!root._uiDt) {
        var initial = parseDateTime(root.getAttribute("data-value"));
        var now = new Date();
        root._uiDt = {
          value: initial,
          viewYear: (initial || now).getFullYear(),
          viewMonth: (initial || now).getMonth(),
          mode: "day",
          decadeStart: null
        };
      }
      return root._uiDt;
    }

    function syncDtTrigger(root) {
      var state = getDtState(root);
      var label = root.querySelector(".ui-datetime__value");
      if (!label) return;
      if (state.value) {
        label.textContent = fmtDateTimeDisplay(state.value);
        label.classList.remove("is-placeholder");
        root.setAttribute("data-value", fmtDateTimeValue(state.value));
      } else {
        label.textContent = "选择日期时间";
        label.classList.add("is-placeholder");
        root.setAttribute("data-value", "");
      }
    }

    function syncDtPanel(root, skipWheelSync) {
      var panel = root.querySelector(".ui-datetime__panel");
      if (!panel || !panel.firstChild) return;
      var state = getDtState(root);
      syncCalView(panel, state, {
        isSelected: function (d) { return state.value && sameDay(d, state.value); },
        selectedMonth: state.value ? state.value.getMonth() : state.viewMonth,
        selectedYear: state.value ? state.value.getFullYear() : state.viewYear
      });
      if (!skipWheelSync) syncWheelsToState(panel, state);
    }

    function ensureDtPanel(root) {
      var panel = root.querySelector(".ui-datetime__panel");
      if (!panel) return null;
      if (!panel.firstChild) buildCalSkeleton(panel, true, false);
      bindTimeWheels(root, panel);
      return panel;
    }

    function setDtValue(root, value, silent, skipWheelSync) {
      if (!root) return;
      var state = getDtState(root);
      var parsed = value == null || value === "" ? null : parseDateTime(value);
      state.value = parsed;
      if (parsed) {
        state.viewYear = parsed.getFullYear();
        state.viewMonth = parsed.getMonth();
      }
      syncDtTrigger(root);
      if (root.classList.contains("is-open")) syncDtPanel(root, skipWheelSync);
      if (!silent) {
        root.dispatchEvent(new CustomEvent("ui-change", {
          bubbles: true,
          detail: { value: parsed ? fmtDateTimeValue(parsed) : null, date: parsed }
        }));
      }
    }

    function closeDt(root) {
      if (!root) return;
      var panel = root.querySelector(".ui-datetime__panel");
      var trigger = root.querySelector(".ui-datetime__trigger");
      var state = getDtState(root);
      state.mode = "day";
      root.classList.remove("is-open");
      if (trigger) trigger.setAttribute("aria-expanded", "false");
      if (panel) {
        if (motionOn()) {
          var t = setTimeout(function () {
            if (!root.classList.contains("is-open")) panel.hidden = true;
          }, 160);
          root._uiDtCloseTimer = t;
        } else {
          panel.hidden = true;
        }
      }
      if (openDt === root) openDt = null;
    }

    function openDtRoot(root) {
      if (!root) return;
      if (window.__uiSelect && window.__uiSelect.closeAll) window.__uiSelect.closeAll();
      if (openDr) closeDr(openDr);
      if (openDt && openDt !== root) closeDt(openDt);
      if (root._uiDtCloseTimer) {
        clearTimeout(root._uiDtCloseTimer);
        root._uiDtCloseTimer = null;
      }
      var state = getDtState(root);
      state.mode = "day";
      var panel = ensureDtPanel(root);
      var trigger = root.querySelector(".ui-datetime__trigger");
      if (panel) {
        panel.hidden = false;
        syncDtPanel(root);
        void panel.offsetWidth;
        positionPanel(panel, trigger);
      }
      root.classList.add("is-open");
      if (trigger) trigger.setAttribute("aria-expanded", "true");
      openDt = root;
      requestAnimationFrame(function () {
        if (panel && trigger && root.classList.contains("is-open")) {
          syncWheelsToState(panel, getDtState(root));
          positionPanel(panel, trigger);
        }
      });
    }

    function toggleDt(root) {
      if (root.classList.contains("is-open")) closeDt(root);
      else openDtRoot(root);
    }

    function getDt(root) {
      if (!root) return null;
      var state = getDtState(root);
      return state.value ? fmtDateTimeValue(state.value) : null;
    }

    function ensureDtBaseDate(state) {
      if (state.value) return new Date(state.value.getTime());
      return new Date(state.viewYear, state.viewMonth, 1, 0, 0, 0, 0);
    }

    /* —— daterange —— */
    function getDrState(root) {
      if (!root._uiDr) {
        var start = parseDateOnly(root.getAttribute("data-start"));
        var end = parseDateOnly(root.getAttribute("data-end"));
        var now = new Date();
        var anchor = start || end || now;
        root._uiDr = {
          start: start,
          end: end,
          picking: null,
          viewYear: anchor.getFullYear(),
          viewMonth: anchor.getMonth(),
          mode: "day",
          decadeStart: null
        };
      }
      return root._uiDr;
    }

    function syncDrTrigger(root) {
      var state = getDrState(root);
      var label = root.querySelector(".ui-daterange__value");
      if (!label) return;
      if (state.start && state.end) {
        label.textContent = fmtDate(state.start) + " → " + fmtDate(state.end);
        label.classList.remove("is-placeholder");
        root.setAttribute("data-start", fmtDate(state.start));
        root.setAttribute("data-end", fmtDate(state.end));
      } else if (state.start && !state.end) {
        label.textContent = fmtDate(state.start) + " → …";
        label.classList.add("is-placeholder");
        root.setAttribute("data-start", fmtDate(state.start));
        root.setAttribute("data-end", "");
      } else {
        label.textContent = "选择日期范围";
        label.classList.add("is-placeholder");
        root.setAttribute("data-start", "");
        root.setAttribute("data-end", "");
      }
    }

    function syncDrSummary(panel, state) {
      var summary = panel.querySelector("[data-range-summary]");
      if (!summary) return;
      if (state.start && state.end) summary.textContent = fmtDate(state.start) + " → " + fmtDate(state.end);
      else if (state.start) summary.textContent = fmtDate(state.start) + " → 选择结束";
      else summary.textContent = "选择起止日期";
    }

    function syncDrPanel(root) {
      var panel = root.querySelector(".ui-daterange__panel");
      if (!panel || !panel.firstChild) return;
      var state = getDrState(root);
      var start = state.start;
      var end = state.end;
      syncCalView(panel, state, {
        isRangeStart: function (d) { return start && sameDay(d, start); },
        isRangeEnd: function (d) { return end && sameDay(d, end); },
        isInRange: function (d) {
          if (!start || !end) return false;
          var t = d.getTime();
          return t > start.getTime() && t < end.getTime();
        },
        isSelected: function (d) {
          return (start && sameDay(d, start)) || (end && sameDay(d, end));
        },
        selectedMonth: (start || end || { getMonth: function () { return state.viewMonth; } }).getMonth(),
        selectedYear: (start || end || { getFullYear: function () { return state.viewYear; } }).getFullYear()
      });
      syncDrSummary(panel, state);
    }

    function ensureDrPanel(root) {
      var panel = root.querySelector(".ui-daterange__panel");
      if (!panel) return null;
      if (!panel.firstChild) buildCalSkeleton(panel, false, true);
      return panel;
    }

    function setDrValue(root, start, end, silent) {
      if (!root) return;
      var state = getDrState(root);
      var s = start == null || start === "" ? null : parseDateOnly(start);
      var e = end == null || end === "" ? null : parseDateOnly(end);
      if (s && e && e.getTime() < s.getTime()) {
        var tmp = s; s = e; e = tmp;
      }
      state.start = s;
      state.end = e;
      state.picking = null;
      var anchor = s || e || new Date();
      state.viewYear = anchor.getFullYear();
      state.viewMonth = anchor.getMonth();
      syncDrTrigger(root);
      if (root.classList.contains("is-open")) syncDrPanel(root);
      if (!silent) {
        root.dispatchEvent(new CustomEvent("ui-change", {
          bubbles: true,
          detail: {
            start: s ? fmtDate(s) : null,
            end: e ? fmtDate(e) : null,
            value: (s && e) ? { start: fmtDate(s), end: fmtDate(e) } : null
          }
        }));
      }
    }

    function getDr(root) {
      if (!root) return null;
      var state = getDrState(root);
      if (!state.start || !state.end) return null;
      return { start: fmtDate(state.start), end: fmtDate(state.end) };
    }

    function closeDr(root) {
      if (!root) return;
      var panel = root.querySelector(".ui-daterange__panel");
      var trigger = root.querySelector(".ui-daterange__trigger");
      var state = getDrState(root);
      state.mode = "day";
      root.classList.remove("is-open");
      if (trigger) trigger.setAttribute("aria-expanded", "false");
      if (panel) {
        if (motionOn()) {
          var t = setTimeout(function () {
            if (!root.classList.contains("is-open")) panel.hidden = true;
          }, 160);
          root._uiDrCloseTimer = t;
        } else {
          panel.hidden = true;
        }
      }
      if (openDr === root) openDr = null;
    }

    function openDrRoot(root) {
      if (!root) return;
      if (window.__uiSelect && window.__uiSelect.closeAll) window.__uiSelect.closeAll();
      if (openDt) closeDt(openDt);
      if (openDr && openDr !== root) closeDr(openDr);
      if (root._uiDrCloseTimer) {
        clearTimeout(root._uiDrCloseTimer);
        root._uiDrCloseTimer = null;
      }
      var state = getDrState(root);
      state.mode = "day";
      var panel = ensureDrPanel(root);
      var trigger = root.querySelector(".ui-daterange__trigger");
      if (panel) {
        panel.hidden = false;
        syncDrPanel(root);
        void panel.offsetWidth;
        positionPanel(panel, trigger);
      }
      root.classList.add("is-open");
      if (trigger) trigger.setAttribute("aria-expanded", "true");
      openDr = root;
      requestAnimationFrame(function () {
        if (panel && trigger && root.classList.contains("is-open")) positionPanel(panel, trigger);
      });
    }

    function toggleDr(root) {
      if (root.classList.contains("is-open")) closeDr(root);
      else openDrRoot(root);
    }

    function handleDtPanelClick(e, dtRoot, panel) {
      var state = getDtState(dtRoot);
      var titleBtn = e.target.closest("[data-cal-title]");
      var prev = e.target.closest("[data-cal-prev]");
      var next = e.target.closest("[data-cal-next]");
      var dayBtn = e.target.closest("[data-day]");
      var monthBtn = e.target.closest("[data-pick-month]");
      var yearBtn = e.target.closest("[data-pick-year]");

      // wheel interactions handled by bindTimeWheels
      if (e.target.closest("[data-wheel]")) return true;

      if (titleBtn) {
        e.preventDefault();
        calTitleClick(state);
        syncDtPanel(dtRoot);
        positionPanel(panel, dtRoot.querySelector(".ui-datetime__trigger"));
        return true;
      }
      if (prev || next) {
        e.preventDefault();
        calNav(state, prev ? -1 : 1);
        syncDtPanel(dtRoot);
        positionPanel(panel, dtRoot.querySelector(".ui-datetime__trigger"));
        return true;
      }
      if (yearBtn) {
        e.preventDefault();
        state.viewYear = +yearBtn.getAttribute("data-pick-year");
        state.mode = "month";
        syncDtPanel(dtRoot);
        positionPanel(panel, dtRoot.querySelector(".ui-datetime__trigger"));
        return true;
      }
      if (monthBtn) {
        e.preventDefault();
        state.viewMonth = +monthBtn.getAttribute("data-pick-month");
        state.mode = "day";
        syncDtPanel(dtRoot);
        positionPanel(panel, dtRoot.querySelector(".ui-datetime__trigger"));
        return true;
      }
      if (dayBtn && (state.mode || "day") === "day") {
        e.preventDefault();
        var day = parseDateOnly(dayBtn.getAttribute("data-day"));
        if (!day) return true;
        var base = ensureDtBaseDate(state);
        setDtValue(dtRoot, new Date(day.getFullYear(), day.getMonth(), day.getDate(), base.getHours(), base.getMinutes()), false);
        return true;
      }
      return true;
    }

    function handleDrPanelClick(e, drRoot, panel) {
      var state = getDrState(drRoot);
      var titleBtn = e.target.closest("[data-cal-title]");
      var prev = e.target.closest("[data-cal-prev]");
      var next = e.target.closest("[data-cal-next]");
      var dayBtn = e.target.closest("[data-day]");
      var monthBtn = e.target.closest("[data-pick-month]");
      var yearBtn = e.target.closest("[data-pick-year]");
      var clearBtn = e.target.closest("[data-range-clear]");

      if (titleBtn) {
        e.preventDefault();
        calTitleClick(state);
        syncDrPanel(drRoot);
        positionPanel(panel, drRoot.querySelector(".ui-daterange__trigger"));
        return true;
      }
      if (prev || next) {
        e.preventDefault();
        calNav(state, prev ? -1 : 1);
        syncDrPanel(drRoot);
        positionPanel(panel, drRoot.querySelector(".ui-daterange__trigger"));
        return true;
      }
      if (yearBtn) {
        e.preventDefault();
        state.viewYear = +yearBtn.getAttribute("data-pick-year");
        state.mode = "month";
        syncDrPanel(drRoot);
        positionPanel(panel, drRoot.querySelector(".ui-daterange__trigger"));
        return true;
      }
      if (monthBtn) {
        e.preventDefault();
        state.viewMonth = +monthBtn.getAttribute("data-pick-month");
        state.mode = "day";
        syncDrPanel(drRoot);
        positionPanel(panel, drRoot.querySelector(".ui-daterange__trigger"));
        return true;
      }
      if (clearBtn) {
        e.preventDefault();
        setDrValue(drRoot, null, null, false);
        return true;
      }
      if (dayBtn && (state.mode || "day") === "day") {
        e.preventDefault();
        var day = parseDateOnly(dayBtn.getAttribute("data-day"));
        if (!day) return true;
        if (!state.start || (state.start && state.end)) {
          state.start = day;
          state.end = null;
          state.picking = "end";
          syncDrTrigger(drRoot);
          syncDrPanel(drRoot);
          return true;
        }
        var s = state.start;
        var eDay = day;
        if (eDay.getTime() < s.getTime()) {
          state.end = s;
          state.start = eDay;
        } else {
          state.end = eDay;
        }
        state.picking = null;
        syncDrTrigger(drRoot);
        syncDrPanel(drRoot);
        drRoot.dispatchEvent(new CustomEvent("ui-change", {
          bubbles: true,
          detail: {
            start: fmtDate(state.start),
            end: fmtDate(state.end),
            value: { start: fmtDate(state.start), end: fmtDate(state.end) }
          }
        }));
        return true;
      }
      return true;
    }

    document.querySelectorAll("[data-ui-datetime]").forEach(function (root) {
      getDtState(root);
      syncDtTrigger(root);
    });
    document.querySelectorAll("[data-ui-daterange]").forEach(function (root) {
      getDrState(root);
      syncDrTrigger(root);
    });

    document.addEventListener("click", function (e) {
      var dtTrigger = e.target.closest(".ui-datetime__trigger");
      if (dtTrigger) {
        var root = dtTrigger.closest("[data-ui-datetime]");
        if (!root) return;
        e.preventDefault();
        toggleDt(root);
        return;
      }

      var drTrigger = e.target.closest(".ui-daterange__trigger");
      if (drTrigger) {
        var root2 = drTrigger.closest("[data-ui-daterange]");
        if (!root2) return;
        e.preventDefault();
        toggleDr(root2);
        return;
      }

      if (openDt) {
        var dtRoot = openDt;
        var panel = dtRoot.querySelector(".ui-datetime__panel");
        if (panel && panel.contains(e.target)) {
          handleDtPanelClick(e, dtRoot, panel);
          return;
        }
        if (!e.target.closest("[data-ui-datetime]")) closeDt(dtRoot);
      }

      if (openDr) {
        var drRoot = openDr;
        var panel2 = drRoot.querySelector(".ui-daterange__panel");
        if (panel2 && panel2.contains(e.target)) {
          handleDrPanelClick(e, drRoot, panel2);
          return;
        }
        if (!e.target.closest("[data-ui-daterange]")) closeDr(drRoot);
      }
    });

    document.addEventListener("keydown", function (e) {
      if (e.key !== "Escape") return;
      if (openDt) {
        var st = getDtState(openDt);
        if (st.mode && st.mode !== "day") {
          st.mode = st.mode === "year" ? "month" : "day";
          syncDtPanel(openDt);
          e.stopPropagation();
          return;
        }
        closeDt(openDt);
        e.stopPropagation();
        return;
      }
      if (openDr) {
        var st2 = getDrState(openDr);
        if (st2.mode && st2.mode !== "day") {
          st2.mode = st2.mode === "year" ? "month" : "day";
          syncDrPanel(openDr);
          e.stopPropagation();
          return;
        }
        closeDr(openDr);
        e.stopPropagation();
      }
    }, true);

    window.addEventListener("resize", function () {
      if (openDt) {
        var p = openDt.querySelector(".ui-datetime__panel");
        var t = openDt.querySelector(".ui-datetime__trigger");
        if (p && t) positionPanel(p, t);
      }
      if (openDr) {
        var p2 = openDr.querySelector(".ui-daterange__panel");
        var t2 = openDr.querySelector(".ui-daterange__trigger");
        if (p2 && t2) positionPanel(p2, t2);
      }
    });

    window.__uiDateTime = {
      get: getDt,
      set: function (root, value, silent) { setDtValue(root, value, !!silent); },
      open: openDtRoot,
      close: closeDt,
      closeAll: function () { if (openDt) closeDt(openDt); }
    };

    window.__uiDateRange = {
      get: getDr,
      set: function (root, start, end, silent) { setDrValue(root, start, end, !!silent); },
      open: openDrRoot,
      close: closeDr,
      closeAll: function () { if (openDr) closeDr(openDr); }
    };
  })();

  /* —— Member data-grid (generic table template) —— */
  (function initMemberGrid() {
    var grid = document.getElementById("memberGrid");
    if (!grid) return;
    var tbody = document.getElementById("memberTbody");
    var selectAll = document.getElementById("memberSelectAll");
    var search = document.getElementById("memberSearch");
    var bulk = document.getElementById("memberBulk");
    var bulkCount = document.getElementById("memberBulkCount");
    var countEl = document.getElementById("memberCount");
    var empty = document.getElementById("memberEmpty");
    var body = document.getElementById("adminTableWrap");
    var filter = "全部";
    var sortKey = null;
    var sortDir = "asc";
    var page = 1;
    var pageSize = 10;
    var pageLabel = document.getElementById("memberPageLabel");
    var prevBtn = document.getElementById("memberPrev");
    var nextBtn = document.getElementById("memberNext");
    var pageSizeSelect = document.getElementById("memberPageSize");

    function rows() {
      return Array.prototype.slice.call(tbody.querySelectorAll("tr"));
    }

    function visibleRows() {
      return rows().filter(function (row) { return row.style.display !== "none"; });
    }

    function updateBulk() {
      var selected = rows().filter(function (row) {
        var cb = row.querySelector('input[type="checkbox"]');
        return cb && cb.checked && row.style.display !== "none";
      });
      rows().forEach(function (row) {
        var cb = row.querySelector('input[type="checkbox"]');
        row.classList.toggle("is-selected", !!(cb && cb.checked));
      });
      if (bulk) bulk.hidden = selected.length === 0;
      if (bulkCount) bulkCount.textContent = "已选 " + selected.length;
      if (selectAll) {
        var vis = visibleRows();
        var checkedVis = vis.filter(function (row) {
          var cb = row.querySelector('input[type="checkbox"]');
          return cb && cb.checked;
        });
        selectAll.checked = vis.length > 0 && checkedVis.length === vis.length;
        selectAll.indeterminate = checkedVis.length > 0 && checkedVis.length < vis.length;
      }
    }

    function matchedRows() {
      var q = ((search && search.value) || "").trim().toLowerCase();
      return rows().filter(function (row) {
        var status = row.getAttribute("data-status") || "";
        var hay = [
          row.getAttribute("data-name") || "",
          row.getAttribute("data-role") || "",
          row.getAttribute("data-scope") || "",
          status
        ].join(" ").toLowerCase();
        var matchQ = !q || hay.indexOf(q) !== -1;
        var matchS = filter === "全部" || status === filter;
        return matchQ && matchS;
      });
    }

    function updatePager(total) {
      var pages = Math.max(1, Math.ceil(total / pageSize) || 1);
      if (page > pages) page = pages;
      if (page < 1) page = 1;
      if (pageLabel) pageLabel.textContent = page + " / " + pages;
      if (prevBtn) prevBtn.disabled = page <= 1 || total === 0;
      if (nextBtn) nextBtn.disabled = page >= pages || total === 0;
    }

    function applyGridFilters(resetPage) {
      if (resetPage) page = 1;
      var matched = matchedRows();
      var total = matched.length;
      var start = (page - 1) * pageSize;
      var end = start + pageSize;
      var matchedSet = new Set(matched);
      rows().forEach(function (row) {
        if (!matchedSet.has(row)) {
          row.style.display = "none";
          return;
        }
        var idx = matched.indexOf(row);
        row.style.display = (idx >= start && idx < end) ? "" : "none";
      });
      if (countEl) {
        if (total === 0) countEl.textContent = "共 0 人";
        else {
          var from = start + 1;
          var to = Math.min(end, total);
          countEl.textContent = "共 " + total + " 人 · " + from + "–" + to;
        }
      }
      if (body) body.classList.toggle("is-empty", total === 0);
      if (empty) {
        empty.hidden = total !== 0;
        empty.classList.toggle("is-visible", total === 0);
      }
      updatePager(total);
      updateBulk();
    }

    function sortBy(key) {
      if (sortKey === key) sortDir = sortDir === "asc" ? "desc" : "asc";
      else { sortKey = key; sortDir = "asc"; }
      grid.querySelectorAll(".data-grid__sort").forEach(function (btn) {
        btn.classList.toggle("is-asc", btn.getAttribute("data-sort-key") === sortKey && sortDir === "asc");
        btn.classList.toggle("is-desc", btn.getAttribute("data-sort-key") === sortKey && sortDir === "desc");
      });
      var list = rows();
      list.sort(function (a, b) {
        var av, bv;
        if (key === "active") {
          av = Number(a.getAttribute("data-active") || 0);
          bv = Number(b.getAttribute("data-active") || 0);
        } else {
          av = (a.getAttribute("data-" + key) || "").toString();
          bv = (b.getAttribute("data-" + key) || "").toString();
        }
        if (av < bv) return sortDir === "asc" ? -1 : 1;
        if (av > bv) return sortDir === "asc" ? 1 : -1;
        return 0;
      });
      list.forEach(function (row) { tbody.appendChild(row); });
      applyGridFilters(false);
    }

    if (selectAll) {
      selectAll.addEventListener("change", function () {
        visibleRows().forEach(function (row) {
          var cb = row.querySelector('input[type="checkbox"]');
          if (cb) cb.checked = selectAll.checked;
        });
        updateBulk();
      });
    }

    tbody.addEventListener("change", function (e) {
      if (e.target && e.target.matches('input[type="checkbox"]')) updateBulk();
    });

    tbody.addEventListener("click", function (e) {
      if (e.target.closest("button, a, input, label")) return;
      var row = e.target.closest("tr");
      if (!row) return;
      var cb = row.querySelector('input[type="checkbox"]');
      if (!cb) return;
      cb.checked = !cb.checked;
      updateBulk();
    });

    grid.querySelectorAll("[data-member-filter]").forEach(function (chip) {
      chip.addEventListener("click", function () {
        grid.querySelectorAll("[data-member-filter]").forEach(function (c) {
          c.classList.toggle("is-active", c === chip);
        });
        filter = chip.getAttribute("data-member-filter") || "全部";
        applyGridFilters(true);
      });
    });

    window.__applyMemberFilters = applyGridFilters;

    if (search) search.addEventListener("input", function () { applyGridFilters(true); });

    var clearMemberBtn = document.getElementById("clearMemberFilters");
    if (clearMemberBtn) {
      clearMemberBtn.addEventListener("click", function () {
        filter = "全部";
        grid.querySelectorAll("[data-member-filter]").forEach(function (c) {
          c.classList.toggle("is-active", c.getAttribute("data-member-filter") === "全部");
        });
        if (search) search.value = "";
        applyGridFilters(true);
        if (window.__showToast) window.__showToast("已清除筛选", "info");
      });
    }

    grid.querySelectorAll(".data-grid__sort").forEach(function (btn) {
      btn.addEventListener("click", function () {
        sortBy(btn.getAttribute("data-sort-key"));
      });
    });

    grid.querySelectorAll("[data-bulk]").forEach(function (btn) {
      btn.addEventListener("click", function () {
        var action = btn.getAttribute("data-bulk");
        var selected = rows().filter(function (row) {
          var cb = row.querySelector('input[type="checkbox"]');
          return cb && cb.checked && row.style.display !== "none";
        });
        selected.forEach(function (row) {
          var next = action === "enable" ? "启用" : "停用";
          row.setAttribute("data-status", next);
          var pill = row.querySelector(".status-pill");
          if (pill) {
            pill.classList.toggle("draft", next === "停用");
            pill.innerHTML = '<i class="sdot"></i>' + next;
          }
          var cb = row.querySelector('input[type="checkbox"]');
          if (cb) cb.checked = false;
        });
        applyGridFilters();
        if (window.__showToast) {
          window.__showToast(
            action === "enable" ? ("已启用 " + selected.length + " 人") : ("已停用 " + selected.length + " 人"),
            action === "enable" ? "success" : "warning"
          );
        }
      });
    });


    if (prevBtn) {
      prevBtn.addEventListener("click", function () {
        if (page <= 1) return;
        page -= 1;
        applyGridFilters(false);
      });
    }
    if (nextBtn) {
      nextBtn.addEventListener("click", function () {
        page += 1;
        applyGridFilters(false);
      });
    }
    if (pageSizeSelect) {
      pageSizeSelect.addEventListener("ui-change", function (e) {
        pageSize = Number((e.detail && e.detail.value) || pageSizeSelect.getAttribute("data-value") || 10) || 10;
        applyGridFilters(true);
      });
    }


    var activeRow = null;
    var nameField = document.getElementById("memberFieldName");
    var roleField = document.getElementById("memberFieldRole");
    var scopeField = document.getElementById("memberFieldScope");
    var statusField = document.getElementById("memberFieldStatus");
    var activeAtField = document.getElementById("memberFieldActiveAt");
    var validRangeField = document.getElementById("memberFieldValidRange");
    var avatarEl = document.getElementById("memberDrawerAvatar");
    var titleEl = document.getElementById("drawer-member-title");
    var kickerEl = document.getElementById("memberDrawerKicker");
    var saveBtn = document.getElementById("memberDrawerSave");

    function openMemberSubwindow(row) {
      if (!row) return;
      activeRow = row;
      var name = row.getAttribute("data-name") || "";
      var role = row.getAttribute("data-role") || "";
      var scope = row.getAttribute("data-scope") || "";
      var status = row.getAttribute("data-status") || "启用";
      if (nameField) nameField.value = name;
      if (window.__uiSelect) {
        window.__uiSelect.set(roleField, role);
        window.__uiSelect.set(statusField, status);
      }
      if (scopeField) scopeField.value = scope;
      if (window.__uiDateTime && activeAtField) {
        var activeAt = row.getAttribute("data-active-at");
        if (activeAt) window.__uiDateTime.set(activeAtField, activeAt, true);
      }
      if (window.__uiDateRange && validRangeField) {
        var vs = row.getAttribute("data-valid-start");
        var ve = row.getAttribute("data-valid-end");
        if (vs && ve) window.__uiDateRange.set(validRangeField, vs, ve, true);
      }
      if (avatarEl) avatarEl.textContent = name ? name.charAt(0) : "成";
      if (titleEl) titleEl.textContent = name || "编辑成员";
      if (kickerEl) kickerEl.textContent = (role || "成员") + " · 权限";
      if (window.__openOverlay) window.__openOverlay("drawer-member");
    }

    tbody.addEventListener("click", function (e) {
      var editBtn = e.target.closest("[data-member-edit]");
      if (!editBtn) return;
      e.preventDefault();
      e.stopPropagation();
      var row = editBtn.closest("tr");
      openMemberSubwindow(row);
    });

    if (saveBtn) {
      saveBtn.addEventListener("click", function () {
        if (!activeRow) return;
        var name = (nameField && nameField.value || "").trim();
        var role = (window.__uiSelect && roleField) ? window.__uiSelect.get(roleField) : "";
        var scope = scopeField ? scopeField.value.trim() : "";
        var status = (window.__uiSelect && statusField) ? window.__uiSelect.get(statusField) : "启用";
        if (!name) {
          if (nameField) nameField.focus();
          return;
        }
        activeRow.setAttribute("data-name", name);
        activeRow.setAttribute("data-role", role);
        activeRow.setAttribute("data-scope", scope);
        activeRow.setAttribute("data-status", status);
        var person = activeRow.querySelector(".data-grid__person span:last-child");
        var av = activeRow.querySelector(".data-grid__person .avatar");
        if (person) person.textContent = name;
        if (av) av.textContent = name.charAt(0);
        var cells = activeRow.querySelectorAll("td");
        // selection, person, role, scope, status, active, actions
        if (cells[2]) { cells[2].textContent = role; cells[2].classList.add("muted"); }
        if (cells[3]) { cells[3].textContent = scope; cells[3].classList.add("muted"); }
        var pill = activeRow.querySelector(".status-pill");
        if (pill) {
          pill.classList.toggle("draft", status === "停用");
          pill.innerHTML = '<i class="sdot"></i>' + status;
        }
        var cb = activeRow.querySelector('input[type="checkbox"]');
        if (cb) cb.setAttribute("aria-label", "选择 " + name);
        applyGridFilters();
        if (window.__closeOverlay) {
          var ov = document.getElementById("drawer-member");
          if (ov) window.__closeOverlay(ov);
        }
        if (window.__showToast) window.__showToast("已保存 " + name, "success");
        activeRow = null;
      });
    }

    applyGridFilters();
  })();


  /* —— Theme toggle (dark / light) —— */
  (function initThemeToggle() {
    var KEY = "mfg-ai-theme";
    var root = document.documentElement;

    function buttons() {
      return document.querySelectorAll(".theme-toggle");
    }

    function current() {
      return root.getAttribute("data-theme") === "light" ? "light" : "dark";
    }

    function apply(theme) {
      var next = theme === "light" ? "light" : "dark";
      root.setAttribute("data-theme", next);
      try { localStorage.setItem(KEY, next); } catch (e) {}
      var label = next === "light" ? "切换到暗色主题" : "切换到亮色主题";
      var title = next === "light" ? "暗色主题" : "亮色主题";
      buttons().forEach(function (btn) {
        btn.setAttribute("aria-label", label);
        btn.setAttribute("title", title);
      });
    }

    // Respect saved preference; only use prefers-color-scheme when unset
    var saved = null;
    try { saved = localStorage.getItem(KEY); } catch (e) {}
    if (saved === "light" || saved === "dark") {
      apply(saved);
    } else if (!root.getAttribute("data-theme")) {
      var prefersLight = false;
      try {
        prefersLight = window.matchMedia && window.matchMedia("(prefers-color-scheme: light)").matches;
      } catch (e) {}
      apply(prefersLight ? "light" : "dark");
    } else {
      apply(current());
    }

    buttons().forEach(function (btn) {
      btn.addEventListener("click", function () {
        apply(current() === "light" ? "dark" : "light");
      });
    });
  })();

  /* —— Sidebar collapse —— */
  (function initSidebarCollapse() {
    var frame = document.getElementById("app");
    var btn = document.getElementById("sidebarCollapseBtn");
    var KEY = "mfg-ai-sidebar-collapsed";
    if (!frame || !btn) return;

    function applyTitles(collapsed) {
      document.querySelectorAll(".sidebar .nav-item").forEach(function (item) {
        var label = item.querySelector(".nav-label");
        if (!label) return;
        if (collapsed) item.setAttribute("title", label.textContent.trim());
        else item.removeAttribute("title");
      });
      var ws = document.querySelector(".workspace");
      if (ws) {
        if (collapsed) ws.setAttribute("title", "集智");
        else ws.removeAttribute("title");
      }
    }

    function setCollapsed(collapsed) {
      frame.classList.toggle("is-sidebar-collapsed", collapsed);
      btn.setAttribute("aria-expanded", collapsed ? "false" : "true");
      btn.setAttribute("title", collapsed ? "展开侧边栏" : "收起侧边栏");
      var label = btn.querySelector(".sidebar-collapse-label");
      if (label) label.textContent = collapsed ? "展开" : "收起";
      applyTitles(collapsed);
      try { localStorage.setItem(KEY, collapsed ? "1" : "0"); } catch (e) {}
    }

    var initial = false;
    try { initial = localStorage.getItem(KEY) === "1"; } catch (e) {}
    setCollapsed(initial);

    btn.addEventListener("click", function () {
      setCollapsed(!frame.classList.contains("is-sidebar-collapsed"));
    });
  })();

  /* —— Toast + empty helpers —— */
  (function initToastAndEmpty() {
    var host = document.getElementById("toastHost");
    var clearBtn = document.getElementById("clearAppFilters");

    function motionOn() {
      return document.documentElement.dataset.motion !== "reduced";
    }

    function normalizeToastType(type) {
      var t = String(type == null ? "info" : type).toLowerCase();
      if (t === "ok" || t === "success") return "success";
      if (t === "warn" || t === "warning") return "warning";
      if (t === "danger" || t === "error") return "error";
      if (t === "neutral" || t === "default" || t === "info") return "info";
      return "info";
    }

    function showToast(message, type) {
      if (!host || !message) return;
      var variant = normalizeToastType(type);
      var el = document.createElement("div");
      el.className = "toast is-" + variant;
      el.setAttribute("role", variant === "error" ? "alert" : "status");
      el.setAttribute("data-toast-type", variant);
      el.textContent = message;
      host.appendChild(el);
      void el.offsetWidth;
      el.classList.add("is-in");
      var stay = 2200;
      setTimeout(function () {
        el.classList.remove("is-in");
        el.classList.add("is-out");
        setTimeout(function () {
          if (el.parentNode) el.parentNode.removeChild(el);
        }, motionOn() ? 200 : 0);
      }, stay);
    }

    document.addEventListener("click", function (e) {
      var btn = e.target.closest("[data-toast]");
      if (!btn) return;
      // defer slightly so overlay close can start first
      var msg = btn.getAttribute("data-toast");
      var type = btn.getAttribute("data-toast-type") || btn.getAttribute("data-toast-variant") || "info";
      setTimeout(function () { showToast(msg, type); }, 10);
    });

    if (clearBtn) {
      clearBtn.addEventListener("click", function () {
        activeFilter = "全部";
        filterChips.forEach(function (c) {
          c.classList.toggle("is-active", c.getAttribute("data-filter") === "全部");
        });
        if (appSearch) appSearch.value = "";
        applyAppFilters();
        showToast("已清除筛选", "info");
      });
    }

    window.__showToast = showToast;
  })();


  /* —— Skeleton loading demos —— */
  (function initSkeletonLoading() {
    var timers = {};

    function motionOn() {
      return document.documentElement.dataset.motion !== "reduced";
    }

    function runLoad(kind) {
      var duration = motionOn() ? 700 : 0;
      if (kind === "apps") {
        var list = document.getElementById("appList");
        var content = list && list.querySelector(".scroll-content");
        if (!list) return;
        if (timers.apps) clearTimeout(timers.apps);
        list.classList.add("is-loading");
        list.classList.remove("is-empty");
        var empty = document.getElementById("appEmpty");
        if (empty) {
          empty.hidden = true;
          empty.classList.remove("is-visible");
        }
        timers.apps = setTimeout(function () {
          list.classList.remove("is-loading");
          if (content) {
            content.classList.remove("content-fade-in");
            void content.offsetWidth;
            content.classList.add("content-fade-in");
          }
          applyAppFilters();
          if (window.__showToast) window.__showToast("应用列表已刷新", "info");
        }, duration);
        return;
      }
      if (kind === "admin") {
        var wrap = document.getElementById("adminTableWrap");
        var tableContent = document.getElementById("adminTableContent");
        var memberEmpty = document.getElementById("memberEmpty");
        if (!wrap) return;
        if (timers.admin) clearTimeout(timers.admin);
        wrap.classList.add("is-loading");
        wrap.classList.remove("is-empty");
        if (memberEmpty) {
          memberEmpty.hidden = true;
          memberEmpty.classList.remove("is-visible");
        }
        timers.admin = setTimeout(function () {
          wrap.classList.remove("is-loading");
          if (tableContent) {
            tableContent.classList.remove("content-fade-in");
            void tableContent.offsetWidth;
            tableContent.classList.add("content-fade-in");
          }
          if (typeof window.__applyMemberFilters === "function") window.__applyMemberFilters(false);
          if (window.__showToast) window.__showToast("成员列表已刷新", "success");
        }, duration);
      }
    }

    document.addEventListener("click", function (e) {
      var btn = e.target.closest("[data-refresh]");
      if (!btn) return;
      runLoad(btn.getAttribute("data-refresh"));
    });

    // first enter apps/admin: brief skeleton once per view
    var seen = { apps: false, admin: false };
    var origSwitch = switchView;
    switchView = function (name) {
      origSwitch(name);
      if ((name === "apps" || name === "admin") && !seen[name]) {
        seen[name] = true;
        runLoad(name === "apps" ? "apps" : "admin");
      }
    };
  })();

  /* —— Overlay open/close (CSS transitions) —— */
  (function initOverlays() {
    var stack = [];
    var closingTimers = new WeakMap();

    function motionOn() {
      return document.documentElement.dataset.motion !== "reduced";
    }

    function openOverlay(id) {
      var el = document.getElementById(id);
      if (!el) return;
      var existing = closingTimers.get(el);
      if (existing) {
        clearTimeout(existing);
        closingTimers.delete(el);
      }
      el.hidden = false;
      el.classList.remove("is-closing");
      // force reflow so enter transition runs
      void el.offsetWidth;
      el.classList.add("is-open");
      if (stack.indexOf(el) === -1) stack.push(el);
      var focusable = el.querySelector("input,button:not([data-close])");
      if (focusable) setTimeout(function () { focusable.focus(); }, 20);
    }

    function closeOverlay(el) {
      if (!el || !el.classList.contains("is-open")) {
        if (el) {
          el.classList.remove("is-closing");
          el.hidden = true;
        }
        return;
      }
      el.classList.remove("is-open");
      el.classList.add("is-closing");
      var delay = motionOn() ? 220 : 0;
      var timer = setTimeout(function () {
        el.classList.remove("is-closing");
        el.hidden = true;
        closingTimers.delete(el);
        stack = stack.filter(function (x) { return x !== el; });
      }, delay);
      closingTimers.set(el, timer);
    }

    function closeTop() {
      if (!stack.length) return;
      closeOverlay(stack[stack.length - 1]);
    }

    document.addEventListener("click", function (e) {
      var openBtn = e.target.closest("[data-open]");
      if (openBtn) {
        var id = openBtn.getAttribute("data-open");
        // if the same click also has data-close (e.g. drawer action), close current first
        if (openBtn.hasAttribute("data-close")) {
          var parent = openBtn.closest(".overlay");
          if (parent) closeOverlay(parent);
          setTimeout(function () { openOverlay(id); }, motionOn() ? 180 : 0);
        } else {
          openOverlay(id);
        }
        return;
      }
      if (e.target.closest("[data-close]")) {
        var overlay = e.target.closest(".overlay");
        if (overlay) closeOverlay(overlay);
      }
    });

    document.addEventListener("keydown", function (e) {
      if (e.key === "Escape") closeTop();
    });

    window.__openOverlay = openOverlay;
    window.__closeOverlay = closeOverlay;
  })();

  /* —— Inertial wheel scroll (same algorithm as clean-template-front) —— */
  (function initInertialWheelScroll() {
    if (typeof installGlobalInertialWheelScroll !== "function") {
      console.warn("inertialWheelScroll failed to load");
      return;
    }
    var params = new URLSearchParams(location.search || "");
    var mode = params.get("motion") || "full"; // mock default: full (OS animation is often off)
    var systemReduced = window.matchMedia && window.matchMedia("(prefers-reduced-motion: reduce)").matches;
    var reduced = mode === "reduced" || (mode === "system" && systemReduced);
    if (document.documentElement) {
      document.documentElement.dataset.motion = reduced ? "reduced" : "full";
    }
    if (systemReduced) {
      console.info("[scroll] OS prefers-reduced-motion is on. Mock still uses inertial unless ?motion=system or ?motion=reduced.");
    }
    installGlobalInertialWheelScroll(document, {
      shouldAnimate: function () {
        return document.documentElement.dataset.motion !== "reduced";
      }
    });
  })();

})();
