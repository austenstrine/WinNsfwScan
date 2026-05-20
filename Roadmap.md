# WinNsfwScan Development Roadmap

## Overview

This document outlines the planned development phases for the WinNsfwScan project — a background monitoring tool that detects NSFW content on screen and displays an overlay when detected.

---

## Phase 0: Current Foundation (Already Complete)

- System tray icon with context menu (Open + Exit)
- WebView2 frontend using Vite + Tailwind CSS
- `NudeNetClient` for communicating with the Python backend
- Basic application structure (`App.xaml`, `MainWindow.xaml`, etc.)
- `ScreenCaptureService` using `Graphics.CopyFromScreen`

**Goal:** Have a stable base before building the core monitoring features.

---

## Phase 1: Continuous Background Detection

**Goal:** Implement continuous screen monitoring and NSFW detection in the background.

### Tasks
- Create a `DetectionLoopService` responsible for:
  - Running a continuous capture → analyze loop
  - Using `ScreenCaptureService` to capture the screen
  - Sending captured images to `NudeNetClient`
  - Handling detection results
- Prevent multiple overlays from appearing at once
- Keep the detection loop running even while an overlay is visible
- Automatically start the detection loop on application startup
- Add basic error handling and logging

**Success Criteria:**  
The application can run in the background and detect NSFW content from the screen.

---

## Phase 2: Overlay System

**Goal:** Display a visual response when NSFW content is detected.

### Tasks
- Create a simple `OverlayWindow` (full-screen)
- Show the overlay when NSFW content is detected
- Allow the user to dismiss the overlay by clicking on it
- Ensure only one overlay can exist at a time
- Add basic debounce logic to prevent rapid flickering

**Initial Version:**  
A full-screen overlay with a message such as:  
*"NSFW content detected — Click to dismiss"*

**Success Criteria:**  
When NSFW is detected, a dismissible full-screen overlay is shown.

---

## Phase 3: Polish & Stability

**Goal:** Make the application stable enough for extended use.

### Tasks
- Improve performance and efficiency of the capture/detection loop
- Add basic status tracking (last detection time, current state, etc.)
- Handle edge cases gracefully (failed captures, missing screens, etc.)
- Improve overall error handling and resilience
- Ensure the app starts minimized to the system tray
- Add the ability to enable/disable monitoring (via tray menu or settings)

**Success Criteria:**  
The application can run reliably for long periods without crashing or spamming errors.

---

## Phase 4: Advanced Features (Future)

**Goal:** Improve the overlay and add more intelligent behavior.

### Possible Features
- Transparent overlay instead of a solid blocker
- Draw censorship boxes over detected regions
- Continue updating censorship boxes while the overlay is visible
- Support for capturing specific windows (instead of full screen)
- Multi-monitor support
- Improved frontend UI (detection history, status indicators, controls)
- User-configurable settings (sensitivity, scope, notifications, etc.)
- Browser integration (Chrome DevTools Protocol or browser extension)

---

## Suggested Implementation Order

| Priority | Phase       | Focus                    | Difficulty | Recommendation                     |
|----------|-------------|--------------------------|------------|------------------------------------|
| 1        | Phase 1     | Detection Loop           | Medium     | Start here                         |
| 2        | Phase 2     | Overlay System           | Medium     | Build after detection loop works   |
| 3        | Phase 3     | Polish & Stability       | Medium     | Focus on reliability               |
| 4        | Phase 4     | Advanced Features        | High       | Only after core functionality works |

---

## Recommendations

- Start with **Phase 1**. Get the continuous detection loop working reliably before building the overlay.
- Keep the initial overlay simple (full-screen blocker). Move to transparent + censorship boxes in a later phase.
- Add basic logging early to help debug the detection loop.
- You can continue using `Graphics.CopyFromScreen` for now — it's simpler and will help you move faster.
- Focus on making the core loop stable before adding complexity (multi-monitor support, window targeting, browser integration, etc.).

---

**Last Updated:** May 2026