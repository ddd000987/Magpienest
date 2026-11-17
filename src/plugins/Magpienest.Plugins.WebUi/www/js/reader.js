// The comic reader: pages paired into frames by the server (the same FrameLayout as the desktop), or one continuous
// strip (strip.js); keys, click or tap thirds and swipes as in the desktop viewer (direction-aware), a page slider,
// and toggles for pages shown (one, two or strip), direction and cover-alone that are saved with the book. Every wheel
// notch turns a page at once: the next frames and their pages are fetched ahead. Progress is sent at most once a second
// and when the reader closes.

import { api, urls, beacon } from "./api.js";
import { t, label, textElement, formatNumber } from "./i18n.js";
import { icon, iconButton } from "./icons.js";
import { back } from "./router.js";
import { createStrip } from "./strip.js";

const IDLE_LIMIT_MS = 5 * 60 * 1000;
const SAVE_EVERY_MS = 1000;
const TIME_FLUSH_MS = 30 * 1000;
const SWIPE = 60;
const TAP_MOVE = 10;
const WHEEL_STEP = 100;    // wheel distance (px) that turns a page; one mouse notch is about this
const WHEEL_RESET_MS = 250; // a touchpad's leftover distance is forgotten after this pause
const DECODE_WAIT_MS = 300; // how long a turn waits for its pages before showing them anyway
const FRAMES_AHEAD = 2;     // page pairs asked for ahead of the reader
const PORTRAIT = 0.7;
const CHROME_MS = 2500;
const STRIP_PAGE = 0.9;   // share of the window a page key or a tap scrolls in a strip
const STRIP_ARROW = 0.5;  // share an arrow key scrolls

