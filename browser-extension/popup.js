const EXT = globalThis.browser ?? chrome;

const toggleBtn = document.getElementById("toggleBtn");
const statusEl = document.getElementById("status");
const openOptionsBtn = document.getElementById("openOptions");

function send(message) {
  return EXT.runtime.sendMessage(message);
}

function render(state) {
  const enabled = Boolean(state?.settings?.enabled);
  const isGoingBack = Boolean(state?.runtime?.isGoingBack);

  toggleBtn.textContent = enabled ? "Disable" : "Enable";
  statusEl.textContent = enabled
    ? isGoingBack
      ? "Enabled. Navigation rollback in progress..."
      : "Enabled. Scanning active tab."
    : "Disabled.";
}

async function refresh() {
  const res = await send({ type: "get-state" });
  if (!res?.ok) {
    statusEl.textContent = `Error: ${res?.error ?? "unknown"}`;
    return;
  }

  render(res);
}

toggleBtn.addEventListener("click", async () => {
  const state = await send({ type: "get-state" });
  if (!state?.ok) {
    return;
  }

  await send({ type: "set-enabled", enabled: !state.settings.enabled });
  await refresh();
});

openOptionsBtn.addEventListener("click", () => {
  EXT.runtime.openOptionsPage();
});

void refresh();
