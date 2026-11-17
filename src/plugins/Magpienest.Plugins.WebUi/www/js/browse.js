// The library grid: kind tabs, search, sort, filters, tiles loaded page by page, and the details panel.

import { api, urls } from "./api.js";
import { t, label, labelAttribute, textElement, onLanguage, formatDate, formatSize, formatDuration, formatClock, formatNumber }
  from "./i18n.js";
import { icon, kindIcon, iconButton } from "./icons.js";
import { isGame, gameTileLine, gameFacts } from "./games.js";
import { go, back } from "./router.js";
import { attachHover } from "./hover.js";

const PAGE_SIZE = 120;
const MAX_PAGE = 500;
const SHOWN_TAGS = 200;

/** The route that opens an item of each kind. Games have none: they can only run on this PC. */
const OPEN_ROUTES = { illustration: "view", comic: "read", video: "play" };
const OPEN_LABELS = { view: "WebUi.Web.Details.View", read: "WebUi.Web.Details.Read", play: "WebUi.Web.Details.Play" };
const OPEN_ICONS = { view: "image", read: "book", play: "play" };

const SORTS = [
  ["title", "WebUi.Web.Sort.Title"],
  ["addedAt", "WebUi.Web.Sort.AddedAt"],
  ["lastOpenedAt", "WebUi.Web.Sort.LastOpenedAt"],
  ["rating", "WebUi.Web.Sort.Rating"],
  ["fileModified", "WebUi.Web.Sort.FileModified"],
  ["fileSize", "WebUi.Web.Sort.FileSize"],
  ["timeSpent", "WebUi.Web.Sort.TimeSpent"],
  ["progress", "WebUi.Web.Sort.Progress"],
  ["path", "WebUi.Web.Sort.Path"],
];

/** Every status with its label key, in the desktop's order. */
export const STATUSES = [
  ["none", "WebUi.Web.Status.None"],
  ["inProgress", "WebUi.Web.Status.InProgress"],
  ["completed", "WebUi.Web.Status.Completed"],
  ["onHold", "WebUi.Web.Status.OnHold"],
  ["dropped", "WebUi.Web.Status.Dropped"],
  ["planned", "WebUi.Web.Status.Planned"],
];

const $ = (id) => document.getElementById(id);

let kinds = [];
let kind = null;
const filters = new Map();
let items = [];
let total = 0;
let generation = 0;
let loading = false;
let currentLoad = Promise.resolve();
let suspended = false;
let refreshTimer = 0;
const dirty = new Set();
let detailsItem = null;
let choices = { kind: null, tags: [], dirs: [] };
let notify = () => {};

/** The route that opens an item, or null when the web page cannot open it. */
export function openRoute(item) {
  const route = OPEN_ROUTES[item.kind];
  return route ? `#/${route}/${item.id}` : null;
}

/** The kind shown in the grid. */
export function currentKind() {
  return kind;
}

/** An item of the loaded grid, or undefined. */
export function cached(id) {
  return items.find((item) => item.id === id);
}

/** Sets everything up once; `options.toast` shows a short message. */
export function init(options) {
  notify = options.toast;
  attachHover($("grid"), cached);
  fillSelect($("sort"), SORTS);
  fillSelect($("filter-rating"), [[0, "WebUi.Web.Filter.Any"], ...[1, 2, 3, 4, 5].map((n) => [n, "WebUi.Web.Filter.RatingAtLeast", n])]);
  fillSelect($("filter-status"), [["", "WebUi.Web.Filter.Any"], ...STATUSES]);

  $("search").addEventListener("input", debounce(() => update({ text: $("search").value.trim() }), 300));
  $("sort").addEventListener("change", () => update({ sort: $("sort").value }, true));
  $("order").addEventListener("click", () => update({ desc: !values().desc }, true));
  $("filter-button").addEventListener("click", () => toggleFilters());
  $("filter-close").addEventListener("click", () => toggleFilters(false));
  $("filter-favorites").addEventListener("change", () => update({ favorites: $("filter-favorites").checked }));
  $("filter-rating").addEventListener("change", () => update({ minRating: Number($("filter-rating").value) }));
  $("filter-status").addEventListener("change", () => update({ status: $("filter-status").value }));
  $("filter-folder").addEventListener("change", () => update({ dir: $("filter-folder").value }));
  $("filter-tag-search").addEventListener("input", renderTagChoices);
  $("filter-clear").addEventListener("click", () => {
    const { sort, desc } = values();
    filters.set(kind, { ...defaults(), sort, desc });
    showValues();
    reload();
  });

  const observer = new IntersectionObserver((entries) => {
    if (entries.some((entry) => entry.isIntersecting)) {
      loadMore();
    }
  }, { rootMargin: "800px 0px" });
  observer.observe($("more"));

  document.addEventListener("keydown", (event) => {
    if (event.key === "Escape" && detailsItem && $("overlay").hidden) {
      closeDetails();
    }
  });

  onLanguage(() => {
    renderTabs();
    showValues();
    renderFolderChoices();
    updateSummary();
    if (detailsItem) {
      renderDetails();
    }
  });
}

