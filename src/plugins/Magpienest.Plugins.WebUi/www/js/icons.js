// Line icons drawn inline (the server never sends .svg files). Each is a 24 x 24 path in the current text colour.

const PATHS = {
  back: "M15 18l-6-6 6-6",
  close: "M6 6l12 12M18 6L6 18",
  search: "M11 4a7 7 0 1 0 0 14a7 7 0 1 0 0-14zM20 20l-4-4",
  filter: "M4 5h16l-6 8v5l-4 2v-7z",
  sortUp: "M8 20V4M4 8l4-4 4 4M14 8h6M14 13h4M14 18h2",
  sortDown: "M8 4v16M4 16l4 4 4-4M14 6h2M14 11h4M14 16h6",
  heart: "M12 20s-7.5-4.6-7.5-10.2A4.3 4.3 0 0 1 12 7.2a4.3 4.3 0 0 1 7.5 2.6C19.5 15.4 12 20 12 20z",
  star: "M12 3.5l2.6 5.4 5.9.8-4.3 4.1 1 5.9L12 16.9l-5.2 2.8 1-5.9-4.3-4.1 5.9-.8z",
  logout: "M14 4h5v16h-5M10 8l-4 4 4 4M6 12h10",
  left: "M15 6l-6 6 6 6",
  right: "M9 6l6 6-6 6",
  spread: "M3 5h8v14H3zM13 5h8v14h-8z",
  single: "M7 4h10v16H7z",
  rtl: "M20 12H4M9 7l-5 5 5 5",
  ltr: "M4 12h16M15 7l5 5-5 5",
  cover: "M6 3h12v18H6zM9 8h6M9 11h4",
  play: "M8 5v14l11-7z",
  image: "M4 5h16v14H4zM4 16l5-5 4 4 3-3 4 4M15 9h.01",
  book: "M5 4h11a3 3 0 0 1 3 3v13H8a3 3 0 0 1-3-3zM5 17a3 3 0 0 1 3-3h11",
  video: "M4 6h12v12H4zM16 10l4-2.5v9L16 14",
  game: "M7 9h10a4 4 0 0 1 4 4v1a3 3 0 0 1-5.4 1.8L14.5 14h-5l-1.1 1.8A3 3 0 0 1 3 14v-1a4 4 0 0 1 4-4zM8 11v3M6.5 12.5h3M15.5 12h.01M17.5 13.5h.01",
  file: "M6 3h8l4 4v14H6zM14 3v4h4",
};

/** A span with the named icon; `filled` paints the shape (favourite heart, rating star). */
export function icon(name, filled = false) {
  const span = document.createElement("span");
  span.className = filled ? "icon filled" : "icon";
  span.setAttribute("aria-hidden", "true");
  setIcon(span, name);
  return span;
}

/** Replaces the drawing of an icon span. */
export function setIcon(span, name) {
  const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
  svg.setAttribute("viewBox", "0 0 24 24");
  const path = document.createElementNS("http://www.w3.org/2000/svg", "path");
  path.setAttribute("d", PATHS[name] ?? PATHS.file);
  svg.append(path);
  span.replaceChildren(svg);
}

/** Fills every `<span data-icon="name">` under `root`. */
export function drawIcons(root = document) {
  for (const span of root.querySelectorAll("[data-icon]")) {
    span.classList.add("icon");
    span.setAttribute("aria-hidden", "true");
    setIcon(span, span.dataset.icon);
  }
}

/** The icon of a library kind. */
export function kindIcon(kind) {
  return { illustration: "image", comic: "book", video: "video", game: "game" }[kind] ?? "file";
}

/** An icon-only button with a keyed tooltip and accessible name. */
export function iconButton(name, labelKey, t, className = "icon-button") {
  const button = document.createElement("button");
  button.type = "button";
  button.className = className;
  button.append(icon(name));
  button.setAttribute("data-i18n-title", labelKey);
  button.setAttribute("data-i18n-aria-label", labelKey);
  button.title = t(labelKey);
  button.setAttribute("aria-label", t(labelKey));
  return button;
}
