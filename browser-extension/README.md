# WinNsfwScan Browser Extension (Chromium)

This extension captures the visible tab, sends it to the local detector, and goes back once if objectionable content is detected.

## Objectionable classes

The filter is intentionally strict for:

- Any class containing `GENITALIA`
- Any class containing `ANUS`
- Any class containing `BUTTOCKS` or `BUTT`
- Any class containing `FEMALE_BREAST`

Covered and exposed variants are both matched by keyword.

## Backend requirement

Run one detector instance with a fixed port:

```bat
server\\server.exe --model 320n.onnx --resolution 320 --port 8765
```

Default extension endpoint:

- `http://127.0.0.1:8765/detect`

You can change this in the extension options page.

## Load unpacked

1. Open Chromium browser extensions page (`chrome://extensions` or `edge://extensions`).
2. Enable Developer mode.
3. Click Load unpacked.
4. Select this folder: `browser-extension`.

## Notes

- Continuous scanning in MV3 runs in a background service worker. Browsers may suspend the worker when idle.
- Restricted pages such as browser internal URLs cannot be captured.
