// Games: listed and described only. They can run on this PC alone, so the web page has no launch button.

import { formatDate, formatDuration } from "./i18n.js";

export const GAME_KIND = "game";

/** Whether items of a kind are games. */
export function isGame(kind) {
  return kind === GAME_KIND;
}

/** The short line under a game's tile: its play time, when it was played at all. */
export function gameTileLine(item) {
  return item.timeSpentSeconds > 0 ? formatDuration(item.timeSpentSeconds) : "";
}

/** The facts of a game's details: [label key, value] pairs. */
export function gameFacts(item) {
  return [
    ["WebUi.Web.Details.PlayTime", formatDuration(item.timeSpentSeconds)],
    ["WebUi.Web.Details.LastPlayed", formatDate(item.lastOpenedAt)],
    ["WebUi.Web.Details.Added", formatDate(item.addedAt)],
    ["WebUi.Web.Details.Folder", item.directory],
  ];
}