/** The registered kinds (names already in the app's language). */
export function setKinds(list) {
  kinds = list;
  renderTabs();
}

/** Shows the grid of a kind. */
export function show(next) {
  $("topbar").hidden = false;
  $("browse").hidden = false;
  if (next === kind && !dirty.has(kind)) {
    renderTabs();
    return currentLoad;
  }

  const changedKind = next !== kind;
  kind = next;
  dirty.delete(kind);
  $("grid").className = `grid kind-${kind}`;
  showValues();
  renderTabs();
  if (changedKind) {
    choices = { kind: null, tags: [], dirs: [] };
    if (!$("filters").hidden) {
      loadChoices();
    }
  }

  return reload(!changedKind);
}

/** While a viewer covers the grid, library changes only mark it for a refresh when the viewer closes. */
export function setSuspended(value) {
  suspended = value;
  if (!value && kind && dirty.has(kind)) {
    dirty.delete(kind);
    reload(true);
    if (detailsItem) {
      refreshDetails();
    }
  }
}

/** The library changed (a scan, or a change from this or another device). */
export function libraryChanged(changed) {
  if (changed && changed !== kind) {
    dirty.add(changed);
    return;
  }

  if (suspended) {
    dirty.add(kind);
    return;
  }

  clearTimeout(refreshTimer);
  refreshTimer = setTimeout(() => {
    choices = { kind: null, tags: [], dirs: [] };
    if (!$("filters").hidden) {
      loadChoices();
    }
    reload(true);
  }, 1500);
}

/** The item `step` places away from `id` in the grid's order (loading more when needed), or null. */
export async function adjacent(id, step) {
  await currentLoad;
  const index = items.findIndex((item) => item.id === id);
  if (index < 0) {
    return null;
  }

  const target = index + step;
  if (target >= items.length && items.length < total) {
    await loadMore(true);
  }

  return target >= 0 ? items[target] ?? null : null;
}

// ---------- Query ----------

function defaults() {
  const saved = readSort(kind);
  return { text: "", tags: [], dir: "", favorites: false, minRating: 0, status: "", sort: saved.sort, desc: saved.desc };
}

function values() {
  if (!filters.has(kind)) {
    filters.set(kind, defaults());
  }

  return filters.get(kind);
}

function update(change, sortChanged = false) {
  Object.assign(values(), change);
  if (sortChanged) {
    writeSort(kind, values());
  }

  showValues();
  reload();
}

function queryValues() {
  const v = values();
  return {
    kind,
    text: v.text,
    tags: v.tags.join(","),
    dir: v.dir,
    favorites: v.favorites,
    minRating: v.minRating || "",
    status: v.status,
    sort: v.sort,
    desc: v.desc,
  };
}

function reload(keep = false) {
  const run = generation + 1;
  generation = run;
  const wanted = keep ? Math.min(Math.max(items.length, PAGE_SIZE), MAX_PAGE) : PAGE_SIZE;
  if (!keep) {
    items = [];
    total = 0;
    $("grid").replaceChildren();
    window.scrollTo(0, 0);
  }

  loading = true;
  updateSummary();
  currentLoad = (async () => {
    try {
      const page = await api.items({ ...queryValues(), offset: 0, limit: wanted });
      if (run !== generation) {
        return;
      }

      items = page.items;
      total = page.total;
      $("grid").replaceChildren(...items.map(tile));
      markCurrent();
    } catch {
      if (run === generation) {
        notify(t("WebUi.Web.Error.Load"));
      }
    } finally {
      if (run === generation) {
        loading = false;
        updateSummary();
        requestAnimationFrame(checkSentinel);
      }
    }
  })();
  return currentLoad;
}