/** Opens book `id` in `root`. `context.toast(text)` shows a short message. Returns `{ close() }`. */
export function openReader(root, id, context) {
  let book = null;
  let frame = null;
  let spread = "double";
  let direction = "rtl";
  let coverAlone = true;
  let flow = "paged";
  let strip = null;
  let stripCurrent = 0;
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
  let wheelSum = 0;
  let wheelTimer = 0;
  const frames = new Map();   // "dir:at" -> Promise of a frame, so turning a page needs no wait for the server
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
      flow = info.flow === "strip" ? "strip" : "paged";
      title.textContent = item.title;
      if (info.count === 0) {
        showMessage("WebUi.Web.Reader.NoPages");
        return;
      }

      slider.max = String(info.count - 1);
      showSettings();
      if (flow === "strip") {
        enterStrip(info.position);
      } else {
        await navigate(() => load(info.position, "forward"));
      }
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

  /** The frame at `at` going `dir`, from the cache or the server. */
  function load(at, dir) {
    const key = `${dir}:${at}`;
    let found = frames.get(key);
    if (!found) {
      if (frames.size > 64) {
        frames.clear();
      }

      found = api.frame(id, { at, dir, spread, coverAlone });
      frames.set(key, found);
      found.catch(() => frames.delete(key));
    }

    return found;
  }

  /** Asks for the next frames on both sides ahead, so the following turns are immediate. */
  async function prefetch(from) {
    try {
      let next = from;
      for (let i = 0; i < FRAMES_AHEAD && next.last + 1 < book.count; i++) {
        next = await load(next.last + 1, "forward");
        preload(next);
      }

      if (from.first > 0) {
        preload(await load(from.first - 1, "backward"));
      }
    } catch {
      // Asked for again when the reader gets there.
    }
  }

  function preload(target) {
    for (let i = target.first; i <= target.last; i++) {
      new Image().src = urls.page(id, i);
    }
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
        if (target && !closed && !strip) {
          await showFrame(target);
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
    if (strip) {
      strip.scrollBy(STRIP_PAGE);
      return;
    }

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
    if (strip) {
      strip.scrollBy(-STRIP_PAGE);
      return;
    }

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
    if (strip) {
      strip.goTo(page);
      return;
    }

    navigate(() => load(Math.min(book.count - 1, Math.max(0, page)), "forward"));
  }

  /** Shows a frame once its pages are decoded (or after a short wait), so the old pages stay until then. */
  async function showFrame(next) {
    const indices = [];
    for (let i = next.first; i <= next.last; i++) {
      indices.push(i);
    }

    const images = indices.map((index) => {
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
    });
    const decoded = Promise.all(images.map((image) => image.decode().then(() => {
      ratios.set(Number(image.dataset.index), image.naturalWidth / image.naturalHeight);
    }, () => {})));
    await Promise.race([decoded, new Promise((resolve) => setTimeout(resolve, DECODE_WAIT_MS))]);
    if (closed || strip) {
      return;
    }

    frame = next;
    pages.replaceChildren(...images);
    layout();

    slider.value = String(next.first);
    if (next.first === next.last) {
      label(pageText, "WebUi.Web.Reader.PageOf", formatNumber(next.first + 1), formatNumber(book.count));
    } else {
      label(pageText, "WebUi.Web.Reader.PagesOf", formatNumber(next.first + 1), formatNumber(next.last + 1), formatNumber(book.count));
    }

    prefetch(next);
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

  // ---------- Strip ----------

  function enterStrip(page) {
    frame = null;
    pages.replaceChildren();
    stripCurrent = page;
    strip = createStrip(stage, {
      count: book.count,
      rtl: direction === "rtl",
      start: page,
      src: (index) => urls.page(id, index),
      onMove: onStripMove,
    });
  }

  function leaveStrip() {
    strip?.destroy();
    strip = null;
  }

  function onStripMove({ current, atEnd }) {
    stripCurrent = current;
    slider.value = String(current);
    label(pageText, "WebUi.Web.Reader.PageOf", formatNumber(current + 1), formatNumber(book.count));
    record({ progressPosition: current, progressTotal: book.count, reachedEnd: atEnd });
  }

  /** The first page on screen. */
  function currentPage() {
    return strip ? stripCurrent : frame ? frame.first : 0;
  }

  // ---------- Settings ----------

  function showSettings() {
    pages.classList.toggle("rtl", direction === "rtl");
    slider.dir = direction === "rtl" ? "rtl" : "ltr";
    spreadButton.replaceChildren(icon(flow === "strip" ? "strip" : spread === "double" ? "spread" : "single"));
    coverButton.hidden = flow === "strip";
    directionButton.replaceChildren(icon(direction === "rtl" ? "rtl" : "ltr"));
    directionButton.setAttribute("aria-pressed", String(direction === "rtl"));
    coverButton.setAttribute("aria-pressed", String(coverAlone));
  }

  function toggle(which) {
    touch();
    let toast;
    let change;
    if (which === "spread") {
      // One page, two pages, strip, as the desktop viewer's "pages shown" button.
      if (flow === "strip") {
        flow = "paged";
        spread = "single";
      } else if (spread === "single") {
        spread = "double";
      } else {
        flow = "strip";
      }

      change = { spread, flow };
      toast = flow === "strip" ? "WebUi.Web.Reader.Strip"
        : spread === "double" ? "WebUi.Web.Reader.DoublePage" : "WebUi.Web.Reader.SinglePage";
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
    frames.clear();
    if (!frame && !strip) {
      return;
    }

    const keep = currentPage();
    if (flow === "strip") {
      if (!strip) {
        enterStrip(keep);
      } else if (which === "direction") {
        strip.setDirection(direction === "rtl");
      }
    } else {
      leaveStrip();
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
    if (event.altKey || event.ctrlKey || event.metaKey || (!frame && !strip)) {
      if (event.key === "Escape") {
        leave();
      }

      return;
    }

    switch (event.key) {
      case "ArrowLeft":
        if (strip) {
          touch();
          strip.scrollBy(direction === "rtl" ? STRIP_ARROW : -STRIP_ARROW);
        } else {
          left();
        }
        break;
      case "ArrowRight":
        if (strip) {
          touch();
          strip.scrollBy(direction === "rtl" ? -STRIP_ARROW : STRIP_ARROW);
        } else {
          right();
        }
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
    if (strip) {
      touch();
      return; // the strip scrolls itself
    }

    // Every notch turns a page at once, as in the desktop viewer; a touchpad turns one per WHEEL_STEP of travel.
    event.preventDefault();
    const delta = event.deltaY * (event.deltaMode === 1 ? 33 : event.deltaMode === 2 ? stage.clientHeight : 1);
    if (!frame || delta === 0) {
      return;
    }

    if (Math.sign(delta) !== Math.sign(wheelSum)) {
      wheelSum = 0;
    }

    wheelSum += delta;
    clearTimeout(wheelTimer);
    wheelTimer = setTimeout(() => (wheelSum = 0), WHEEL_RESET_MS);
    if (Math.abs(delta) >= WHEEL_STEP / 2 || Math.abs(wheelSum) >= WHEEL_STEP) {
      wheelSum = 0;
      if (delta > 0) {
        forward();
      } else {
        backward();
      }
    }
  }

  function onPointerDown(event) {
    touch();
    drag = { x: event.clientX, y: event.clientY };
  }

  function onPointerUp(event) {
    if (!drag || (!frame && !strip)) {
      drag = null;
      return;
    }

    const dx = event.clientX - drag.x;
    const dy = event.clientY - drag.y;
    drag = null;
    if (Math.hypot(dx, dy) > TAP_MOVE) {
      if (strip) {
        return; // the strip was scrolled
      }

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
    if (document.visibilityState === "visible" && Date.now() - lastInput < IDLE_LIMIT_MS && (frame || strip)) {
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
      leaveStrip();
      flushOnLeave();
      clearInterval(ticker);
      clearTimeout(chromeTimer);
      clearTimeout(wheelTimer);
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
