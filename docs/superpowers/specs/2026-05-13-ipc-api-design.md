# Browsingway External IPC for HiAuRo

**Date:** 2026-05-13
**Status:** Design approved

## Overview

Add Dalamud `IPluginIpc` endpoints to Browsingway so external plugins (HiAuRo) can programmatically create, position, and control browser overlays. Five endpoints: IsReady, Exists, CreateOrUpdate, SetVisibility, SetPosition.

## Architecture

```
HiAuRo (consumer)                    Browsingway (provider)
  │                                       │
  │  IPluginIpc.InvokeFunc("Browsingway.IsReady")
  │ ─────────────────────────────────────→ │  check renderer + D3D11 alive
  │                                       │
  │  IPluginIpc.InvokeFunc("Browsingway.Overlay.Exists", name)
  │ ─────────────────────────────────────→ │  lookup _overlays dictionary
  │                                       │
  │  IPluginIpc.InvokeAction("Browsingway.Overlay.CreateOrUpdate", name, url, w, h, zoom, locked)
  │ ─────────────────────────────────────→ │  upsert Config.Inlays → HydrateOverlays
  │                                       │
  │  IPluginIpc.InvokeAction("Browsingway.Overlay.SetVisibility", name, visible)
  │ ─────────────────────────────────────→ │  InlayConfiguration.Hidden = !visible
  │                                       │
  │  IPluginIpc.InvokeAction("Browsingway.Overlay.SetPosition", name, x, y)
  │ ─────────────────────────────────────→ │  Overlay.SetPosition(x, y)
```

IPC overlays are stored in the same `Config.Inlays` list as user-created overlays. Users can see and manage them in the Browsingway settings panel.

## Endpoints

### 1. Browsingway.IsReady

- **Type:** Func `<void, bool>`
- **Returns:** `true` if render process is alive and D3D11 shared textures are available

### 2. Browsingway.Overlay.Exists

- **Type:** Func `<string, bool>`
- **Parameter:** `name` — overlay name (unique identifier)
- **Returns:** `true` if an overlay with this name exists

### 3. Browsingway.Overlay.CreateOrUpdate

- **Type:** Action `<string, string, int, int, float, bool>`
- **Parameters:**
  - `name` — unique identifier, recommended prefix `HiAuRo.xxx`
  - `url` — full URL (e.g. `http://localhost:5678/main.html`)
  - `width` — window width (px)
  - `height` — window height (px)
  - `zoom` — zoom percentage (default 100)
  - `locked` — prevent user dragging/resizing
- **Behavior:** Creates new overlay if name doesn't exist, updates URL/zoom/locked if it does. Handles resize via existing IPC to renderer.

### 4. Browsingway.Overlay.SetVisibility

- **Type:** Action `<string, bool>`
- **Parameters:**
  - `name` — overlay name
  - `visible` — show/hide
- **Behavior:** Sets `Hidden = !visible`. Does NOT destroy the CEF instance — toggling visibility is instant.

### 5. Browsingway.Overlay.SetPosition

- **Type:** Action `<string, int, int>`
- **Parameters:**
  - `name` — overlay name
  - `x` — screen X coordinate
  - `y` — screen Y coordinate
- **Behavior:** Positions the ImGui window on next frame. Position is NOT persisted (HiAuRo re-sets on each startup). Takes effect even on locked overlays.

## Overlay Class Changes

```csharp
// New field
private Vector2? _position;

// New method
public void SetPosition(int x, int y) => _position = new Vector2(x, y);

// New property
public string Name => _overlayConfig.Name;

// Render modification
if (_position.HasValue)
    ImGui.SetWindowPos(_position.Value, ImGuiCond.Always);
```

## Error Handling

| Scenario | Response |
|---|---|
| IsReady called before renderer starts | Return false |
| CreateOrUpdate before renderer ready | Write config, defer overlay creation until renderer ready callback |
| SetPosition on non-existent overlay | No-op (overlay not rendering) |
| SetVisibility on non-existent overlay | No-op |

## Lifecycle

- HiAuRo startup: poll IsReady → CreateOrUpdate → SetPosition → SetVisibility(true)
- HiAuRo shutdown: SetVisibility(false) for each overlay
- Overlays are NOT automatically deleted on HiAuRo unload — persist in config for next launch

## Files Modified

| File | Change |
|---|---|
| `Browsingway/Plugin.cs` | Register 5 IPC providers, CreateOrUpdate logic, expose HydrateOverlays |
| `Browsingway/Overlay.cs` | Add `_position`, `SetPosition()`, `Name` property, position application in Render |
| `Browsingway/Configuration.cs` | No changes (reuses existing InlayConfiguration fields) |
| `Browsingway/Settings.cs` | Expose `HydrateOverlays()` as internal method |

## Not Included (by design)

- Mouse/keyboard event forwarding (HiAuRo UI handles its own input)
- CSS injection, mute, framerate control, DevTools, click-through, opacity
- Auto-deletion of IPC-created overlays on HiAuRo unload
