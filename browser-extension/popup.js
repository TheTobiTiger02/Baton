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

let sending = false;
function render(state) {
  root.textContent = "";
  if (!state) { root.append(element("div", { className: "muted", textContent: "Baton status unavailable." })); return; }
  if (!state.connected || state.awaitingApproval) {
    root.append(element("div", { className: "card" }, [
      element("div", { className: "title", textContent: state.awaitingApproval ? "Allow this browser in Baton" : "Baton isn't running" }),
      element("div", { className: "muted", textContent: state.awaitingApproval ? "Choose Allow in Baton → Settings → Browser." : "Start Baton on this PC to reconnect." })
    ]));
  }
  const tab = state.tab;
  const media = tab && tab.media;
  const card = element("div", { className: "card" }, [
    element("div", { className: "title", textContent: (media && media.title) || (tab && tab.pageTitle) || "This tab" }),
    element("div", { className: "muted", textContent: media ? (media.live ? "Live" : `${format(media.positionMs)} / ${format(media.durationMs)}`) : "Page" })
  ]);
  root.append(card);
  const online = state.devices.filter(d => d.online).sort((a, b) => Number(b.isDefault) - Number(a.isDefault));
  if (state.connected && !state.awaitingApproval && !online.length) card.append(element("div", { className: "muted", textContent: "No phone connected. Open Baton on your phone." }));
  function submit(deviceId, previous) {
    if (sending) return;
    sending = true;
    chrome.runtime.sendMessage({ type: "sendTab", tabId: state.tabId, targetDeviceId: deviceId, previousRequestId: previous }, () => {
      sending = false;
      refresh();
    });
    refresh();
  }
  for (const device of online) {
    const busy = (state.transfers || []).some(t => t.targetDeviceId === device.deviceId && t.status === "pending");
    const button = element("button", { textContent: busy ? "Sending…" : `Continue on ${device.name}`, disabled: sending || busy || state.awaitingApproval });
    button.addEventListener("click", () => submit(device.deviceId));
    card.append(button);
  }
  for (const transfer of state.transfers || []) {
    const labels = { pending: "Sending…", opened: "Opened on destination", fallback: "Fallback used", failed: "Couldn't continue", unconfirmed: "No confirmation received", unsupported: "Confirmation unavailable" };
    const result = element("div", { className: "card", role: "status" }, [
      element("div", { className: "title", textContent: labels[transfer.status] || "Status unavailable" }),
      element("div", { className: "muted", textContent: transfer.detail })
    ]);
    if (["failed", "unconfirmed"].includes(transfer.status)) {
      const connected = state.connected && state.confirmsHandoffs && online.some(d => d.deviceId === transfer.targetDeviceId);
      const retry = element("button", { textContent: "Retry", disabled: !connected || sending });
      retry.addEventListener("click", () => {
        if (transfer.status === "unconfirmed" && !window.confirm("The activity may already have opened. Try again?")) return;
        submit(transfer.targetDeviceId, transfer.requestId);
      });
      result.append(retry);
      if (!connected) result.append(element("div", { className: "muted", textContent: "Connect Baton and the destination to retry." }));
    }
    root.append(result);
  }
}
function refresh() {
  chrome.runtime.sendMessage({ type: "popup" }, state => {
    if (chrome.runtime.lastError) render(null); else render(state);
  });
}
refresh();
setInterval(refresh, 500);
