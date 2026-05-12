# OnAcceleratedPaint Rendering Pipeline

**Date:** 2026-05-13
**Status:** Design (awaiting implementation plan)

## Overview

Replace the current `OnPaint` CPU-buffer rendering path with CEF's `OnAcceleratedPaint` GPU-accelerated path. This eliminates CPU-side per-frame buffer copies, PCIe `UpdateSubresource` transfers, and `Flush()` synchronization — CEF renders directly to D3D11 shared textures that we composite GPU-to-GPU.

## Motivation

The current `OnPaint` path has per-frame overhead from:
1. `Buffer.MemoryCopy` of full-resolution BGRA buffer to `_alphaLookupBuffer` (CPU)
2. `UpdateSubresource` dirty rect upload from CPU → GPU (PCIe transfer)
3. `Flush()` to synchronize before the plugin reads the texture

`OnAcceleratedPaint` (CEF's `EnableAcceleratedPaint`) lets CEF render directly to D3D11 shared textures. The shared handle arrives in the callback, eliminating all CPU-side data movement.

## Architecture

### Old Pipeline

```
CEF render → OnPaint CPU buffer
  → Buffer.MemoryCopy → _alphaLookupBuffer (CPU, per-frame)
  → UpdateSubresource → _viewTexture/_popupTexture (CPU→GPU PCIe)
  → CopySubresourceRegion → _sharedTexture (GPU→GPU composite)
  → Flush() (sync point)
  → SharedTextureHandle → IPC → Plugin OpenSharedResource → ImGui
```

### New Pipeline

```
CEF render to D3D11 shared texture
  → OnAcceleratedPaint (shared handle)
  → CopySubresourceRegion → _sharedTexture (GPU→GPU)
  → Plugin reads _sharedTexture (unchanged)
  → Alpha: CopySubresourceRegion 1px → staging texture → Map (on-demand)
```

### Changes by Layer

| Layer | File | Impact |
|---|---|---|
| Renderer | `TextureRenderHandler.cs` | Rewrite: implement `OnAcceleratedPaint`, remove `OnPaint`, remove `_alphaLookupBuffer` |
| Renderer | `CefHandler.cs` | +1 line: enable `EnableAcceleratedPaint` |
| Renderer | `Browsingway.Renderer.csproj` | Bump CefSharp 143.0.90 → 147.0.100 |
| Plugin | `Browsingway.csproj` | Bump CefSharp version (if referenced) |
| Plugin | `SharedTextureHandler.cs` | No changes |
| Plugin | `Overlay.cs` | No changes |
| Plugin | `Plugin.cs` | No changes |

## Texture Ownership

| Texture | Current | New |
|---|---|---|
| `_sharedTexture` | Created by us (D3D11_RESOURCE_MISC_SHARED), composites view+popup | Unchanged |
| `_viewTexture` | Created by us, `UpdateSubresource` from CPU | **Removed** |
| `_popupTexture` | Created by us, `UpdateSubresource` from CPU | **Removed** |
| `_cefViewTexture` | — | **New**: `OpenSharedResource` from CEF view handle, held as long-lived reference |
| `_cefPopupTexture` | — | **New**: `OpenSharedResource` from CEF popup handle |
| `_stagingTexture` | — | **New**: 1×1 px, D3D11_USAGE_STAGING, CPU_ACCESS_READ, for alpha detection |
| `_alphaLookupBuffer` | CPU `byte[]` per-frame copy | **Removed** |

## OnAcceleratedPaint Implementation

CEF calls `OnAcceleratedPaint(PaintElementType type, Rect dirtyRect, AcceleratedPaintInfo info)` for each paint element (View, Popup). The `info.SharedTextureHandle` is stable across frames (CEF reuses the same texture).

```
OnAcceleratedPaint(type, dirtyRect, info):
  lock(_renderLock):
    if type == View and handle changed:
      release _cefViewTexture; OpenSharedResource(new handle); save handle
    if type == Popup and handle changed:
      release _cefPopupTexture; OpenSharedResource(new handle); save handle

    if type == View:
      CopySubresourceRegion(_cefViewTexture → _sharedTexture, full)
    if type == Popup and _popupVisible:
      CopySubresourceRegion(_cefPopupTexture → _sharedTexture, at popupPos)
```

Key differences from `OnPaint`:
- No `UpdateSubresource` (CEF renders GPU-side)
- No `Flush()` (CopySubresourceRegion is FIFO-ordered on immediate context, no CPU→GPU sync needed)
- No `Buffer.MemoryCopy` to alpha buffer
- CEF texture handle opened once, reused across frames until resize

## Alpha Detection (Click-Through)

Replace the per-frame full-resolution `_alphaLookupBuffer` copy with an on-demand staging texture read.

### Implementation

A single 1×1 `D3D11_USAGE_STAGING` texture is created at init time (global, reused).

On every mouse position update:
1. `CopySubresourceRegion(_sharedTexture, 1px at (x,y) → _stagingTexture)` — GPU→GPU
2. `Map(_stagingTexture, READ)` — GPU→CPU readback for one pixel
3. Read `pData[3]` (BGRA, offset 3 = alpha byte)
4. `Unmap(_stagingTexture)`

### Performance

| Metric | Old (CPU buffer) | New (staging texture) |
|---|---|---|
| Per-frame cost | `width × height × 4` bytes copy (~8MB at 1080p) | 0 |
| Per mouse event | Array index O(1), ~0 | Map/Unmap round-trip + 1px GPU copy |
| Memory | `width × height × 4` bytes heap | 4 bytes (staging texture) |

The staging texture approach converts a per-frame cost into per-interaction cost. Map/Unmap on a 1-pixel texture is ~0.01ms.

## Popup Compositing

Logic unchanged from current code:

- `OnPopupShow(show)` → sets `_popupVisible`
- `OnPopupSize(rect)` → stores `_popupRect`, releases old `_cefPopupTexture` reference (new handle arrives in next `OnAcceleratedPaint(Popup, ...)`)
- `OnAcceleratedPaint(Popup, ...)` → `CopySubresourceRegion` from `_cefPopupTexture` to `_sharedTexture` at the scaled popup position

## Resize

```
Resize(size):
  lock(_renderLock):
    replace _sharedTexture (new D3D11_RESOURCE_MISC_SHARED)
    release _cefViewTexture, _cefPopupTexture (new handles arrive next frame)
    queue old _sharedTexture for deferred disposal
    reset _sharedTextureHandle
```

`BuildViewTexture` is simplified: only creates the shared texture, `isShared` parameter removed (always true).

## Thread Safety

`_renderLock` scope is reduced. It now only protects:
- CopySubresourceRegion calls (GPU operations on shared textures)
- CEF texture handle/pointer transitions

No longer needed:
- Alpha buffer protection (`_alphaLookupBuffer` is gone; staging texture Map/Unmap is naturally thread-safe)

Staging texture is only accessed from the main render thread (mouse events are processed on the framework thread, which serializes with draw).

## Error Handling

| Scenario | Response |
|---|---|
| CEF never calls OnAcceleratedPaint | Plugin shows existing "render error" placeholder |
| OpenSharedResource failure | Throw, caught by existing `_hasRenderError` path |
| Staging texture Map failure | Return alpha 255 (opaque), log warning |
| CEF destroys texture between frames | AddRef/Release on our pointer prevents use-after-free |

## CefSharp 143 → 147 Upgrade

- NuGet package versions: `143.0.90` → `147.0.100`
- `OnAcceleratedPaint` and `AcceleratedPaintInfo` exist in both versions (no API change)
- Compile-time detection of any breaking API changes in `IRenderHandler` or `CefSettings`
- `EnableAcceleratedPaint` setting location confirmed via intellisense at implementation time (expected: `CefSettings.EnableAcceleratedPaint`)

## Files Modified

| File | Change |
|---|---|
| `Browsingway.Renderer/TextureRenderHandler.cs` | Rewrite (~200 lines net reduction) |
| `Browsingway.Renderer/CefHandler.cs` | Add `EnableAcceleratedPaint` (1 line) |
| `Browsingway.Renderer/Browsingway.Renderer.csproj` | CefSharp version bump |
| `Browsingway/Browsingway.csproj` | CefSharp version bump (if ref present) |

**No changes to plugin-side files** (`Plugin.cs`, `Overlay.cs`, `SharedTextureHandler.cs`, `DxHandler.cs`).
