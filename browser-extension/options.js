const EXT = globalThis.browser ?? chrome;

const defaults = {
  serverUrl: "http://127.0.0.1:8765/detect",
  confidenceThreshold: 0.45,
  captureQuality: 80,
  backPollMs: 200,
  backCooldownMs: 1000
};

const serverUrlEl = document.getElementById("serverUrl");
const confidenceThresholdEl = document.getElementById("confidenceThreshold");
const captureQualityEl = document.getElementById("captureQuality");
const backPollMsEl = document.getElementById("backPollMs");
const backCooldownMsEl = document.getElementById("backCooldownMs");
const saveBtn = document.getElementById("saveBtn");
const statusEl = document.getElementById("status");

async function load() {
  const saved = await EXT.storage.local.get(Object.keys(defaults));
  const cfg = { ...defaults, ...saved };

  serverUrlEl.value = cfg.serverUrl;
  confidenceThresholdEl.value = String(cfg.confidenceThreshold);
  captureQualityEl.value = String(cfg.captureQuality);
  backPollMsEl.value = String(cfg.backPollMs);
  backCooldownMsEl.value = String(cfg.backCooldownMs);
}

function toNumber(value, fallback) {
  const n = Number(value);
  return Number.isFinite(n) ? n : fallback;
}

saveBtn.addEventListener("click", async () => {
  const patch = {
    serverUrl: serverUrlEl.value.trim() || defaults.serverUrl,
    confidenceThreshold: Math.min(1, Math.max(0, toNumber(confidenceThresholdEl.value, defaults.confidenceThreshold))),
    captureQuality: Math.min(100, Math.max(1, Math.round(toNumber(captureQualityEl.value, defaults.captureQuality)))),
    backPollMs: Math.max(50, Math.round(toNumber(backPollMsEl.value, defaults.backPollMs))),
    backCooldownMs: Math.max(0, Math.round(toNumber(backCooldownMsEl.value, defaults.backCooldownMs)))
  };

  await EXT.storage.local.set(patch);
  await EXT.runtime.sendMessage({ type: "update-settings", patch });

  statusEl.textContent = "Saved.";
  setTimeout(() => {
    statusEl.textContent = "";
  }, 1400);
});

void load();
