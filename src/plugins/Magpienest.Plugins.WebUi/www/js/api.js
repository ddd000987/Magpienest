// Calls to this PC's Magpienest server. Every request is same-origin; the session or token cookie goes with it.

/** A request the server refused or could not answer. */
export class ApiError extends Error {
  constructor(status, path) {
    super(`${path}: ${status}`);
    this.status = status;
  }
}

async function call(path, options = {}) {
  const response = await fetch(path, { credentials: "same-origin", cache: "no-store", ...options });
  if (response.status === 401) {
    // Signed out, or the access link was reset: the server shows the sign-in or "open from the app" page.
    location.replace("/");
    throw new ApiError(401, path);
  }

  if (!response.ok) {
    throw new ApiError(response.status, path);
  }

  return response.json();
}

function query(values) {
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(values)) {
    if (value !== undefined && value !== null && value !== "" && value !== false) {
      params.set(key, String(value));
    }
  }

  return params.toString();
}

function send(method, path, body) {
  return call(path, { method, headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });
}

export const api = {
  state: () => call("/api/state"),
  strings: () => call("/api/strings"),
  items: (values) => call(`/api/items?${query(values)}`),
  item: (id) => call(`/api/items/${id}`),
  patch: (id, body) => send("PATCH", `/api/items/${id}`, body),
  tags: (kind) => call(`/api/tags?${query({ kind })}`),
  dirs: (kind) => call(`/api/dirs?${query({ kind })}`),
  book: (id) => call(`/api/books/${id}`),
  frame: (id, values) => call(`/api/books/${id}/frame?${query(values)}`),
  logout: () => send("POST", "/api/logout", {}),
};

export const urls = {
  thumb: (id) => `/api/thumb/${id}`,
  file: (id) => `/api/file/${id}`,
  page: (id, index) => `/api/books/${id}/pages/${index}`,
};

/** Sends progress while the page is closing; the browser delivers it even after the tab is gone. */
export function beacon(id, body) {
  const path = `/api/items/${id}/progress`;
  const data = new Blob([JSON.stringify(body)], { type: "text/plain;charset=UTF-8" });
  if (!navigator.sendBeacon?.(path, data)) {
    fetch(path, { method: "POST", credentials: "same-origin", keepalive: true, body: data }).catch(() => {});
  }
}

/** Listens to the server's live events: `handlers` maps an event name (language, theme, library) to a function. */
export function listen(handlers) {
  const source = new EventSource("/api/events");
  for (const [name, handler] of Object.entries(handlers)) {
    source.addEventListener(name, (event) => {
      let data = {};
      try {
        data = JSON.parse(event.data);
      } catch {
        // An event without data still counts.
      }
      handler(data);
    });
  }

  return source;
}
