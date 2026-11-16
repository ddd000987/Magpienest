// The sign-in page for other devices (phase 7 T7.3). Texts come from /api/strings, the look from /api/appearance.

let strings = {};

function t(key, ...args) {
  const text = strings[key] ?? key;
  return text.replace(/\{(\d+)\}/g, (match, index) => String(args[Number(index)] ?? match));
}

async function getJson(path) {
  const response = await fetch(path, { credentials: "same-origin" });
  return response.ok ? response.json() : null;
}

async function loadTexts() {
  const [texts, look] = await Promise.all([getJson("/api/strings"), getJson("/api/appearance")]);
  strings = texts ?? {};
  if (look) {
    document.documentElement.lang = look.language;
    document.documentElement.dataset.theme = look.theme === "light" || look.theme === "dark" ? look.theme : "system";
  }
  for (const element of document.querySelectorAll("[data-i18n]")) {
    element.textContent = t(element.dataset.i18n);
  }
}

function show(text) {
  document.getElementById("message").textContent = text;
}

async function signIn(event) {
  event.preventDefault();
  const button = document.getElementById("submit");
  const input = document.getElementById("password");
  button.disabled = true;
  show("");
  try {
    const response = await fetch("/api/login", {
      method: "POST",
      credentials: "same-origin",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ password: input.value }),
    });
    if (response.ok) {
      location.replace("/");
      return;
    }
    const answer = await response.json().catch(() => ({}));
    switch (answer.code) {
      case "wrongPassword":
        show(t("WebUi.Web.SignIn.Wrong"));
        input.select();
        break;
      case "tooManyAttempts":
        show(t("WebUi.Web.SignIn.TooMany", Math.max(1, Math.ceil((answer.retryAfterSeconds ?? 60) / 60))));
        break;
      case "noPassword":
        show(t("WebUi.Web.SignIn.NoPassword"));
        break;
      default:
        show(t("WebUi.Web.SignIn.Failed"));
    }
  } catch {
    show(t("WebUi.Web.SignIn.Failed"));
  } finally {
    button.disabled = false;
  }
}

// Until the app's language is known, the page follows the browser's.
document.documentElement.lang = navigator.language || "en";
document.getElementById("form").addEventListener("submit", signIn);
loadTexts();
