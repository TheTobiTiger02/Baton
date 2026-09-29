const root = document.getElementById("root");

function element(tag, props = {}, children = []) {
  const node = Object.assign(document.createElement(tag), props);
  children.forEach((child) => node.append(child));
  return node;
}

function format(ms) {
  const s = Math.floor(ms / 1000);
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  const sec = String(s % 60).padStart(2, "0");
  return h > 0 ? `${h}:${String(m).padStart(2, "0")}:${sec}` : `${m}:${sec}`;
}

chrome.runtime.sendMessage({ type: "popup" }, (state) => {
  root.textContent = "";
  if (!state || !state.connected) {
    root.append(element("div", { className: "card" }, [
      element("div", { className: "title", textContent: "Baton isn't running" }),
      element("div", { className: "muted", textContent: "Start Baton on this PC to continue tabs on your phone." })
    ]));
    return;
  }

  if (state.awaitingApproval) {
    root.append(element("div", { className: "card" }, [
      element("div", { className: "title", textContent: "Allow this browser in Baton" }),
      element("div", { className: "muted", textContent: "Baton on this PC asks whether this browser may show its tabs. Choose Allow there (or in Settings → Browser)." })
    ]));
    return;
  }

  const tab = state.tab;
  const media = tab && tab.media;
  const card = element("div", { className: "card" }, [
    element("div", { className: "title", textContent: (media && media.title) || (tab && tab.pageTitle) || "This tab" }),
    element("div", {
      className: "muted",
      textContent: media ? (media.live ? "Live" : `${format(media.positionMs)} / ${format(media.durationMs)}`) : "Page"
    })
  ]);
  root.append(card);

  const online = state.devices.filter((d) => d.online).sort((a, b) => (b.isDefault ? 1 : 0) - (a.isDefault ? 1 : 0));
  if (online.length === 0) {
    root.append(element("div", { className: "muted", textContent: "No phone connected. Open Baton on your phone." }));
    return;
  }
  online.forEach((device) => {
    const button = element("button", { textContent: `Continue on ${device.name}` });
    button.addEventListener("click", () => {
      chrome.runtime.sendMessage({ type: "sendTab", tabId: state.tabId, targetDeviceId: device.deviceId });
      button.textContent = "Sent";
      button.disabled = true;
      setTimeout(() => window.close(), 700);
    });
    card.append(button);
  });
});
