const EXT = globalThis.browser ?? chrome;

const DEFAULT_SETTINGS = {
  enabled: true,
  serverUrl: "http://127.0.0.1:8765/detect",
  confidenceThreshold: 0.45,
  captureQuality: 80,
  scanDelayMs: 0,
  backPollMs: 200,
  backMaxWaitMs: 6000,
  backCooldownMs: 1000
};

let settings = { ...DEFAULT_SETTINGS };
let loopRunning = false;
let loopTimer = null;
let isGoingBack = false;
let lastBackAt = 0;

function delay(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

function clearLoopTimer() {
  if (loopTimer !== null) {
    clearTimeout(loopTimer);
    loopTimer = null;
  }
}

function scheduleNextTick(ms = settings.scanDelayMs) {
  clearLoopTimer();
  loopTimer = setTimeout(() => {
    void scanTick();
  }, Math.max(0, ms));
}

function log(...args) {
  console.log("[BrowserGuard]", ...args);
}

async function loadSettings() {
  const saved = await EXT.storage.local.get(Object.keys(DEFAULT_SETTINGS));
  settings = {
    ...DEFAULT_SETTINGS,
    ...saved
  };
}

async function getActiveTab() {
  const tabs = await EXT.tabs.query({ active: true, currentWindow: true });
  return tabs[0] ?? null;
}

function classIsObjectionable(className, score) {
  if (typeof className !== "string") {
    return false;
  }

  if (typeof score === "number" && score < settings.confidenceThreshold) {
    return false;
  }

  const upper = className.toUpperCase();

  if (upper.includes("GENITALIA")) {
    return true;
  }

  if (upper.includes("ANUS")) {
    return true;
  }

  if (upper.includes("BUTTOCKS") || upper.includes("BUTT")) {
    return true;
  }

  if (upper.includes("FEMALE_BREAST")) {
    return true;
  }

  return false;
}

function isTabCapturable(tab) {
  if (!tab || !tab.url) {
    return false;
  }

  const blockedPrefixes = ["chrome://", "edge://", "devtools://", "about:", "chrome-extension://"];
  return !blockedPrefixes.some((prefix) => tab.url.startsWith(prefix));
}

function captureVisibleTab(windowId) {
  return new Promise((resolve, reject) => {
    EXT.tabs.captureVisibleTab(
      windowId,
      { format: "jpeg", quality: Math.max(1, Math.min(100, settings.captureQuality)) },
      (dataUrl) => {
        const err = EXT.runtime.lastError;
        if (err) {
          reject(new Error(err.message));
          return;
        }

        if (!dataUrl) {
          reject(new Error("No capture data URL returned"));
          return;
        }

        resolve(dataUrl);
      }
    );
  });
}

async function postForDetections(dataUrl) {
  const imgBlob = await (await fetch(dataUrl)).blob();
  const form = new FormData();
  form.append("file", imgBlob, "capture.jpg");

  const resp = await fetch(settings.serverUrl, {
    method: "POST",
    body: form
  });

  if (!resp.ok) {
    throw new Error(`Detector error ${resp.status}`);
  }

  const payload = await resp.json();
  if (!payload || !Array.isArray(payload.detections)) {
    return [];
  }

  return payload.detections;
}

function tabGoBack(tabId) {
  return new Promise((resolve, reject) => {
    if (typeof EXT.tabs.goBack === "function") {
      EXT.tabs.goBack(tabId, () => {
        const err = EXT.runtime.lastError;
        if (err) {
          reject(new Error(err.message));
          return;
        }
        resolve();
      });
      return;
    }

    EXT.scripting.executeScript(
      {
        target: { tabId },
        func: () => {
          history.back();
        }
      },
      () => {
        const err = EXT.runtime.lastError;
        if (err) {
          reject(new Error(err.message));
          return;
        }
        resolve();
      }
    );
  });
}

async function waitUntilBackNavigationSettles(tabId) {
  const start = Date.now();
  while (Date.now() - start < settings.backMaxWaitMs) {
    let tab;
    try {
      tab = await EXT.tabs.get(tabId);
    } catch {
      return;
    }

    if (!tab || tab.status === "complete") {
      return;
    }

    await delay(settings.backPollMs);
  }
}

async function handleDetectedTab(tab) {
  if (isGoingBack) {
    return;
  }

  const now = Date.now();
  if (now - lastBackAt < settings.backCooldownMs) {
    return;
  }

  isGoingBack = true;
  try {
    await tabGoBack(tab.id);
    lastBackAt = Date.now();
    await waitUntilBackNavigationSettles(tab.id);
  } finally {
    isGoingBack = false;
  }
}

async function scanTick() {
  if (!loopRunning) {
    return;
  }

  if (!settings.enabled) {
    scheduleNextTick(250);
    return;
  }

  if (isGoingBack) {
    scheduleNextTick(settings.backPollMs);
    return;
  }

  try {
    const tab = await getActiveTab();
    if (!tab || !isTabCapturable(tab)) {
      scheduleNextTick(150);
      return;
    }

    const dataUrl = await captureVisibleTab(tab.windowId);
    const detections = await postForDetections(dataUrl);

    const objectionable = detections.filter((d) =>
      classIsObjectionable(d.class, d.score)
    );

    if (objectionable.length > 0) {
      log("Objectionable content detected", objectionable.map((d) => d.class));
      await handleDetectedTab(tab);
    }
  } catch (err) {
    log("scanTick error", err);
  }

  scheduleNextTick();
}

function startLoop() {
  if (loopRunning) {
    return;
  }

  loopRunning = true;
  scheduleNextTick(0);
}

function stopLoop() {
  loopRunning = false;
  clearLoopTimer();
}

async function setEnabled(enabled) {
  settings.enabled = Boolean(enabled);
  await EXT.storage.local.set({ enabled: settings.enabled });
}

async function updateSettings(patch) {
  settings = {
    ...settings,
    ...patch
  };
  await EXT.storage.local.set(settings);
}

EXT.runtime.onInstalled.addListener(async () => {
  await loadSettings();
  await EXT.storage.local.set({ ...DEFAULT_SETTINGS, ...settings });
  startLoop();
});

EXT.runtime.onStartup.addListener(async () => {
  await loadSettings();
  startLoop();
});

EXT.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  (async () => {
    if (!message || typeof message !== "object") {
      sendResponse({ ok: false, error: "Invalid message" });
      return;
    }

    if (message.type === "get-state") {
      await loadSettings();
      sendResponse({
        ok: true,
        settings,
        runtime: {
          loopRunning,
          isGoingBack,
          lastBackAt
        }
      });
      return;
    }

    if (message.type === "set-enabled") {
      await setEnabled(Boolean(message.enabled));
      sendResponse({ ok: true, enabled: settings.enabled });
      return;
    }

    if (message.type === "update-settings") {
      await updateSettings(message.patch ?? {});
      sendResponse({ ok: true, settings });
      return;
    }

    sendResponse({ ok: false, error: "Unknown message type" });
  })().catch((err) => {
    sendResponse({ ok: false, error: String(err) });
  });

  return true;
});

void (async () => {
  await loadSettings();
  startLoop();
})();