async function loadMore(force = false) {
  if (loading || !kind || items.length >= total || (suspended && !force)) {
    return;
  }

  const run = generation;
  loading = true;
  try {
    const page = await api.items({ ...queryValues(), offset: items.length, limit: PAGE_SIZE });
    if (run !== generation) {
      return;
    }

    const known = new Set(items.map((item) => item.id));
    const fresh = page.items.filter((item) => !known.has(item.id));
    items.push(...fresh);
    total = page.total;
    $("grid").append(...fresh.map(tile));
  } catch {
    if (run === generation) {
      notify(t("WebUi.Web.Error.Load"));
    }
  } finally {
    if (run === generation) {
      loading = false;
      updateSummary();
      requestAnimationFrame(checkSentinel);
    }
  }
}

function checkSentinel() {
  if (!$("browse").hidden && $("more").getBoundingClientRect().top < innerHeight + 800) {
    loadMore();
  }
}

function updateSummary() {
  if (loading && items.length === 0) {
    label($("summary"), "WebUi.Web.Loading");
  } else {
    label($("summary"), "WebUi.Web.Count", formatNumber(total));
  }

  $("empty").hidden = loading || total > 0;
  const count = activeFilterCount();
  $("filter-count").hidden = count === 0;
  $("filter-count").textContent = formatNumber(count);
}

function activeFilterCount() {
  if (!kind) {
    return 0;
  }

  const v = values();
  return v.tags.length + (v.dir ? 1 : 0) + (v.favorites ? 1 : 0) + (v.minRating ? 1 : 0) + (v.status ? 1 : 0);
}

// ---------- Top bar and filters ----------

function renderTabs() {
  $("tabs").replaceChildren(...kinds.map((k) => {
    const tab = document.createElement("a");
    tab.className = "tab";
    tab.href = `#/kind/${k.id}`;
    tab.setAttribute("role", "tab");
    tab.setAttribute("aria-selected", String(k.id === kind));
    tab.textContent = k.name;
    return tab;
  }));
}

function showValues() {
  if (!kind) {
    return;
  }

  const v = values();
  if ($("search").value.trim() !== v.text) {
    $("search").value = v.text;
  }

  $("sort").value = v.sort;
  const order = $("order");
  order.replaceChildren(icon(v.desc ? "sortDown" : "sortUp"));
  labelAttribute(order, "title", v.desc ? "WebUi.Web.Sort.Descending" : "WebUi.Web.Sort.Ascending");
  labelAttribute(order, "aria-label", v.desc ? "WebUi.Web.Sort.Descending" : "WebUi.Web.Sort.Ascending");
  $("filter-favorites").checked = v.favorites;
  $("filter-rating").value = String(v.minRating);
  $("filter-status").value = v.status;
  renderFolderChoices();
  renderTagChoices();
}

function toggleFilters(open = $("filters").hidden) {
  $("filters").hidden = !open;
  $("filter-button").setAttribute("aria-expanded", String(open));
  if (open) {
    loadChoices();
  }
}

async function loadChoices() {
  if (choices.kind === kind) {
    return;
  }

  const forKind = kind;
  try {
    const [tags, dirs] = await Promise.all([api.tags(forKind), api.dirs(forKind)]);
    if (forKind !== kind) {
      return;
    }

    choices = { kind: forKind, tags: [...tags].sort((a, b) => b.count - a.count || a.name.localeCompare(b.name)), dirs };
    renderFolderChoices();
    renderTagChoices();
  } catch {
    notify(t("WebUi.Web.Error.Load"));
  }
}

function renderFolderChoices() {
  if (!kind) {
    return;
  }

  const select = $("filter-folder");
  const current = values().dir;
  const all = textElement("option", "WebUi.Web.Filter.AllFolders");
  all.value = "";
  const dirs = choices.dirs.includes(current) || !current ? choices.dirs : [current, ...choices.dirs];
  select.replaceChildren(all, ...dirs.map((dir) => {
    const option = document.createElement("option");
    option.value = dir;
    option.textContent = shortFolder(dir);
    option.title = dir;
    return option;
  }));
  select.value = current;
}

