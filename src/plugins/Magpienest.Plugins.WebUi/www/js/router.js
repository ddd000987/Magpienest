// Hash routes: #/kind/<kind>, #/item/<id>, #/view/<id>, #/read/<id>, #/play/<id>.

const PATTERNS = [
  [/^#\/kind\/([A-Za-z0-9_.-]+)$/, (m) => ({ name: "kind", kind: m[1] })],
  [/^#\/item\/(\d+)$/, (m) => ({ name: "item", id: Number(m[1]) })],
  [/^#\/view\/(\d+)$/, (m) => ({ name: "view", id: Number(m[1]) })],
  [/^#\/read\/(\d+)$/, (m) => ({ name: "read", id: Number(m[1]) })],
  [/^#\/play\/(\d+)$/, (m) => ({ name: "play", id: Number(m[1]) })],
];

let handler = () => {};
let previous = null;
let current = null;

/** The route of a hash, or null. */
export function parse(hash) {
  for (const [pattern, make] of PATTERNS) {
    const match = pattern.exec(hash);
    if (match) {
      return make(match);
    }
  }

  return null;
}

/** Goes to a hash; `replace` keeps the browser history from growing. */
export function go(hash, replace = false) {
  if (location.hash === hash) {
    return;
  }

  if (replace) {
    location.replace(hash);
  } else {
    location.hash = hash;
  }
}

/** Changes the address without showing a new route (paging inside a viewer). */
export function setQuietly(hash) {
  history.replaceState(null, "", hash);
  current = parse(hash);
}

/** Back to where the user came from inside this page, or to `fallback` when the page was opened at this route. */
export function back(fallback) {
  if (previous) {
    history.back();
  } else {
    go(fallback, true);
  }
}

/** Starts calling `onRoute(route)` for the current and every later hash; `fallback()` gives the start hash. */
export function start(onRoute, fallback) {
  handler = onRoute;
  window.addEventListener("hashchange", () => dispatch(false));
  if (!parse(location.hash)) {
    history.replaceState(null, "", fallback());
  }

  dispatch(true);
}

function dispatch(first) {
  const route = parse(location.hash);
  if (!route) {
    return;
  }

  if (!first) {
    previous = current;
  }

  current = route;
  handler(route);
}
