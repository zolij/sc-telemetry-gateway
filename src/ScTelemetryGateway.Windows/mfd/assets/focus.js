"use strict";

const MfdFocus = (() => {
  const $ = id => document.getElementById(id);
  let active = false, available = false, pending = false, usedFullscreen = false, requestedFullscreen = false;
  let fullscreenMessage = "", wakeMessage = "";
  const fullscreenElement = () => document.fullscreenElement ?? document.webkitFullscreenElement;
  const standalone = () => navigator.standalone === true ||
    window.matchMedia("(display-mode: standalone)").matches ||
    window.matchMedia("(display-mode: fullscreen)").matches;

  function message(text) {
    fullscreenMessage = text;
    renderMessage();
  }
  function renderMessage() {
    const text = [fullscreenMessage, wakeMessage].filter(Boolean).join(" ");
    $("focus-message").textContent = text;
    $("focus-feedback").hidden = !text;
  }
  function controls() {
    $("focus-button").disabled = !available || pending;
    $("focus-button").setAttribute("aria-pressed", String(active));
    $("leave-focus").hidden = !active;
    document.body.classList.toggle("focus-mode", active);
  }
  function restore() {
    active = false;
    usedFullscreen = false;
    MfdWakeLock.stop();
    wakeMessage = "";
    message("");
    controls();
    $("focus-button").focus();
  }
  async function exitFullscreen() {
    if (fullscreenElement() !== document.documentElement) return;
    try {
      const exit = document.exitFullscreen ?? document.webkitExitFullscreen;
      if (!exit) throw new Error("Fullscreen exit is unavailable.");
      requestedFullscreen = false;
      await exit.call(document);
    } catch {
      message("Could not leave full screen. Use the browser's exit control or Escape.");
    }
  }
  async function leave() {
    if (!active) return;
    restore();
    await exitFullscreen();
  }
  async function enter() {
    if (!available || pending || active) return;
    active = true;
    pending = true;
    message("");
    controls();
    $("leave-focus").focus();
    MfdWakeLock.start();
    try {
      if (standalone() || fullscreenElement() === document.documentElement) return;
      const root = document.documentElement;
      const request = root.requestFullscreen ?? root.webkitRequestFullscreen;
      if (!request) {
        message("Full screen is not supported by this browser. On iPhone/iPad, add this page to the Home Screen and open it there, then select Focus Mode.");
        return;
      }
      // Request inside the button gesture; mobile browsers require user activation.
      requestedFullscreen = true;
      await request.call(root, { navigationUI: "hide" });
      if (!active) await exitFullscreen();
      else usedFullscreen = fullscreenElement() === root;
    } catch {
      if (active) message("The browser refused full screen. Focus mode is still active; exit and try again, or open this page from the Home Screen.");
    } finally {
      pending = false;
      controls();
    }
  }
  function update(canEnter, canStay) {
    available = canEnter;
    if (!canStay) void leave();
    controls();
  }
  function init() {
    MfdWakeLock.init(text => {
      if (!active) return;
      wakeMessage = text;
      renderMessage();
    });
    $("focus-button").addEventListener("click", enter);
    $("leave-focus").addEventListener("click", leave);
    $("dismiss-focus-message").addEventListener("click", () => {
      wakeMessage = "";
      message("");
    });
    document.addEventListener("keydown", event => {
      if (event.key === "Escape" && active) void leave();
    });
    const changed = () => {
      if (!active) {
        if (requestedFullscreen && fullscreenElement() === document.documentElement) void exitFullscreen();
        return;
      }
      if (fullscreenElement() === document.documentElement) usedFullscreen = true;
      else if (usedFullscreen) restore();
    };
    document.addEventListener("fullscreenchange", changed);
    document.addEventListener("webkitfullscreenchange", changed);
    document.addEventListener("webkitfullscreenerror", () => {
      if (active) message("The browser refused full screen. Focus mode is still active.");
    });
    controls();
  }
  return { init, update, leave };
})();