function renderTagChoices() {
  if (!kind) {
    return;
  }

  const selected = new Set(values().tags);
  const find = $("filter-tag-search").value.trim().toLowerCase();
  const known = new Map(choices.tags.map((tag) => [tag.name, tag.count]));
  for (const name of selected) {
    if (!known.has(name)) {
      known.set(name, null);
    }
  }

  const shown = [...known.entries()]
    .filter(([name]) => selected.has(name) || !find || name.toLowerCase().includes(find))
    .sort((a, b) => Number(selected.has(b[0])) - Number(selected.has(a[0])))
    .slice(0, SHOWN_TAGS + selected.size);
  $("filter-tags").replaceChildren(...shown.map(([name, count]) => {
    const chip = tagChip(name, count);
    chip.setAttribute("aria-pressed", String(selected.has(name)));
    chip.addEventListener("click", () => {
      const tags = selected.has(name) ? values().tags.filter((tag) => tag !== name) : [...values().tags, name];
      update({ tags });
    });
    return chip;
  }));
  $("filter-no-tags").hidden = shown.length > 0 || choices.kind !== kind;
}

function tagChip(name, count) {
  const chip = document.createElement("button");
  chip.type = "button";
  chip.className = "chip";
  const text = document.createElement("span");
  text.className = "label";
  text.textContent = name;
  chip.append(text);
  if (count !== null && count !== undefined) {
    const number = document.createElement("span");
    number.className = "count";
    number.textContent = formatNumber(count);
    chip.append(number);
  }

  return chip;
}

function fillSelect(select, options) {
  select.replaceChildren(...options.map(([value, key, ...args]) => {
    const option = textElement("option", key, ...args);
    option.value = String(value);
    return option;
  }));
}

function shortFolder(dir) {
  const parts = dir.split(/[\\/]+/).filter(Boolean);
  return parts.length <= 2 ? dir : parts.slice(-2).join("\\");
}

// ---------- Tiles ----------

function tile(item) {
  const link = document.createElement("a");
  link.className = item.missing ? "tile missing" : "tile";
  link.href = `#/item/${item.id}`;
  link.dataset.id = String(item.id);
  link.title = item.title;

  const thumb = document.createElement("div");
  thumb.className = "thumb";
  const image = document.createElement("img");
  image.loading = "lazy";
  image.decoding = "async";
  image.alt = "";
  image.src = urls.thumb(item.id);
  image.addEventListener("error", () => image.replaceWith(icon(kindIcon(item.kind))), { once: true });
  thumb.append(image);
  if (item.favorite) {
    const favorite = icon("heart", true);
    favorite.classList.add("tile-favorite");
    thumb.append(favorite);
  }

  const share = progressShare(item);
  if (share > 0) {
    const bar = document.createElement("div");
    bar.className = "tile-progress";
    const fill = document.createElement("span");
    fill.style.width = `${Math.round(share * 100)}%`;
    bar.append(fill);
    thumb.append(bar);
  }

  const title = document.createElement("div");
  title.className = "tile-title";
  title.textContent = item.title;
  link.append(thumb, title);

  const line = isGame(item.kind) ? gameTileLine(item) : "";
  if (line) {
    const sub = document.createElement("div");
    sub.className = "tile-sub";
    sub.textContent = line;
    link.append(sub);
  }

  link.addEventListener("dblclick", (event) => {
    const route = openRoute(item);
    if (route) {
      event.preventDefault();
      go(route);
    }
  });
  return link;
}

function progressShare(item) {
  if (item.status === "completed") {
    return item.progressTotal > 0 ? 1 : 0;
  }

  if (item.progressTotal <= 0 || item.status === "none") {
    return 0;
  }

  const position = item.kind === "comic" ? item.progressPosition + 1 : item.progressPosition;
  return Math.min(1, Math.max(0, position / item.progressTotal));
}

function replaceTile(item) {
  const index = items.findIndex((i) => i.id === item.id);
  if (index < 0) {
    return;
  }

  items[index] = { ...items[index], ...item };
  const old = $("grid").querySelector(`[data-id="${item.id}"]`);
  old?.replaceWith(tile(items[index]));
  markCurrent();
}

