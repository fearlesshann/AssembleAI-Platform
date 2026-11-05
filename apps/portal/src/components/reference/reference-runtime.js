/*! MIT License

Copyright (c) 2026 YeHan

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
 */
/* Adapted from references/ui-v1/app.js, custom-select and custom-date-control sections.
 * Original DOM, drawing, calendar navigation, pointer and inertial wheel algorithms retained.
 * Changes: namespace/scope, disabled guards, wheel keyboard operation and component disposal.
 * Source license: references/ui-v1/LICENSE. */
let installed = false;
const runtimeKey = Symbol.for('assembleai.referenceControls');

// Keep the source DOM/paint/animation, but render the panel in the browser's
// top layer. A fixed child inside a transformed/animated ancestor otherwise
// interprets viewport coordinates in that ancestor's coordinate system.
function showFloatingPanel(root, panel, trigger, position, close) {
  root._referenceStopPosition?.();
  panel.setAttribute('popover', 'manual');
  Object.assign(panel.style, { margin: '0', right: 'auto', bottom: 'auto', color: 'inherit' });
  panel.hidden = false;
  if (!panel.matches(':popover-open')) panel.showPopover();
  const doc = root.ownerDocument, view = doc.defaultView;
  let frame = 0, stopped = false, previous = '';
  function update() {
    if (stopped) return;
    if (!root.isConnected || !trigger.getClientRects().length) { close(root); return; }
    const rect = trigger.getBoundingClientRect();
    const geometry = [rect.left, rect.top, rect.width, rect.height, panel.offsetWidth, panel.offsetHeight, view.innerWidth, view.innerHeight].join(',');
    if (geometry === previous) return;
    previous = geometry;
    let left = 0, top = 0, right = view.innerWidth, bottom = view.innerHeight;
    for (let parent = trigger.parentElement; parent; parent = parent.parentElement) {
      const style = view.getComputedStyle(parent), bounds = parent.getBoundingClientRect();
      if (/auto|scroll|hidden|clip/.test(style.overflowX)) { left = Math.max(left, bounds.left); right = Math.min(right, bounds.right); }
      if (/auto|scroll|hidden|clip/.test(style.overflowY)) { top = Math.max(top, bounds.top); bottom = Math.min(bottom, bounds.bottom); }
    }
    if (rect.right <= left || rect.left >= right || rect.bottom <= top || rect.top >= bottom) { close(root); return; }
    position();
  }
  function tick() { update(); if (!stopped) frame = view.requestAnimationFrame(tick); }
  const events = [[doc, 'scroll', true], [view, 'resize', false]];
  if (view.visualViewport) events.push([view.visualViewport, 'resize', false], [view.visualViewport, 'scroll', false]);
  for (const [target, name, capture] of events) EventTarget.prototype.addEventListener.call(target, name, update, { capture, passive: true });
  root._referenceStopPosition = () => {
    stopped = true; view.cancelAnimationFrame(frame);
    for (const [target, name, capture] of events) EventTarget.prototype.removeEventListener.call(target, name, update, capture);
    root._referenceStopPosition = null;
  };
  // RAF also tracks transforms, content expansion and sidebar width changes,
  // which do not necessarily dispatch scroll or resize events.
  update(); if (!stopped) frame = view.requestAnimationFrame(tick);
  return !stopped;
}
function hideFloatingPanel(panel) {
  if (panel.matches(':popover-open')) panel.hidePopover();
  panel.hidden = true;
}
export function ensureReferenceControls() {
  if (installed) return;
  // qiankun apps bundle their own module copy but share one DOM. Register the
  // original delegated listeners only once across portal and embedded apps.
  const existing = document[runtimeKey];
  if (existing) {
    window.__assembleaiReferenceSelect = existing.select;
    window.__assembleaiReferenceDateTime = existing.dateTime;
    window.__assembleaiReferenceDateRange = existing.dateRange;
    installed = true;
    return;
  }
  installed = true;
  /* —— Custom select —— */
  (function initUiSelect() {
    var openRoot = null;

    function positionMenu(root) {
      var menu = root.querySelector('.ui-select__menu');
      var trigger = root.querySelector('.ui-select__trigger');
      if (!menu || !trigger) return;
      var rect = trigger.getBoundingClientRect();
      var width = Math.min(rect.width, window.innerWidth - 16);
      // Preserve the original 4px gap and menu width in viewport coordinates.
      Object.assign(menu.style, { position: 'fixed', right: 'auto', bottom: 'auto', minWidth: '0', width: width + 'px',
        left: Math.max(8, Math.min(rect.left, window.innerWidth - width - 8)) + 'px' });
      var above = root.getAttribute('data-placement') === 'top';
      if (!above && rect.bottom + menu.offsetHeight + 4 > window.innerHeight - 8 && rect.top > menu.offsetHeight + 4) above = true;
      menu.style.top = (above ? Math.max(8, rect.top - menu.offsetHeight - 4) : rect.bottom + 4) + 'px';
      menu.style.transformOrigin = above ? 'bottom center' : 'top center';
    }

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
      root._referenceStopPosition?.();
      var menu = root.querySelector(".ui-select__menu");
      var trigger = root.querySelector(".ui-select__trigger");
      root.classList.remove("is-open");
      if (trigger && root.contains(document.activeElement)) trigger.focus({ preventScroll: true });
      if (trigger) trigger.setAttribute("aria-expanded", "false");
      if (menu) {
        if (motionOn()) {
          var t = setTimeout(function () {
            if (!root.classList.contains("is-open")) hideFloatingPanel(menu);
          }, 160);
          root._uiCloseTimer = t;
        } else {
          hideFloatingPanel(menu);
        }
      }
      if (openRoot === root) openRoot = null;
    }

    function open(root) {
      if (!root || root.querySelector("button")?.matches(":disabled")) return;
      if (openRoot && openRoot !== root) close(openRoot);
      if (window.__assembleaiReferenceDateTime && window.__assembleaiReferenceDateTime.closeAll) window.__assembleaiReferenceDateTime.closeAll();
      if (window.__assembleaiReferenceDateRange && window.__assembleaiReferenceDateRange.closeAll) window.__assembleaiReferenceDateRange.closeAll();
      if (root._uiCloseTimer) {
        clearTimeout(root._uiCloseTimer);
        root._uiCloseTimer = null;
      }
      var menu = root.querySelector(".ui-select__menu");
      var trigger = root.querySelector(".ui-select__trigger");
      if (menu) {
        if (!showFloatingPanel(root, menu, trigger, () => positionMenu(root), close)) return;
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
        var root = trigger.closest("[data-ui-select][data-reference-control]");
        if (!root) return;
        e.preventDefault();
        toggle(root);
        return;
      }
      var option = e.target.closest(".ui-select__option");
      if (option && !option.disabled) {
        var root = option.closest("[data-ui-select][data-reference-control]");
        if (!root) return;
        e.preventDefault();
        setValue(root, option.getAttribute("data-value") || "");
        close(root);
        return;
      }
      if (openRoot && !e.target.closest("[data-ui-select][data-reference-control]")) close(openRoot);
    });

    document.addEventListener("keydown", function (e) {
      if (!openRoot) return;
      if (openRoot.querySelector("button")?.matches(":disabled")) { close(openRoot); return; }
      if (e.key === "Escape") {
        e.preventDefault();
        close(openRoot);
        return;
      }
      if (e.key === "ArrowDown" || e.key === "ArrowUp") {
        e.preventDefault();
        var opts = Array.prototype.slice.call(openRoot.querySelectorAll(".ui-select__option:not(:disabled)"));
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

    window.__assembleaiReferenceSelect = {
      get: getValue,
      close: close,
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

          vp._referenceDispose = function () { drag = null; unbindGestureDocs(); stopWheelAnim(vp); stopTrackInertia(); clearTimeout(vp._uiWheelUnlock); };

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

          vp.addEventListener("keydown", function (e) {
            if (!["ArrowUp", "ArrowDown", "Home", "End"].includes(e.key)) return;
            e.preventDefault();
            var index = currentIndex();
            selectItemByIndex(e.key === "Home" ? 0 : e.key === "End" ? max : Math.max(0, Math.min(max, index + (e.key === "ArrowDown" ? 1 : -1))));
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
      root._referenceStopPosition?.();
      var panel = root.querySelector(".ui-datetime__panel");
      var trigger = root.querySelector(".ui-datetime__trigger");
      var state = getDtState(root);
      for (const wheel of root.querySelectorAll("[data-wheel-scroll]")) wheel._referenceDispose?.();
      state.mode = "day";
      root.classList.remove("is-open");
      if (trigger && root.contains(document.activeElement)) trigger.focus({ preventScroll: true });
      if (trigger) trigger.setAttribute("aria-expanded", "false");
      if (panel) {
        if (motionOn()) {
          var t = setTimeout(function () {
            if (!root.classList.contains("is-open")) hideFloatingPanel(panel);
          }, 160);
          root._uiDtCloseTimer = t;
        } else {
          hideFloatingPanel(panel);
        }
      }
      if (openDt === root) openDt = null;
    }

    function openDtRoot(root) {
      if (!root || root.querySelector("button")?.matches(":disabled")) return;
      if (window.__assembleaiReferenceSelect && window.__assembleaiReferenceSelect.closeAll) window.__assembleaiReferenceSelect.closeAll();
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
        syncDtPanel(root);
        if (!showFloatingPanel(root, panel, trigger, () => positionPanel(panel, trigger), closeDt)) return;
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
      root._referenceStopPosition?.();
      var panel = root.querySelector(".ui-daterange__panel");
      var trigger = root.querySelector(".ui-daterange__trigger");
      var state = getDrState(root);
      state.mode = "day";
      root.classList.remove("is-open");
      if (trigger && root.contains(document.activeElement)) trigger.focus({ preventScroll: true });
      if (trigger) trigger.setAttribute("aria-expanded", "false");
      if (panel) {
        if (motionOn()) {
          var t = setTimeout(function () {
            if (!root.classList.contains("is-open")) hideFloatingPanel(panel);
          }, 160);
          root._uiDrCloseTimer = t;
        } else {
          hideFloatingPanel(panel);
        }
      }
      if (openDr === root) openDr = null;
    }

    function openDrRoot(root) {
      if (!root || root.querySelector("button")?.matches(":disabled")) return;
      if (window.__assembleaiReferenceSelect && window.__assembleaiReferenceSelect.closeAll) window.__assembleaiReferenceSelect.closeAll();
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
        syncDrPanel(root);
        if (!showFloatingPanel(root, panel, trigger, () => positionPanel(panel, trigger), closeDr)) return;
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

    document.querySelectorAll("[data-ui-datetime][data-reference-control]").forEach(function (root) {
      getDtState(root);
      syncDtTrigger(root);
    });
    document.querySelectorAll("[data-ui-daterange][data-reference-control]").forEach(function (root) {
      getDrState(root);
      syncDrTrigger(root);
    });

    document.addEventListener("click", function (e) {
      var dtTrigger = e.target.closest(".ui-datetime__trigger");
      if (dtTrigger) {
        var root = dtTrigger.closest("[data-ui-datetime][data-reference-control]");
        if (!root) return;
        e.preventDefault();
        toggleDt(root);
        return;
      }

      var drTrigger = e.target.closest(".ui-daterange__trigger");
      if (drTrigger) {
        var root2 = drTrigger.closest("[data-ui-daterange][data-reference-control]");
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
        if (!e.target.closest("[data-ui-datetime][data-reference-control]")) closeDt(dtRoot);
      }

      if (openDr) {
        var drRoot = openDr;
        var panel2 = drRoot.querySelector(".ui-daterange__panel");
        if (panel2 && panel2.contains(e.target)) {
          handleDrPanelClick(e, drRoot, panel2);
          return;
        }
        if (!e.target.closest("[data-ui-daterange][data-reference-control]")) closeDr(drRoot);
      }
    });

    document.addEventListener("keydown", function (e) {
      if (e.key !== "Escape") return;
      if (openDt || openDr) e.preventDefault();
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

    window.__assembleaiReferenceDateTime = {
      get: getDt,
      set: function (root, value, silent) { setDtValue(root, value, !!silent); },
      open: openDtRoot,
      close: closeDt,
      closeAll: function () { if (openDt) closeDt(openDt); }
    };

    window.__assembleaiReferenceDateRange = {
      get: getDr,
      set: function (root, start, end, silent) { setDrValue(root, start, end, !!silent); },
      open: openDrRoot,
      close: closeDr,
      closeAll: function () { if (openDr) closeDr(openDr); }
    };
  })();

  document[runtimeKey] = { select: window.__assembleaiReferenceSelect, dateTime: window.__assembleaiReferenceDateTime, dateRange: window.__assembleaiReferenceDateRange };
}
export function setReferenceSelect(root, value) {
  ensureReferenceControls(); window.__assembleaiReferenceSelect.set(root, value);
}
export function setReferenceDateTime(root, value) {
  ensureReferenceControls();
  if (window.__assembleaiReferenceDateTime.get(root) !== (value ? value.slice(0,16).replace(" ", "T") : null)) window.__assembleaiReferenceDateTime.set(root, value, true);
}
export function closeReferenceControl(root) {
  if (!installed || !root) return;
  if (root.matches('[data-ui-select]')) window.__assembleaiReferenceSelect.close(root);
  if (root.matches('[data-ui-datetime]')) window.__assembleaiReferenceDateTime.close(root);
  if (root.matches('[data-ui-daterange]')) window.__assembleaiReferenceDateRange.close(root);
}
export function releaseReferenceControl(root) {
  closeReferenceControl(root);
  root._referenceStopPosition?.();
  for (const key of ['_uiCloseTimer', '_uiDtCloseTimer', '_uiDrCloseTimer']) clearTimeout(root[key]);
  for (const panel of root.querySelectorAll('.ui-select__menu,.ui-datetime__panel,.ui-daterange__panel')) hideFloatingPanel(panel);
  for (const wheel of root.querySelectorAll('[data-wheel-scroll]')) wheel._referenceDispose?.();
}
