# Browsingway XIVLauncherCN Adaptation

**Date:** 2026-05-13
**Status:** Approved

## Overview

Adapt Browsingway for the XIVLauncherCN (Chinese FFXIV Launcher) ecosystem. Three targeted changes: SDK switch, library path, and CEF download mirror.

## Changes

### 1. SDK: `Dalamud.NET.Sdk` → `Dalamud.CN.NET.Sdk`

**File:** `Browsingway/Browsingway.csproj:1`

XIVLauncherCN uses a different Dalamud SDK package.

### 2. DalamudLibPath: `XIVLauncher` → `XIVLauncherCN`

**File:** `Browsingway.Renderer/Browsingway.Renderer.csproj:25`

The CN launcher installs to a different Windows appdata path.

### 3. CEF Download: GitHub Releases → JsDelivr CDN

**File:** `Browsingway/DependencyManager.cs:44`

The CEF binary zip is hosted on GitHub Releases, inaccessible in some regions of China. Replace the download URL with a JsDelivr CDN URL. SHA256 checksum unchanged (same file).

If JsDelivr cannot serve GitHub Release assets directly, fall back to a Chinese mirror (e.g., ghproxy).

## Files Modified

| File | Change |
|---|---|
| `Browsingway/Browsingway.csproj` | Line 1: SDK package name |
| `Browsingway.Renderer/Browsingway.Renderer.csproj` | Line 25: DalamudLibPath |
| `Browsingway/DependencyManager.cs` | Lines 44-46: CEF download URL |

## Unchanged

- All rendering, IPC, and plugin logic
- Plugin manifest (`Browsingway.json`)
- OnAcceleratedPaint GPU optimization (includes this change)
- SHA256 checksum
