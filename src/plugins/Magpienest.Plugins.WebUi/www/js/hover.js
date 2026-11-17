// Hover preview on the grid, as in the desktop app: when the mouse rests on an illustration or a comic, the
// picture shows large beside the cursor (never under it); the wheel turns a comic's pages; moving away, clicking,
// Esc or scrolling closes it. Mouse only: touch screens have no hover.

import { api, urls } from "./api.js";
import { label, formatNumber } from "./i18n.js";

const DELAY_MS = 400;
const MARGIN = 8;
const GAP = 16;
const MIN_SIDE = 48;
const KINDS = new Set(["illustration", "comic"]);

/** Turns hover preview on for the tiles in `grid`; `lookup(id)` returns a tile's item. */
export function attachHover(grid, lookup) {
  if (!window.matchMedia("(hover: hover) and (pointer: fine)").matches) {
    return;
  }

  let tile = null;          // the tile under the mouse
  let item = null;
  let timer = 0;
  let token = 0;            // bumps on every close, so late answers are dropped
  let pages = null;         // { count, url(index) } once known
  let index = 0;
  let shown = false;
  let anchor = { x: 0, y: 0 };
  let mouse = { x: 0, y: 0 };
  let savedTitle = null;

  const box = document.createElement("div");
  box.className = "hover-preview";
  box.hidden = true;
  const picture = document.createElement("img");
  picture.alt = "";
  picture.draggable = false;
  const badge = document.createElement("span");
  badge.className = "hover-badge";
  box.append(picture, badge);
  document.body.append(box);

  grid.addEventListener("pointerover", (event) => {
    if (event.pointerType !== "mouse") {
      return;
    }

    const next = event.target.closest(".tile");
    if (next === tile) {
      return;
    }

    close();
    const found = next && lookup(Number(next.dataset.id));
    if (!found || !KINDS.has(found.kind) || found.missing) {
      return;
    }

    tile = next;
    item = found;
    timer = setTimeout(open, DELAY_MS);
  });

  grid.addEventListener("pointerout", (event) => {
    if (tile && !tile.contains(event.relatedTarget)) {
      close();
    }
  });

  document.addEventListener("pointermove", (event) => {
    mouse = { x: event.clientX, y: event.clientY };
  }, { passive: true });
  document.addEventListener("pointerdown", () => close(), true);
  document.addEventListener("keydown", (event) => {
    if (event.key === "Escape" && tile) {
      close();
    }
  });
  window.addEventListener("scroll", () => close(), true);
  window.addEventListener("hashchange", () => close());
  window.addEventListener("blur", () => close());
  // While a comic is open the wheel turns its pages instead of scrolling the grid.
  window.addEventListener("wheel", onWheel, { passive: false, capture: true });

  async function open() {
    timer = 0;
    const mine = token;
    anchor = { ...mouse };
    if (item.kind === "illustration") {
      const id = item.id;
      pages = { count: 1, url: () => urls.file(id) };
    } else {
      pages = { count: -1, url: () => "" };   // opening: the wheel is already held back
      try {
        const book = await api.book(item.id);
        if (mine !== token) {
          return;
        }

        const id = item.id;
        pages = { count: book.count, url: (i) => urls.page(id, i) };
      } catch {
        if (mine === token) {
          close();
        }

        return;
      }
    }

    if (pages.count <= 0) {
      close();
      return;
    }

    index = 0;
    show(index);
  }

  /** Loads a page and shows it once it has arrived, so the box never shows a half-loaded picture. */
  function show(page) {
    const mine = token;
    const next = new Image();
    next.decoding = "async";
    next.onload = () => {
      if (mine !== token || page !== index) {
        return;
      }

      place(next.naturalWidth, next.naturalHeight);
      picture.src = next.src;
      label(badge, "WebUi.Web.Reader.PageOf", formatNumber(page + 1), formatNumber(pages.count));
      badge.hidden = pages.count < 2;
    };
    next.src = pages.url(page);
  }

  function place(naturalWidth, naturalHeight) {
    const ratio = window.devicePixelRatio || 1;
    const rect = fit(anchor, window.innerWidth, window.innerHeight, naturalWidth / ratio, naturalHeight / ratio);
    if (!rect) {
      box.hidden = true;
      shown = false;
      return;
    }

    picture.style.width = `${rect.width}px`;
    picture.style.height = `${rect.height}px`;
    box.style.left = `${rect.x}px`;
    box.style.top = `${rect.y}px`;
    box.hidden = false;
    if (!shown) {
      shown = true;
      savedTitle = tile.getAttribute("title");
      tile.removeAttribute("title");  // the browser's own tooltip would sit on the picture
    }
  }

  function onWheel(event) {
    if (!tile || timer || !pages) {
      return;   // nothing open yet: the grid scrolls as usual
    }

    if (pages.count === 1) {
      return;   // a single picture: scrolling closes the preview
    }

    event.preventDefault();
    event.stopPropagation();
    if (pages.count < 0 || event.deltaY === 0) {
      return;
    }

    const next = Math.min(Math.max(0, index + (event.deltaY > 0 ? 1 : -1)), pages.count - 1);
    if (next !== index) {
      index = next;
      show(next);
    }
  }

  function close() {
    clearTimeout(timer);
    timer = 0;
    token++;
    if (tile && savedTitle !== null) {
      tile.setAttribute("title", savedTitle);
    }

    savedTitle = null;
    tile = null;
    item = null;
    pages = null;
    shown = false;
    box.hidden = true;
    picture.removeAttribute("src");
  }
}

/**
 * Picks the side of the cursor (right, left, below, above; the first wins a tie) where the picture can be
 * largest without being enlarged, and returns the box, or null when it would be too small everywhere.
 */
export function fit(cursor, areaWidth, areaHeight, width, height) {
  if (width <= 0 || height <= 0) {
    return null;
  }

  const fullWidth = areaWidth - 2 * MARGIN;
  const fullHeight = areaHeight - 2 * MARGIN;
  const boxes = [
    ["right", areaWidth - MARGIN - (cursor.x + GAP), fullHeight],
    ["left", cursor.x - GAP - MARGIN, fullHeight],
    ["below", fullWidth, areaHeight - MARGIN - (cursor.y + GAP)],
    ["above", fullWidth, cursor.y - GAP - MARGIN],
  ];
  let side = null;
  let best = 0;
  for (const [name, boxWidth, boxHeight] of boxes) {
    if (boxWidth <= 0 || boxHeight <= 0) {
      continue;
    }

    const scale = Math.min(1, boxWidth / width, boxHeight / height);
    if (scale > best) {
      best = scale;
      side = name;
    }
  }

  const shownWidth = Math.floor(width * best);
  const shownHeight = Math.floor(height * best);
  if (!side || Math.max(shownWidth, shownHeight) < MIN_SIDE) {
    return null;
  }

  const clamp = (value, min, max) => (max < min ? min : Math.min(Math.max(value, min), max));
  const centredX = clamp(cursor.x - shownWidth / 2, MARGIN, areaWidth - MARGIN - shownWidth);
  const centredY = clamp(cursor.y - shownHeight / 2, MARGIN, areaHeight - MARGIN - shownHeight);
  const positions = {
    right: [cursor.x + GAP, centredY],
    left: [cursor.x - GAP - shownWidth, centredY],
    below: [centredX, cursor.y + GAP],
    above: [centredX, cursor.y - GAP - shownHeight],
  };
  const [x, y] = positions[side];
  return { x, y, width: shownWidth, height: shownHeight };
}
