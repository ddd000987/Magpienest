// The comic reader: pages paired into frames by the server (the same FrameLayout as the desktop), keys, click or tap
// thirds and swipes as in the desktop viewer (direction-aware), a page slider, and toggles for spread, direction and
// cover-alone that are saved with the book. Progress is sent at most once a second and when the reader closes.

import { api, urls, beacon } from "./api.js";
import { t, label, textElement, formatNumber } from "./i18n.js";
import { icon, iconButton } from "./icons.js";
import { back } from "./router.js";

const IDLE_LIMIT_MS = 5 * 60 * 1000;
const SAVE_EVERY_MS = 1000;
const TIME_FLUSH_MS = 30 * 1000;
const SWIPE = 60;
const TAP_MOVE = 10;
const WHEEL_GAP_MS = 250;
const PORTRAIT = 0.7;
const CHROME_MS = 2500;

/** Opens book `id` in `root`. `context.toast(text)` shows a short message. Returns `{ close() }`. */
export function openReader(root, id, context) {
  let book = null;
  let frame = null;
  let spread = "double";
  let direction = "rtl";
  let coverAlone = true;
  let closed = false;
  let chain = Promise.resolve();
  let pending = 0;
  const ratios = new Map();

  // Progress and time spent.
  let lastInput = Date.now();
  let seconds = 0;
  let opened = false;
  let unsent = null;
  let lastSave = 0;
  let saveTimer = 0;
  let lastWheel = 0;
  let drag = null;
  let chromeTimer = 0;

  root.className = "overlay reader";
  const stage = document.createElement("div");
  stage.className = "stage";
  const pages = document.createElement("div");
  pages.className = "spread";
  stage.append(pages);

  const top = document.createElement("div");
  top.className = "chrome top";
  const backButton = iconButton("back", "WebUi.Web.Back", t);
  const title = document.createElement("span");
  title.className = "title";
  const pageText = document.createElement("span");
  pageText.className = "page-text";
  top.append(backButton, title, pageText);

  const bottom = document.createElement("div");
  bottom.className = "chrome bottom";
  const slider = document.createElement("input");
  slider.type = "range";
  slider.className = "slider";
  slider.min = "0";
  slider.step = "1";
  slider.setAttribute("data-i18n-aria-label", "WebUi.Web.Reader.Page");
  slider.setAttribute("aria-label", t("WebUi.Web.Reader.Page"));
  const toggles = document.createElement("div");
  toggles.className = "toggles";
  const spreadButton = iconButton("spread", "WebUi.Web.Reader.Spread", t);
  const directionButton = iconButton("rtl", "WebUi.Web.Reader.Direction", t);
  const coverButton = iconButton("cover", "WebUi.Web.Reader.CoverAlone", t);
  toggles.append(spreadButton, directionButton, coverButton);
  bottom.append(slider, toggles);

  const message = document.createElement("div");
  message.className = "overlay-message";
  message.hidden = true;

  root.replaceChildren(stage, top, bottom, message);
  root.hidden = false;

  backButton.addEventListener("click", leave);
  spreadButton.addEventListener("click", () => toggle("spread"));
  directionButton.addEventListener("click", () => toggle("direction"));
  coverButton.addEventListener("click", () => toggle("cover"));
  slider.addEventListener("input", () => {
    touch();
    const page = Number(slider.value);
    label(pageText, "WebUi.Web.Reader.PageOf", formatNumber(page + 1), formatNumber(book.count));
  });
  slider.addEventListener("change", () => goTo(Number(slider.value)));
  stage.addEventListener("pointerdown", onPointerDown);
  stage.addEventListener("pointerup", onPointerUp);
  stage.addEventListener("pointercancel", () => (drag = null));
  stage.addEventListener("wheel", onWheel, { passive: false });
  root.addEventListener("pointermove", (event) => {
    touch();
    if (event.pointerType === "mouse") {
      showChrome();
    }
  });
  window.addEventListener("keydown", onKey);
  window.addEventListener("resize", layout);
  window.addEventListener("pagehide", flushOnLeave);
  document.addEventListener("visibilitychange", onVisibility);
  const ticker = setInterval(tick, 1000);

  start();
  showChrome();

  async function start() {
    try {
      const [info, item] = await Promise.all([api.book(id), api.item(id)]);
      if (closed) {
        return;
      }

      book = info;
      spread = info.spread;
      direction = info.direction;
      coverAlone = info.coverAlone;
      title.textContent = item.title;
      if (info.count === 0) {
        showMessage("WebUi.Web.Reader.NoPages");
        return;
      }

      slider.max = String(info.count - 1);
      showSettings();
      await navigate(() => load(info.position, "forward"));
    } catch {
      if (!closed) {
        showMessage("WebUi.Web.Reader.CannotOpen");
      }
    }
  }

  function showMessage(key) {
    message.replaceChildren(textElement("p", key));
    message.hidden = false;
    bottom.hidden = true;
  }

  // ---------- Frames ----------

  function load(at, dir) {
    return api.frame(id, { at, dir, spread, coverAlone });
  }

  /** Runs navigations one after another so quick key presses page in order; more than three waiting are dropped. */
  function navigate(next) {
    if (pending >= 3) {
      return chain;
    }

    pending++;
    chain = chain.then(async () => {
      try {
        const target = await next();
        if (target && !closed) {
          showFrame(target);
        }
      } catch {
        if (!closed) {
          context.toast(t("WebUi.Web.Error.Load"));
        }
      } finally {
        pending--;
      }
    });
    return chain;
  }

  function forward() {
    touch();
    hideChrome();
    navigate(() => {
      if (frame.last + 1 >= book.count) {
        context.toast(t("WebUi.Web.Reader.LastPage"));
        return null;
      }

      return load(frame.last + 1, "forward");
    });
  }

  function backward() {
    touch();
    hideChrome();
    navigate(() => {
      if (frame.first <= 0) {
        context.toast(t("WebUi.Web.Reader.FirstPage"));
        return null;
      }

      return load(frame.first - 1, "backward");
    });
  }

  function goTo(page) {
    touch();
    navigate(() => load(Math.min(book.count - 1, Math.max(0, page)), "forward"));
  }

  function showFrame(next) {
    frame = next;
    const indices = [];
    for (let i = next.first; i <= next.last; i++) {
      indices.push(i);
    }

    pages.replaceChildren(...indices.map((index) => {
      const image = document.createElement("img");
      image.alt = "";
      image.draggable = false;
      image.dataset.index = String(index);
      image.addEventListener("load", () => {
        if (image.naturalHeight > 0) {
          ratios.set(index, image.naturalWidth / image.naturalHeight);
          layout();
        }
      });
      image.src = urls.page(id, index);
      return image;
    }));
    layout();

    slider.value = String(next.first);
    if (next.first === next.last) {
      label(pageText, "WebUi.Web.Reader.PageOf", formatNumber(next.first + 1), formatNumber(book.count));
    } else {
      label(pageText, "WebUi.Web.Reader.PagesOf", formatNumber(next.first + 1), formatNumber(next.last + 1), formatNumber(book.count));
    }

    // Load the pages around this frame ahead.
    for (const index of [next.last + 1, next.last + 2, next.first - 1, next.first - 2]) {
      if (index >= 0 && index < book.count) {
        new Image().src = urls.page(id, index);
      }
    }

    record({ progressPosition: next.first, progressTotal: book.count, reachedEnd: next.last >= book.count - 1 });
  }

  /** Both pages get the same height and sit edge to edge; the pair fits the window. */
  function layout() {
    const images = [...pages.children];
    if (images.length === 0) {
      return;
    }

    const shares = images.map((image) => ratios.get(Number(image.dataset.index)) ?? PORTRAIT);
    const sum = shares.reduce((a, b) => a + b, 0);
    const height = Math.min(stage.clientHeight, stage.clientWidth / sum);
    images.forEach((image, i) => {
      image.style.height = `${Math.floor(height)}px`;
      image.style.width = `${Math.floor(height * shares[i])}px`;
    });
  }

  // ---------- Settings ----------

  function showSettings() {
    pages.classList.toggle("rtl", direction === "rtl");
    slider.dir = direction === "rtl" ? "rtl" : "ltr";
    spreadButton.replaceChildren(icon(spread === "double" ? "spread" : "single"));
    spreadButton.setAttribute("aria-pressed", String(spread === "double"));
    directionButton.replaceChildren(icon(direction === "rtl" ? "rtl" : "ltr"));
    directionButton.setAttribute("aria-pressed", String(direction === "rtl"));
    coverButton.setAttribute("aria-pressed", String(coverAlone));
  }

  function toggle(which) {
    touch();
    let toast;
    let change;
    if (which === "spread") {
      spread = spread === "double" ? "single" : "double";
      change = { spread };
      toast = spread === "double" ? "WebUi.Web.Reader.DoublePage" : "WebUi.Web.Reader.SinglePage";
    } else if (which === "direction") {
      direction = direction === "rtl" ? "ltr" : "rtl";
      change = { direction };
      toast = direction === "rtl" ? "WebUi.Web.Reader.RightToLeft" : "WebUi.Web.Reader.LeftToRight";
    } else {
      coverAlone = !coverAlone;
      change = { coverAlone };
      toast = coverAlone ? "WebUi.Web.Reader.CoverAloneOn" : "WebUi.Web.Reader.CoverAloneOff";
    }

    showSettings();
    context.toast(t(toast));
    api.patch(id, { extra: change }).catch(() => context.toast(t("WebUi.Web.Error.Save")));
    if (frame) {
      const keep = frame.first;
      navigate(() => load(keep, "forward"));
    }
  }

  // ---------- Input ----------

  /** "Left" is forward when reading right to left, backward otherwise. */
  function left() {
    if (direction === "rtl") {
      forward();
    } else {
      backward();
    }
  }

  function right() {
    if (direction === "rtl") {
      backward();
    } else {
      forward();
    }
  }

  function onKey(event) {
    if (event.altKey || event.ctrlKey || event.metaKey || !frame) {
      if (event.key === "Escape") {
        leave();
      }

      return;
    }

    switch (event.key) {
      case "ArrowLeft":
        left();
        break;
      case "ArrowRight":
        right();
        break;
      case " ":
      case "PageDown":
        forward();
        break;
      case "Backspace":
      case "PageUp":
        backward();
        break;
      case "Home":
        goTo(0);
        break;
      case "End":
        goTo(book.count - 1);
        break;
      case "d":
      case "D":
        toggle("spread");
        break;
      case "m":
      case "M":
        toggle("direction");
        break;
      case "s":
      case "S":
        toggle("cover");
        break;
      case "Escape":
        leave();
        break;
      default:
        return;
    }

    event.preventDefault();
  }

  function onWheel(event) {
    event.preventDefault();
    const now = Date.now();
    if (!frame || now - lastWheel < WHEEL_GAP_MS || event.deltaY === 0) {
      return;
    }

    lastWheel = now;
    if (event.deltaY > 0) {
      forward();
    } else {
      backward();
    }
  }

  function onPointerDown(event) {
    touch();
    drag = { x: event.clientX, y: event.clientY };
  }

  function onPointerUp(event) {
    if (!drag || !frame) {
      drag = null;
      return;
    }

    const dx = event.clientX - drag.x;
    const dy = event.clientY - drag.y;
    drag = null;
    if (Math.hypot(dx, dy) > TAP_MOVE) {
      // A swipe to the right works like the Left key (it pulls the page on the left into view).
      if (Math.abs(dx) > SWIPE && Math.abs(dx) > Math.abs(dy)) {
        if (dx > 0) {
          left();
        } else {
          right();
        }
      }

      return;
    }

    const box = stage.getBoundingClientRect();
    const third = (event.clientX - box.left) / box.width;
    if (third < 1 / 3) {
      left();
    } else if (third > 2 / 3) {
      right();
    } else {
      clearTimeout(chromeTimer);
      root.classList.toggle("chrome-hidden");
    }
  }

  // ---------- Chrome ----------

  /** The bars show while the mouse moves and hide again after a moment; paging hides them at once. */
  function showChrome() {
    root.classList.remove("chrome-hidden");
    clearTimeout(chromeTimer);
    chromeTimer = setTimeout(() => {
      if (!bottom.contains(document.activeElement)) {
        root.classList.add("chrome-hidden");
      }
    }, CHROME_MS);
  }

  function hideChrome() {
    clearTimeout(chromeTimer);
    root.classList.add("chrome-hidden");
  }

  // ---------- Progress ----------

  function touch() {
    lastInput = Date.now();
  }

  function tick() {
    if (document.visibilityState === "visible" && Date.now() - lastInput < IDLE_LIMIT_MS && frame) {
      seconds++;
      if (seconds * 1000 >= TIME_FLUSH_MS) {
        record({});
      }
    }
  }

  /** Keeps the latest progress and sends it, at most once a second. */
  function record(change) {
    const reachedEnd = Boolean(unsent?.reachedEnd || change.reachedEnd);
    unsent = { ...(unsent ?? {}), ...change, reachedEnd };
    const wait = SAVE_EVERY_MS - (Date.now() - lastSave);
    clearTimeout(saveTimer);
    if (wait <= 0) {
      save();
    } else {
      saveTimer = setTimeout(save, wait);
    }
  }

  function takeUnsent() {
    if (!unsent && seconds === 0) {
      return null;
    }

    const body = { ...(unsent ?? {}) };
    if (!body.reachedEnd) {
      delete body.reachedEnd;
    }

    if (seconds > 0) {
      body.addTimeSpentSeconds = seconds;
    }

    if (!opened) {
      body.markOpened = true;
      opened = true;
    }

    unsent = null;
    seconds = 0;
    return body;
  }

  function save() {
    const body = takeUnsent();
    lastSave = Date.now();
    if (body) {
      api.patch(id, body).catch(() => {});
    }
  }

  function flushOnLeave() {
    clearTimeout(saveTimer);
    const body = takeUnsent();
    if (body) {
      beacon(id, body);
    }
  }

  function onVisibility() {
    if (document.visibilityState === "hidden") {
      flushOnLeave();
    }
  }

  function leave() {
    back("#/kind/comic");
  }

  return {
    close() {
      closed = true;
      flushOnLeave();
      clearInterval(ticker);
      clearTimeout(chromeTimer);
      window.removeEventListener("keydown", onKey);
      window.removeEventListener("resize", layout);
      window.removeEventListener("pagehide", flushOnLeave);
      document.removeEventListener("visibilitychange", onVisibility);
      root.hidden = true;
      root.replaceChildren();
      root.className = "overlay";
    },
  };
}
