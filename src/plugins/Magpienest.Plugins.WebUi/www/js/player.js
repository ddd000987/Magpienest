// The video player: the browser's own controls on /api/file/{id}, resuming where the user stopped. Progress is sent
// every 10 seconds while playing, on pause and when the player closes; the server turns 95 % into "completed".

import { api, urls, beacon } from "./api.js";
import { t, textElement } from "./i18n.js";
import { iconButton } from "./icons.js";
import { back } from "./router.js";

const SAVE_EVERY_MS = 10 * 1000;
const FINISHED_SHARE = 0.95;
const CHROME_MS = 2500;

/** Opens video `id` in `root`. Returns `{ close() }`. */
export function openPlayer(root, id) {
  let closed = false;
  let seconds = 0;
  let opened = false;
  let lastSave = Date.now();
  let resumeAt = 0;
  let chromeTimer = 0;

  root.className = "overlay video";
  const box = document.createElement("div");
  box.className = "player";
  const video = document.createElement("video");
  video.controls = true;
  video.playsInline = true;
  video.preload = "metadata";
  video.setAttribute("playsinline", "");
  box.append(video);

  const top = document.createElement("div");
  top.className = "chrome top";
  const backButton = iconButton("back", "WebUi.Web.Back", t);
  const title = document.createElement("span");
  title.className = "title";
  top.append(backButton, title);

  const message = document.createElement("div");
  message.className = "overlay-message";
  message.hidden = true;
  message.append(textElement("p", "WebUi.Web.Player.CannotPlay"));

  root.replaceChildren(box, top, message);
  root.hidden = false;

  backButton.addEventListener("click", () => back("#/kind/video"));
  video.addEventListener("loadedmetadata", () => {
    if (resumeAt > 0 && Number.isFinite(video.duration) && resumeAt < video.duration * FINISHED_SHARE) {
      video.currentTime = resumeAt;
    }
  });
  video.addEventListener("pause", () => save(false));
  video.addEventListener("ended", () => save(true));
  video.addEventListener("error", () => {
    if (!closed) {
      message.hidden = false;
      box.hidden = true;
    }
  });
  // The title bar shows while the mouse moves or the video is paused, like the browser's own controls.
  root.addEventListener("pointermove", showChrome);
  root.addEventListener("pointerdown", showChrome);
  video.addEventListener("play", showChrome);
  video.addEventListener("pause", () => {
    clearTimeout(chromeTimer);
    root.classList.remove("chrome-hidden");
  });
  window.addEventListener("keydown", onKey);
  window.addEventListener("pagehide", flushOnLeave);
  document.addEventListener("visibilitychange", onVisibility);
  const ticker = setInterval(tick, 1000);

  start();

  async function start() {
    try {
      const item = await api.item(id);
      if (closed) {
        return;
      }

      title.textContent = item.title;
      resumeAt = item.status !== "completed" && item.progressPosition > 0 ? item.progressPosition / 1000 : 0;
    } catch {
      // Still try to play; the title stays empty.
    }

    if (!closed) {
      video.src = urls.file(id);
      video.play().catch(() => {
        // Autoplay can be refused (phones); the controls start it.
      });
    }
  }

  function showChrome() {
    root.classList.remove("chrome-hidden");
    clearTimeout(chromeTimer);
    chromeTimer = setTimeout(() => {
      if (!video.paused) {
        root.classList.add("chrome-hidden");
      }
    }, CHROME_MS);
  }

  function tick() {
    if (!video.paused && !video.ended) {
      seconds++;
      if (Date.now() - lastSave >= SAVE_EVERY_MS) {
        save(false);
      }
    }
  }

  function body(reachedEnd) {
    const duration = video.duration;
    if (!Number.isFinite(duration) || duration <= 0) {
      return null;
    }

    const result = {
      progressPosition: Math.round((reachedEnd ? duration : video.currentTime) * 1000),
      progressTotal: Math.round(duration * 1000),
    };
    if (reachedEnd) {
      result.reachedEnd = true;
    }

    if (seconds > 0) {
      result.addTimeSpentSeconds = seconds;
    }

    if (!opened) {
      result.markOpened = true;
    }

    return result;
  }

  function save(reachedEnd) {
    const data = body(reachedEnd);
    lastSave = Date.now();
    if (!data) {
      return;
    }

    seconds = 0;
    opened = true;
    api.patch(id, data).catch(() => {});
  }

  function flushOnLeave() {
    const data = body(false);
    if (data) {
      seconds = 0;
      opened = true;
      beacon(id, data);
    }
  }

  function onVisibility() {
    if (document.visibilityState === "hidden") {
      flushOnLeave();
    }
  }

  function onKey(event) {
    if (event.key === "Escape" && !document.fullscreenElement) {
      back("#/kind/video");
    } else if (event.key === " " && event.target !== video) {
      event.preventDefault();
      if (video.paused) {
        video.play().catch(() => {});
      } else {
        video.pause();
      }
    }
  }

  return {
    close() {
      if (!video.ended) {
        flushOnLeave();
      }

      closed = true;
      clearInterval(ticker);
      clearTimeout(chromeTimer);
      window.removeEventListener("keydown", onKey);
      window.removeEventListener("pagehide", flushOnLeave);
      document.removeEventListener("visibilitychange", onVisibility);
      video.pause();
      video.removeAttribute("src");
      video.load();
      root.hidden = true;
      root.replaceChildren();
      root.className = "overlay";
    },
  };
}