function markCurrent() {
  for (const element of $("grid").querySelectorAll("[aria-current]")) {
    element.removeAttribute("aria-current");
  }

  if (detailsItem) {
    $("grid").querySelector(`[data-id="${detailsItem.id}"]`)?.setAttribute("aria-current", "true");
  }
}

// ---------- Details panel ----------

/** Opens the details of an item (the grid of its kind is shown underneath). */
export async function showDetails(id) {
  let item;
  try {
    item = await api.item(id);
  } catch {
    notify(t("WebUi.Web.Error.NotFound"));
    go(`#/kind/${kind ?? kinds[0]?.id ?? ""}`, true);
    return;
  }

  await show(item.kind);
  detailsItem = item;
  renderDetails();
  $("details").hidden = false;
  document.body.classList.add("details-open");
  markCurrent();
  $("grid").querySelector(`[data-id="${id}"]`)?.scrollIntoView({ block: "nearest" });
}

/** Hides the details panel. */
export function hideDetails() {
  detailsItem = null;
  $("details").hidden = true;
  $("details").replaceChildren();
  document.body.classList.remove("details-open");
  markCurrent();
}

function closeDetails() {
  if (detailsItem) {
    back(`#/kind/${detailsItem.kind}`);
  }
}

async function refreshDetails() {
  const id = detailsItem?.id;
  try {
    const item = await api.item(id);
    if (detailsItem?.id === id) {
      detailsItem = item;
      renderDetails();
    }
  } catch {
    // The item is gone; the panel keeps what it showed.
  }
}

function renderDetails() {
  const item = detailsItem;
  const panel = $("details");
  const route = openRoute(item);

  const head = document.createElement("div");
  head.className = "details-head";
  const title = document.createElement("h2");
  title.textContent = item.title;
  const close = iconButton("close", "WebUi.Web.Close", t, "icon-button flat");
  close.addEventListener("click", closeDetails);
  head.append(title, close);

  const cover = document.createElement("div");
  cover.className = route ? "details-cover" : "details-cover no-open";
  const image = document.createElement("img");
  image.alt = "";
  image.src = urls.thumb(item.id);
  image.addEventListener("error", () => image.replaceWith(icon(kindIcon(item.kind))), { once: true });
  cover.append(image);
  if (route) {
    cover.addEventListener("click", () => go(route));
  }

  const actions = document.createElement("div");
  actions.className = "details-actions";
  if (route && !item.missing) {
    const open = document.createElement("button");
    open.type = "button";
    open.className = "button accent";
    const kindOfRoute = route.split("/")[1];
    open.append(icon(OPEN_ICONS[kindOfRoute]), textElement("span", OPEN_LABELS[kindOfRoute]));
    open.addEventListener("click", () => go(route));
    actions.append(open);
  }

  const favorite = document.createElement("button");
  favorite.type = "button";
  favorite.className = "button favorite-button";
  favorite.setAttribute("aria-pressed", String(item.favorite));
  favorite.append(icon("heart"), textElement("span", "WebUi.Web.Details.Favorite"));
  favorite.addEventListener("click", () => change({ favorite: !item.favorite }));
  actions.append(favorite);

  const parts = [head, cover, actions, ratingRow(item), statusRow(item)];
  if (item.missing) {
    parts.push(textElement("p", "WebUi.Web.Details.Missing"));
    parts.at(-1).className = "warning";
  }

  parts.push(factList(item));
  if (item.tags?.length) {
    const section = document.createElement("section");
    const heading = textElement("h3", "WebUi.Web.Details.Tags");
    heading.className = "section-title";
    const chips = document.createElement("div");
    chips.className = "chips";
    chips.append(...item.tags.map((tag) => {
      const chip = tagChip(tag);
      chip.addEventListener("click", () => {
        filters.set(item.kind, { ...(filters.get(item.kind) ?? values()), tags: [tag] });
        go(`#/kind/${item.kind}`);
        showValues();
        reload();
      });
      return chip;
    }));
    section.append(heading, chips);
    parts.push(section);
  }

  if (item.notes) {
    const section = document.createElement("section");
    const heading = textElement("h3", "WebUi.Web.Details.Notes");
    heading.className = "section-title";
    const notes = document.createElement("p");
    notes.className = "notes";
    notes.textContent = item.notes;
    section.append(heading, notes);
    parts.push(section);
  }

  panel.replaceChildren(...parts);
}

