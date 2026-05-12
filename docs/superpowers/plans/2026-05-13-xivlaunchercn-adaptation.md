# XIVLauncherCN Adaptation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Adapt Browsingway for XIVLauncherCN by switching SDK, library paths, and CEF download mirror.

**Architecture:** Three isolated file changes — SDK package name in plugin csproj, DalamudLibPath in renderer csproj, CEF download URL in DependencyManager. No logic changes.

**Tech Stack:** .NET 10, CefSharp 147, JsDelivr CDN

**Spec:** `docs/superpowers/specs/2026-05-13-xivlaunchercn-adaptation-design.md`

---

### Task 1: Switch SDK to Dalamud.CN.NET.Sdk

**Files:**
- Modify: `Browsingway/Browsingway.csproj:1`

- [ ] **Step 1: Change SDK package name**

```xml
<Project Sdk="Dalamud.CN.NET.Sdk/15.0.0">
```

Replace `Dalamud.NET.Sdk/15.0.0` with `Dalamud.CN.NET.Sdk/15.0.0` on line 1.

- [ ] **Step 2: Commit**

```bash
git add Browsingway/Browsingway.csproj
git commit -m "build: switch SDK to Dalamud.CN.NET.Sdk for XIVLauncherCN"
```

---

### Task 2: Update DalamudLibPath for CN launcher

**Files:**
- Modify: `Browsingway.Renderer/Browsingway.Renderer.csproj:25`

- [ ] **Step 1: Change DalamudLibPath**

```xml
<DalamudLibPath Condition="$([MSBuild]::IsOSPlatform('Windows'))">$(appdata)\XIVLauncherCN\addon\Hooks\dev\</DalamudLibPath>
```

Replace `XIVLauncher` with `XIVLauncherCN` in the path.

- [ ] **Step 2: Commit**

```bash
git add Browsingway.Renderer/Browsingway.Renderer.csproj
git commit -m "build: update DalamudLibPath for XIVLauncherCN"
```

---

### Task 3: Replace CEF download URL with JsDelivr CDN

**Files:**
- Modify: `Browsingway/DependencyManager.cs:44-46`

- [ ] **Step 1: Change download URL**

The current URL:
```
https://github.com/Styr1x/Browsingway/releases/download/cef-binaries/cefsharp-{VERSION}.zip
```

Replace with JsDelivr CDN URL. Since GitHub Release assets may not be directly accessible via JsDelivr's `gh` source, use the JsDelivr GitHub mirror format. If that doesn't work, verify at implementation time and use an alternative Chinese mirror (e.g., ghproxy).

Try this JsDelivr format first:
```
https://cdn.jsdelivr.net/gh/Styr1x/Browsingway@cef-binaries/cefsharp-{VERSION}.zip
```

If the above isn't available (Release assets vs repo files), use:
```
https://ghproxy.com/https://github.com/Styr1x/Browsingway/releases/download/cef-binaries/cefsharp-{VERSION}.zip
```

Update the `Url` field in the `_dependencies` array, line 44. The `Checksum` stays the same.

- [ ] **Step 2: Commit**

```bash
git add Browsingway/DependencyManager.cs
git commit -m "fix: replace CEF download URL with China-friendly mirror"
```

---

### Task 4: Verify build

- [ ] **Step 1: Build with new SDK**

```bash
dotnet restore Browsingway/Browsingway.csproj
dotnet build Browsingway.sln 2>&1 | tail -20
```

Note: May fail on Linux due to missing Dalamud.CN.NET.Sdk. This SDK is Windows-only, XIVLauncherCN-specific. Build verification requires a Windows environment with XIVLauncherCN installed.

- [ ] **Step 2: Commit any generated lock files**

```bash
git add -A
git commit -m "chore: update lock files for XIVLauncherCN SDK"
```
