// The illustration viewer: full-window black, fit to window, wheel and pinch zoom, drag to pan, arrow keys and swipes
// to the neighbouring illustrations of the grid (which are loaded ahead).

import { api, urls } from "./api.js";
import { t, textElement } from "./i18n.js";
import { iconButton } from "./icons.js";
import { back, setQuietly } from "./router.js";

const MAX_ZOOM = 8;
const SWIPE = 60;
const TAP_MOVE = 8;
const DOUBLE_TAP_MS = 300;
const CHROME_MS = 2500;

/**
 * Opens illustration `id` in `root`. `context` gives `adjacent(id, step)`, `cached(id)` and `toast(text)`.
 * Returns `{ close() }`.
 */
export function openViewer(root, id, context) {
  let currentId = id;
  let width = 0;
  let height = 0;
  let fit = 1;
  let scale = 1;
  let x = 0;
  let y = 0;
  let chromeTimer = 0;
  let lastTap = 0;
  let closed = false;
  const pointers = new Map();
  let drag = null;
  let pinch = null;

  root.className = "overlay viewer";
  const stage = document.createElement("div");
  stage.className = "stage";
  const photo = document.createElement("img");
  photo.className = "photo";
  photo.alt = "";
  photo.draggable = false;
  stage.append(photo);

  const top = document.createElement("div");
  top.className = "chrome top";
  const backButton = iconButton("back", "WebUi.Web.Back", t);
  const title = document.createElement("span");
  title.className = "title";
  top.append(backButton, title);

  const previous = iconButton("left", "WebUi.Web.Viewer.Previous", t, "nav prev");
  const next = iconButton("right", "WebUi.Web.Viewer.Next", t, "nav next");
  const message = document.createElement("div");
  message.className = "overlay-message";
  message.hidden = true;
  message.append(textElement("p", "WebUi.Web.Viewer.CannotShow"));

  root.replaceChildren(stage, top, previous, next, message);
  root.hidden = false;

  backButton.addEventListener("click", () => back("#/kind/illustration"));
  previous.addEventListener("click", () => go(-1));
  next.addEventListener("click", () => go(1));
  photo.addEventListener("load", () => {
    width = photo.naturalWidth;
    height = photo.naturalHeight;
    photo.style.width = `${width}px`;
    photo.style.height = `${height}px`;
    message.hidden = true;
    reset();
  });
  photo.addEventListener("error", () => {
    if (photo.getAttribute("src")) {
      message.hidden = false;
      photo.hidden = true;
    }
  });

  stage.addEventListener("wheel", onWheel, { passive: false });
  stage.addEventListener("pointerdown", onPointerDown);
  stage.addEventListener("pointermove", onPointerMove);
  stage.addEventListener("pointerup", onPointerUp);
  stage.addEventListener("pointercancel", onPointerUp);
  root.addEventListener("pointermove", (event) => {
    if (event.pointerType === "mouse") {
      showChrome();
    }
  });
  window.addEventListener("keydown", onKey);
  window.addEventListener("resize", reset);

  show(id);
  showChrome();

  async function show(showId) {
    currentId = showId;
    photo.hidden = false;
    photo.style.transform = "translate(-50%, -50%) scale(0)";
    photo.src = urls.file(showId);
    const known = context.cached(showId);
    title.textContent = known?.title ?? "";
    if (!known) {
      api.item(showId).then((item) => {
        if (currentId === showId) {
          title.textContent = item.title;
        }
      }).catch(() => {});
    }

    api.patch(showId, { markOpened: true }).catch(() => {});
    for (const step of [1, -1]) {
      context.adjacent(showId, step).then((item) => {
        if (item) {
          new Image().src = urls.file(item.id);
        }
      });
    }
  }

  async function go(step) {
    const from = currentId;
    const item = await context.adjacent(from, step);
    if (closed || from !== currentId) {
      return;
    }

    if (!item) {
      context.toast(t(step > 0 ? "WebUi.Web.Viewer.Last" : "WebUi.Web.Viewer.First"));
      return;
    }

    setQuietly(`#/view/${item.id}`);
    show(item.id);
  }

  // ---------- Zoom and pan ----------

  function reset() {
    if (!width || !height) {
      return;
    }

    fit = Math.min(stage.clientWidth / width, stage.clientHeight / height);
    scale = fit;
    x = 0;
    y = 0;
    apply();
  }

  function zoomed() {
    return scale > fit * 1.001;
  }

  function apply() {
    const overflowX = Math.max(0, (width * scale - stage.clientWidth) / 2);
    const overflowY = Math.max(0, (height * scale - stage.clientHeight) / 2);
    x = Math.min(overflowX, Math.max(-overflowX, x));
    y = Math.min(overflowY, Math.max(-overflowY, y));
    photo.style.transform = `translate(-50%, -50%) translate(${x}px, ${y}px) scale(${scale})`;
  }

  /** Zooms to `target`, keeping the picture point under (px, py) in place (stage centre coordinates). */
  function zoomAt(target, px, py, fromScale = scale, fromX = x, fromY = y) {
    const clamped = Math.min(Math.max(fit * MAX_ZOOM, 4), Math.max(fit, target));
    const u = (px - fromX) / fromScale;
    const v = (py - fromY) / fromScale;
    scale = clamped;
    x = px - u * scale;
    y = py - v * scale;
    apply();
  }

  function centred(event) {
    const box = stage.getBoundingClientRect();
    return [event.clientX - box.left - box.width / 2, event.clientY - box.top - box.height / 2];
  }

  function onWheel(event) {
    event.preventDefault();
    const [px, py] = centred(event);
    zoomAt(scale * Math.exp(-event.deltaY * 0.0015), px, py);
  }

  function onPointerDown(event) {
    stage.setPointerCapture(event.pointerId);
    pointers.set(event.pointerId, { x: event.clientX, y: event.clientY });
    if (pointers.size === 1) {
      drag = { startX: event.clientX, startY: event.clientY, x, y, moved: false };
      pinch = null;
    } else if (pointers.size === 2) {
      const [a, b] = [...pointers.values()];
      const box = stage.getBoundingClientRect();
      pinch = {
        distance: Math.hypot(a.x - b.x, a.y - b.y) || 1,
        cx: (a.x + b.x) / 2 - box.left - box.width / 2,
        cy: (a.y + b.y) / 2 - box.top - box.height / 2,
        scale,
        x,
        y,
      };
      drag = null;
    }
  }

  function onPointerMove(event) {
    if (!pointers.has(event.pointerId)) {
      return;
    }

    pointers.set(event.pointerId, { x: event.clientX, y: event.clientY });
    if (pinch && pointers.size >= 2) {
      const [a, b] = [...pointers.values()];
      const box = stage.getBoundingClientRect();
      const cx = (a.x + b.x) / 2 - box.left - box.width / 2;
      const cy = (a.y + b.y) / 2 - box.top - box.height / 2;
      zoomAt(pinch.scale * (Math.hypot(a.x - b.x, a.y - b.y) / pinch.distance), pinch.cx, pinch.cy, pinch.scale, pinch.x, pinch.y);
      x += cx - pinch.cx;
      y += cy - pinch.cy;
      apply();
      return;
    }

    if (drag) {
      const dx = event.clientX - drag.startX;
      const dy = event.clientY - drag.startY;
      if (Math.hypot(dx, dy) > TAP_MOVE) {
        drag.moved = true;
      }

      if (zoomed()) {
        x = drag.x + dx;
        y = drag.y + dy;
        apply();
      }
    }
  }

  function onPointerUp(event) {
    pointers.delete(event.pointerId);
    if (pinch) {
      if (pointers.size === 0) {
        pinch = null;
        if (!zoomed()) {
          reset();
        }
      }

      return;
    }

    if (!drag) {
      return;
    }

    const dx = event.clientX - drag.startX;
    const dy = event.clientY - drag.startY;
    const wasZoomed = zoomed();
    const moved = drag.moved;
    drag = null;
    if (event.type === "pointercancel") {
      return;
    }

    if (moved) {
      if (!wasZoomed && Math.abs(dx) > SWIPE && Math.abs(dx) > Math.abs(dy)) {
        go(dx < 0 ? 1 : -1);
      }

      return;
    }

    const now = Date.now();
    if (now - lastTap < DOUBLE_TAP_MS) {
      lastTap = 0;
      const [px, py] = centred(event);
      if (wasZoomed) {
        reset();
      } else {
        zoomAt(fit * 2.5, px, py);
      }

      return;
    }

    lastTap = now;
    if (event.pointerType !== "mouse") {
      toggleChrome();
    }
  }

  function onKey(event) {
    if (event.altKey || event.ctrlKey || event.metaKey) {
      return;
    }

    switch (event.key) {
      case "ArrowLeft":
        go(-1);
        break;
      case "ArrowRight":
        go(1);
        break;
      case "Escape":
        back("#/kind/illustration");
        break;
      case "+":
      case "=":
        zoomAt(scale * 1.25, 0, 0);
        break;
      case "-":
        zoomAt(scale / 1.25, 0, 0);
        break;
      case "0":
        reset();
        break;
      default:
        return;
    }

    event.preventDefault();
  }

  // ---------- Chrome ----------

  function showChrome() {
    root.classList.remove("chrome-hidden");
    clearTimeout(chromeTimer);
    chromeTimer = setTimeout(() => root.classList.add("chrome-hidden"), CHROME_MS);
  }

  function toggleChrome() {
    clearTimeout(chromeTimer);
    root.classList.toggle("chrome-hidden");
  }

  return {
    close() {
      closed = true;
      clearTimeout(chromeTimer);
      window.removeEventListener("keydown", onKey);
      window.removeEventListener("resize", reset);
      root.hidden = true;
      root.replaceChildren();
      root.className = "overlay";
    },
  };
}