function ratingRow(item) {
  const section = document.createElement("section");
  const heading = textElement("h3", "WebUi.Web.Details.Rating");
  heading.className = "section-title";
  const stars = document.createElement("div");
  stars.className = "rating";
  for (let n = 1; n <= 5; n++) {
    const star = document.createElement("button");
    star.type = "button";
    star.className = n <= item.rating ? "on" : "";
    star.append(icon("star"));
    labelAttribute(star, "aria-label", "WebUi.Web.Details.Stars", n);
    labelAttribute(star, "title", "WebUi.Web.Details.Stars", n);
    star.setAttribute("aria-pressed", String(n === item.rating));
    star.addEventListener("click", () => change({ rating: item.rating === n ? 0 : n }));
    stars.append(star);
  }

  section.append(heading, stars);
  return section;
}

function statusRow(item) {
  const row = document.createElement("label");
  row.className = "status-row";
  const heading = textElement("span", "WebUi.Web.Details.Status");
  heading.className = "section-title";
  const select = document.createElement("select");
  select.className = "field";
  fillSelect(select, STATUSES);
  select.value = item.status;
  select.addEventListener("change", () => change({ status: select.value }));
  row.append(heading, select);
  return row;
}

function factList(item) {
  let facts;
  if (isGame(item.kind)) {
    facts = gameFacts(item);
  } else {
    facts = [];
    if (item.kind === "comic" && item.progressTotal > 0) {
      facts.push(["WebUi.Web.Details.Progress", t("WebUi.Web.Details.PageOf", formatNumber(Math.min(item.progressPosition + 1, item.progressTotal)),
                                                   formatNumber(item.progressTotal))]);
    }

    if (item.kind === "video" && item.progressTotal > 0) {
      facts.push(["WebUi.Web.Details.Progress", t("WebUi.Web.Details.TimeOf", formatClock(item.progressPosition / 1000),
                                                   formatClock(item.progressTotal / 1000))]);
    }

    if (item.kind === "comic" || item.kind === "video") {
      facts.push(["WebUi.Web.Details.TimeSpent", formatDuration(item.timeSpentSeconds)]);
    }

    facts.push(
      ["WebUi.Web.Details.LastOpened", formatDate(item.lastOpenedAt)],
      ["WebUi.Web.Details.Added", formatDate(item.addedAt)],
      ["WebUi.Web.Details.FileSize", formatSize(item.fileSize)],
      ["WebUi.Web.Details.FileDate", formatDate(item.fileModified)],
      ["WebUi.Web.Details.Folder", item.directory],
    );
  }

  const list = document.createElement("dl");
  list.className = "facts";
  for (const [key, value] of facts) {
    const name = textElement("dt", key);
    const text = document.createElement("dd");
    text.textContent = value;
    list.append(name, text);
  }

  return list;
}

async function change(body) {
  const id = detailsItem.id;
  try {
    const updated = await api.patch(id, body);
    replaceTile(updated);
    if (detailsItem?.id === id) {
      detailsItem = { ...detailsItem, ...updated };
      renderDetails();
    }
  } catch {
    notify(t("WebUi.Web.Error.Save"));
  }
}

// ---------- Helpers ----------

function debounce(action, delay) {
  let timer = 0;
  return () => {
    clearTimeout(timer);
    timer = setTimeout(action, delay);
  };
}

function readSort(forKind) {
  try {
    const saved = JSON.parse(localStorage.getItem(`magpienest.sort.${forKind}`) ?? "null");
    if (saved && SORTS.some(([value]) => value === saved.sort)) {
      return { sort: saved.sort, desc: Boolean(saved.desc) };
    }
  } catch {
    // No storage (private window): the default order.
  }

  return { sort: "title", desc: false };
}

function writeSort(forKind, v) {
  try {
    localStorage.setItem(`magpienest.sort.${forKind}`, JSON.stringify({ sort: v.sort, desc: v.desc }));
  } catch {
    // No storage: the order lasts until the page closes.
  }
}
