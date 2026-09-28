// Watches the page's main <video>/<audio> element and reports its state to the background worker.
// The background worker decides what to tell Baton; this script only observes, pauses and seeks.

(() => {
  if (window.__batonContent) return;
  window.__batonContent = true;

  let media = null;
  let lastSent = "";

  /** The element that matters: the one playing, else the largest one with a duration. */
  function mainMedia() {
    const all = [...document.querySelectorAll("video, audio")].filter((el) => el.readyState > 0);
    if (all.length === 0) return null;
    const playing = all.find((el) => !el.paused && !el.ended);
    if (playing) return playing;
    return all.sort((a, b) => area(b) - area(a))[0];
  }

  function area(el) {
    const rect = el.getBoundingClientRect();
    return rect.width * rect.height + (el.tagName === "AUDIO" ? 1 : 0);
  }

  function metadata() {
    const meta = navigator.mediaSession && navigator.mediaSession.metadata;
    const artwork = meta && meta.artwork && meta.artwork.length ? meta.artwork[meta.artwork.length - 1].src : null;
    return {
      title: (meta && meta.title) || document.title,
      artist: (meta && meta.artist) || null,
      artwork: artwork || null
    };
  }

  function state() {
    const el = media;
    const info = metadata();
    if (!el) return { url: location.href, pageTitle: document.title, media: null };
    const live = !isFinite(el.duration);
    return {
      url: location.href,
      pageTitle: document.title,
      media: {
        title: info.title,
        artist: info.artist,
        artwork: info.artwork,
        positionMs: Math.round((el.currentTime || 0) * 1000),
        durationMs: live ? 0 : Math.round((el.duration || 0) * 1000),
        playing: !el.paused && !el.ended,
        rate: el.playbackRate || 1,
        volume: el.muted ? 0 : el.volume,
        live
      }
    };
  }

  function report(force) {
    const current = state();
    // Positions advance on their own; only report changes a viewer would notice.
    const key = JSON.stringify({ ...current, media: current.media && { ...current.media, positionMs: Math.round(current.media.positionMs / 5000) } });
    if (!force && key === lastSent) return;
    lastSent = key;
    chrome.runtime.sendMessage({ type: "state", state: current }).catch(() => {});
  }

  function attach(el) {
    if (el === media) return;
    if (media) ["play", "pause", "seeked", "loadedmetadata", "ended", "ratechange"].forEach((e) => media.removeEventListener(e, onEvent));
    media = el;
    if (media) ["play", "pause", "seeked", "loadedmetadata", "ended", "ratechange"].forEach((e) => media.addEventListener(e, onEvent));
    report(true);
    if (media && media.readyState > 0) chrome.runtime.sendMessage({ type: "ready", url: location.href }).catch(() => {});
  }

  function onEvent(event) {
    report(true);
    if (event.type === "loadedmetadata") chrome.runtime.sendMessage({ type: "ready", url: location.href }).catch(() => {});
  }

  // Sites swap their players around (YouTube reuses one element across videos, Twitch replaces it).
  setInterval(() => {
    attach(mainMedia());
    report(false);
  }, 2000);
  attach(mainMedia());

  /** Remote control from another device: play, pause, seek, skip, next and volume. */
  function command({ action, positionMs, volume }) {
    const el = media;
    switch (action) {
      case "play": el.play().catch(() => {}); break;
      case "pause": el.pause(); break;
      case "toggle": el.paused ? el.play().catch(() => {}) : el.pause(); break;
      case "seek": if (isFinite(el.duration)) el.currentTime = Math.max(0, positionMs / 1000); break;
      case "skip": el.currentTime = Math.max(0, el.currentTime + positionMs / 1000); break;
      case "volume":
        el.volume = Math.min(1, Math.max(0, volume));
        if (volume > 0) el.muted = false;
        break;
      case "next": clickFirst([".ytp-next-button", "[data-testid=control-button-skip-forward]", "button[aria-label*='Next' i]"]); break;
      case "previous": clickFirst(["[data-testid=control-button-skip-back]", "button[aria-label*='Previous' i]"]) || (el.currentTime = 0); break;
    }
    setTimeout(() => report(true), 150);
  }

  function clickFirst(selectors) {
    for (const selector of selectors) {
      const button = document.querySelector(selector);
      if (button) {
        button.click();
        return true;
      }
    }
    return false;
  }

  /**
   * Moves to `target` and keeps it there for a while: a page that reloads or resumes (YouTube)
   * jumps back to its own saved spot after loading, which would undo a single seek. For the next
   * 15 s, a jump back behind where playback should be is corrected; ads (shorter than the target)
   * are left alone and the content video is caught when it starts.
   */
  let hold = null;
  function holdAt(target) {
    hold = { target, since: performance.now() };
    enforce();
  }

  function enforce() {
    const el = media;
    if (!hold || !el) return;
    const elapsed = (performance.now() - hold.since) / 1000;
    if (elapsed > 15) {
      hold = null;
      return;
    }
    if (!isFinite(el.duration) || el.duration < hold.target) return; // an ad, or not loaded yet
    // Playback can only have moved on by the time since the hold began; anything else is the
    // page putting back its own position.
    if (el.currentTime < hold.target - 3 || el.currentTime > hold.target + elapsed + 4) el.currentTime = hold.target;
  }

  // The person takes over: their own seeking wins.
  ["mousedown", "keydown", "touchstart"].forEach((type) => document.addEventListener(type, () => (hold = null), true));

  document.addEventListener("timeupdate", enforce, true);
  document.addEventListener("loadedmetadata", enforce, true);
  document.addEventListener("playing", enforce, true);

  chrome.runtime.onMessage.addListener((message, _sender, reply) => {
    if (message.type === "take") {
      const snapshot = state();
      if (message.pause && media && !media.paused) media.pause();
      reply(snapshot);
      return;
    }
    if (message.type === "command" && media) {
      command(message);
      reply(true);
      return;
    }
    if (message.type === "seek") {
      if (!media) {
        reply({ applied: false, reason: "no player" });
        return;
      }
      const before = media.currentTime;
      holdAt(message.positionMs / 1000);
      if (message.play) media.play().catch(() => {});
      reply({ applied: true, before, after: media.currentTime, duration: media.duration });
    }
  });
})();
