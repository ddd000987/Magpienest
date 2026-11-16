// Texts and number/date formats. Every visible text comes from /api/strings (this plugin's language files).
// Elements carry their key in data-i18n (text), data-i18n-placeholder, data-i18n-title or data-i18n-aria-label, so a
// language change re-labels the whole page without reloading it.

let strings = {};
let language = "en";
const listeners = new Set();
const ATTRIBUTES = ["placeholder", "title", "aria-label"];

/** The text of a key with {0}, {1}… replaced; the key itself when it is unknown. */
export function t(key, ...args) {
  const text = strings[key] ?? key;
  return args.length === 0 ? text : text.replace(/\{(\d+)\}/g, (match, index) => String(args[Number(index)] ?? match));
}

/** The current language code ("en", "zh-TW"…). */
export function currentLanguage() {
  return language;
}

/** Stores new texts and language, re-labels the page and tells every listener. */
export function setStrings(texts, lang) {
  strings = texts ?? {};
  language = lang || language;
  document.documentElement.lang = language;
  applyStrings(document);
  for (const listener of listeners) {
    listener();
  }
}

/** Calls `listener` after every language change. */
export function onLanguage(listener) {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

/** Re-labels every keyed element under `root`. */
export function applyStrings(root) {
  for (const element of root.querySelectorAll("[data-i18n]")) {
    element.textContent = t(element.dataset.i18n, ...argsOf(element));
  }

  for (const attribute of ATTRIBUTES) {
    for (const element of root.querySelectorAll(`[data-i18n-${attribute}]`)) {
      element.setAttribute(attribute, t(element.getAttribute(`data-i18n-${attribute}`), ...argsOf(element)));
    }
  }
}

/** Gives an element a keyed text (kept up to date on language changes) and returns it. */
export function label(element, key, ...args) {
  element.dataset.i18n = key;
  setArgs(element, args);
  element.textContent = t(key, ...args);
  return element;
}

/** Gives an element a keyed attribute (title, aria-label, placeholder) and returns it. */
export function labelAttribute(element, attribute, key, ...args) {
  element.setAttribute(`data-i18n-${attribute}`, key);
  setArgs(element, args);
  element.setAttribute(attribute, t(key, ...args));
  return element;
}

/** A new element of `tag` with a keyed text. */
export function textElement(tag, key, ...args) {
  return label(document.createElement(tag), key, ...args);
}

function setArgs(element, args) {
  if (args.length > 0) {
    element.dataset.i18nArgs = JSON.stringify(args);
  } else {
    delete element.dataset.i18nArgs;
  }
}

function argsOf(element) {
  try {
    return element.dataset.i18nArgs ? JSON.parse(element.dataset.i18nArgs) : [];
  } catch {
    return [];
  }
}

/** A date and time in the current language, or the "never" text. */
export function formatDate(value) {
  const date = value ? new Date(value) : null;
  return !date || Number.isNaN(date.getTime())
    ? t("WebUi.Web.Never")
    : new Intl.DateTimeFormat(language, { dateStyle: "medium", timeStyle: "short" }).format(date);
}

/** A file size with its unit (kB, MB, GB) in the current language. */
export function formatSize(bytes) {
  const units = ["byte", "kilobyte", "megabyte", "gigabyte", "terabyte"];
  let value = Math.max(0, Number(bytes) || 0);
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }

  return new Intl.NumberFormat(language, {
    style: "unit",
    unit: units[unit],
    unitDisplay: "short",
    maximumFractionDigits: unit === 0 ? 0 : 1,
  }).format(value);
}

/** Time spent: "2 h 5 min", "12 min" or "less than a minute". */
export function formatDuration(seconds) {
  const minutes = Math.floor((Number(seconds) || 0) / 60);
  if (minutes < 1) {
    return t("WebUi.Web.Duration.LessThanMinute");
  }

  const hours = Math.floor(minutes / 60);
  return hours > 0
    ? t("WebUi.Web.Duration.HoursMinutes", hours, minutes % 60)
    : t("WebUi.Web.Duration.Minutes", minutes);
}

/** A playback position as 1:02:03 or 2:03. */
export function formatClock(seconds) {
  const whole = Math.max(0, Math.floor(Number(seconds) || 0));
  const h = Math.floor(whole / 3600);
  const m = Math.floor((whole % 3600) / 60);
  const s = String(whole % 60).padStart(2, "0");
  return h > 0 ? `${h}:${String(m).padStart(2, "0")}:${s}` : `${m}:${s}`;
}

/** A whole number in the current language. */
export function formatNumber(value) {
  return new Intl.NumberFormat(language).format(value);
}
