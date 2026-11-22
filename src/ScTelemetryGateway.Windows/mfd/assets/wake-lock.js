"use strict";

const MfdWakeLock = (() => {
  const warning = "Could not keep the screen awake. Check your device's screen timeout and battery settings.";
  let enabled = false, generation = 0, sentinel = null, video = null, notify = () => {};

  function release() {
    generation++;
    const previous = sentinel;
    sentinel = null;
    if (previous) void previous.release().catch(() => notify(warning));
    if (video) {
      video.pause();
      video.remove();
      video = null;
    }
  }

  function acquire() {
    const attempt = ++generation;
    notify("");
    if (navigator.wakeLock?.request) {
      let request;
      try {
        request = navigator.wakeLock.request("screen");
      } catch {
        notify(warning);
        return;
      }
      request.then(lock => {
        if (!enabled || generation !== attempt) {
          void lock.release().catch(() => notify(warning));
          return;
        }
        sentinel = lock;
        lock.addEventListener("release", () => {
          if (sentinel !== lock) return;
          sentinel = null;
          if (enabled && document.visibilityState === "visible") notify(warning);
        });
      }).catch(() => {
        if (enabled && generation === attempt) notify(warning);
      });
      return;
    }

    const player = document.createElement("video");
    player.src = "/assets/keep-awake.mp4";
    player.muted = true;
    player.loop = true;
    player.playsInline = true;
    player.setAttribute("playsinline", "");
    player.setAttribute("aria-hidden", "true");
    player.className = "wake-video";
    document.body.append(player);
    video = player;
    player.addEventListener("pause", () => {
      if (enabled && generation === attempt && document.visibilityState === "visible") notify(warning);
    });
    player.addEventListener("error", () => {
      if (enabled && generation === attempt) notify(warning);
    });
    let playback;
    try {
      playback = player.play();
    } catch {
      notify(warning);
      return;
    }
    playback.then(() => {
      if (!enabled || generation !== attempt) player.pause();
    }).catch(() => {
      if (enabled && generation === attempt) notify(warning);
    });
  }

  function init(onWarning) {
    notify = onWarning;
    document.addEventListener("visibilitychange", () => {
      if (!enabled) return;
      release();
      if (document.visibilityState === "visible") acquire();
    });
  }

  function start() {
    enabled = true;
    acquire();
  }

  function stop() {
    enabled = false;
    release();
  }

  return { init, start, stop };
})();
