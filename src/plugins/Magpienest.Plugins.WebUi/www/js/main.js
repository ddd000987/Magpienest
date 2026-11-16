// Starts the web interface: texts, theme, routes and live events from the app.

import { api, listen } from "./api.js";
import { t, setStrings } from "./i18n.js";
import { drawIcons } from "./icons.js";
import { start } from "./router.js";
import * as browse from "./browse.js";
import { openViewer } from "./viewer.js";
import { openReader } from "./reader.js";
import { openPlayer } from "./player.js";

const OPENERS = { view: openViewer, read: openReader, play: openPlayer };
const overlayRoot = document.getElementById("overlay");
let overlay = null;
let kinds = [];
let toastTimer = 0;

function applyTheme(theme) {
  document.documentElement.dataset.theme = theme === "light" || theme === "dark" ? theme : "system";
}

function toast(text) {
  const element = document.getElementById("toast");
  element.textContent = text;
  element.classList.add("show");
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => element.classList.remove("show"), 1800);
}

async function loadAll() {
  const [state, texts] = await Promise.all([api.state(), api.strings()]);
  // The desktop's order: illustrations, comics, videos, games, then kinds from other plugins.
  const order = ["illustration", "comic", "video", "game"];
  const rank = (id) => (order.includes(id) ? order.indexOf(id) : order.length);
  kinds = [...state.kinds].sort((a, b) => rank(a.id) - rank(b.id));
  applyTheme(state.theme);
  setStrings(texts, state.language);
  browse.setKinds(kinds);
  document.getElementById("logout").hidden = !state.canLogout;
}

function closeOverlay() {
  if (overlay) {
    overlay.close();
    overlay = null;
    document.body.classList.remove("overlay-open");
    browse.setSuspended(false);
  }
}

async function onRoute(route) {
  if (route.name === "kind") {
    closeOverlay();
    browse.hideDetails();
    if (!kinds.some((k) => k.id === route.kind) && kinds.length > 0) {
      location.replace(`#/kind/${kinds[0].id}`);
      return;
    }

    browse.show(route.kind);
    return;
  }

  if (route.name === "item") {
    closeOverlay();
    await browse.showDetails(route.id);
    return;
  }

  closeOverlay();
  const kind = { view: "illustration", read: "comic", play: "video" }[route.name];
  if (browse.currentKind() !== kind) {
    browse.hideDetails();
    browse.show(kind); // The grid underneath gives the viewer its neighbours and is there when it closes.
  }

  browse.setSuspended(true);
  document.body.classList.add("overlay-open");
  overlay = OPENERS[route.name](overlayRoot, route.id, { adjacent: browse.adjacent, cached: browse.cached, toast });
}

async function boot() {
  drawIcons();
  try {
    await loadAll();
  } catch {
    document.getElementById("loading").textContent = t("WebUi.Web.Error.Load");
    return;
  }

  document.getElementById("loading").hidden = true;
  browse.init({ toast });
  document.getElementById("logout").addEventListener("click", async () => {
    try {
      await api.logout();
    } finally {
      location.replace("/login");
    }
  });

  start(onRoute, () => `#/kind/${kinds[0]?.id ?? "illustration"}`);

  listen({
    language: () => loadAll().catch(() => {}),
    theme: (data) => applyTheme(data.theme),
    library: (data) => browse.libraryChanged(data.kind),
  });
}

boot();
