# OnAcceleratedPaint GPU Rendering Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace OnPaint CPU-buffer rendering with CEF's OnAcceleratedPaint GPU-accelerated path, reducing per-frame CPU overhead and eliminating PCIe texture uploads.

**Architecture:** CEF renders directly to D3D11 shared textures. OnAcceleratedPaint provides shared handles that we open with OpenSharedResource, then CopySubresourceRegion to composite view+popup into our own _sharedTexture. Plugin-side code is unchanged.

**Tech Stack:** C# .NET 10, TerraFX.Interop.Windows (D3D11), CefSharp 147.0.100

**Spec:** `docs/superpowers/specs/2026-05-13-accelerated-paint-design.md`

---

### Task 1: Bump CefSharp NuGet packages

**Files:**
- Modify: `Browsingway.Renderer/Browsingway.Renderer.csproj:43-49`

- [ ] **Step 1: Update CefSharp version numbers**

```xml
<PackageReference Include="CefSharp.Common.NETCore" Version="147.0.100">
    <ExcludeAssets>runtime;contentFiles;native</ExcludeAssets>
</PackageReference>
<PackageReference Include="CefSharp.OffScreen.NETCore" Version="147.0.100" PrivateAssets="none">
    <ExcludeAssets>contentFiles;native</ExcludeAssets>
    <IncludeAssets>runtime; compile; build; analyzers; buildtransitive</IncludeAssets>
</PackageReference>
```

- [ ] **Step 2: Restore packages and check for compile errors from API breakage**

Run: `dotnet restore Browsingway.Renderer/Browsingway.Renderer.csproj`
Expected: SUCCESS (packages restored)

Run: `dotnet build Browsingway.Renderer/Browsingway.Renderer.csproj --no-restore 2>&1 | head -50`
Expected: May have compile errors from CefSharp API changes (e.g., new IRenderHandler members). These will be fixed in subsequent tasks. Note any errors for reference.

- [ ] **Step 3: If Browsingway.csproj has a direct CefSharp reference, update it too**

Check `Browsingway/Browsingway.csproj` for any `<PackageReference Include="CefSharp...` lines. If none found, skip this step. If found, bump to 147.0.100.

- [ ] **Step 4: Commit**

```bash
git add Browsingway.Renderer/Browsingway.Renderer.csproj
git commit -m "deps: bump CefSharp from 143.0.90 to 147.0.100"
```

---

### Task 2: Enable AcceleratedPaint in CefSettings

**Files:**
- Modify: `Browsingway.Renderer/CefHandler.cs`

- [ ] **Step 1: Add EnableAcceleratedPaint setting**

Add this line after `settings.EnableAudio();` (currently line 31):

```csharp
settings.EnableAcceleratedPaint = AcceleratedPaintOptions.Enabled;
```

The file should look like this at lines 25-33:

```csharp
    public static void Initialise(string cefAssemblyPath, string cefCacheDir, int parentPid)
    {
        CefSettings settings = new()
        {
            BrowserSubprocessPath = Path.Combine(cefAssemblyPath, "CefSharp.BrowserSubprocess.exe"),
            RootCachePath = cefCacheDir,
#if !DEBUG
            LogSeverity = LogSeverity.Fatal,
#endif
        };
        RootCachePath = settings.RootCachePath;
        settings.CefCommandLineArgs["autoplay-policy"] = "no-user-gesture-required";
        if (Environment.IsPrivilegedProcess)
        {
            Console.Error.WriteLine(
                "The game is running as a privileged process (e.g. as admin). This is a big security risk. It will also weaken CEF's security features. Please restart the game as a normal user.");
            settings.CefCommandLineArgs.Add("do-not-de-elevate");
        }

        settings.EnableAudio();
        settings.EnableAcceleratedPaint = AcceleratedPaintOptions.Enabled;
        settings.SetOffScreenRenderingBestPerformanceArgs();
        settings.UserAgentProduct = $"Chrome/{Cef.ChromiumVersion} Browsingway/{Assembly.GetEntryAssembly()?.GetName().Version} (ffxiv_pid {parentPid}; renderer_pid {Environment.ProcessId})";

        Cef.Initialize(settings, false, browserProcessHandler: null);
    }
```

Note: The exact property name may differ in CefSharp 147. If `AcceleratedPaintOptions` is not found, check intellisense for `CefSettings` members containing "AcceleratedPaint". It may also be `Cef.EnableAcceleratedPainting = true` or a similar static property. Adjust the property name as needed — the intent is to enable GPU-accelerated off-screen rendering.

- [ ] **Step 2: Commit**

```bash
git add Browsingway.Renderer/CefHandler.cs
git commit -m "feat: enable CEF EnableAcceleratedPaint for GPU rendering"
```

---

### Task 3: Rewrite TextureRenderHandler — fields and staging texture

**Files:**
- Modify: `Browsingway.Renderer/TextureRenderHandler.cs`

Replace all field declarations (lines 14-42) with the new field set:

- [ ] **Step 1: Replace fields**

Remove all lines from the class start through the constructor, and replace with:

```csharp
using Browsingway.Common.Ipc;
using CefSharp;
using CefSharp.Enums;
using CefSharp.OffScreen;
using CefSharp.Structs;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using System.Collections.Concurrent;
using Range = CefSharp.Structs.Range;
using Size = System.Drawing.Size;

namespace Browsingway.Renderer;

internal unsafe class TextureRenderHandler : IRenderHandler
{
    private const byte _bytesPerPixel = 4;

    private readonly object _renderLock = new();

    private Cursor _cursor;
    private bool _cursorOnBackground;

    private ConcurrentBag<IntPtr> _obsoleteTextures = [];

    // CEF-provided GPU textures (opened via OpenSharedResource, held by reference)
    private ID3D11Texture2D* _cefViewTexture;
    private IntPtr _cefViewHandle = IntPtr.Zero;
    private ID3D11Texture2D* _cefPopupTexture;
    private IntPtr _cefPopupHandle = IntPtr.Zero;

    // Our composited shared texture (plugin reads this)
    private ID3D11Texture2D* _sharedTexture;
    private IntPtr _sharedTextureHandle = IntPtr.Zero;
    private int _texWidth;
    private int _texHeight;

    // Staging texture for on-demand alpha detection (1x1 px, CPU-readable)
    private ID3D11Texture2D* _stagingTexture;

    // Popup state
    private Rect _popupRect;
    private bool _popupVisible;

    public TextureRenderHandler(Size size)
    {
        _sharedTexture = BuildSharedTexture(size);
        _stagingTexture = BuildStagingTexture();

        D3D11_TEXTURE2D_DESC desc;
        _sharedTexture->GetDesc(&desc);
        _texWidth = (int)desc.Width;
        _texHeight = (int)desc.Height;
    }
```

Note: Import `using static TerraFX.Interop.DirectX.DirectX;` is not added here since it's in `DxHandler.cs`. If needed for `D3D11CreateDevice` etc., it's already available in the project.

- [ ] **Step 2: Commit**

```bash
git add Browsingway.Renderer/TextureRenderHandler.cs
git commit -m "refactor: replace TextureRenderHandler fields for OnAcceleratedPaint"
```

---

### Task 4: Add helper methods — BuildSharedTexture, BuildStagingTexture, OpenCefTexture, ReleaseCefTexture

**Files:**
- Modify: `Browsingway.Renderer/TextureRenderHandler.cs`

Append the following private methods after the constructor (before `SharedTextureHandle` property):

- [ ] **Step 1: Add BuildSharedTexture**

```csharp
    private static ID3D11Texture2D* BuildSharedTexture(Size size)
    {
        D3D11_TEXTURE2D_DESC desc = new()
        {
            Width = (uint)size.Width,
            Height = (uint)size.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
            CPUAccessFlags = 0,
            MiscFlags = (uint)D3D11_RESOURCE_MISC_FLAG.D3D11_RESOURCE_MISC_SHARED
        };

        ID3D11Texture2D* texture;
        HRESULT hr = DxHandler.Device->CreateTexture2D(&desc, null, &texture);
        if (hr.FAILED)
        {
            throw new Exception($"Failed to create shared texture: {hr}");
        }

        return texture;
    }
```

- [ ] **Step 2: Add BuildStagingTexture**

```csharp
    private static ID3D11Texture2D* BuildStagingTexture()
    {
        D3D11_TEXTURE2D_DESC desc = new()
        {
            Width = 1,
            Height = 1,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
            Usage = D3D11_USAGE.D3D11_USAGE_STAGING,
            BindFlags = 0,
            CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ,
            MiscFlags = 0
        };

        ID3D11Texture2D* texture;
        HRESULT hr = DxHandler.Device->CreateTexture2D(&desc, null, &texture);
        if (hr.FAILED)
        {
            throw new Exception($"Failed to create staging texture: {hr}");
        }

        return texture;
    }
```

- [ ] **Step 3: Add OpenCefTexture helper**

```csharp
    private static void OpenCefTexture(IntPtr sharedHandle, out ID3D11Texture2D* texture)
    {
        ID3D11Device* device = DxHandler.Device;
        Guid texture2DGuid = typeof(ID3D11Texture2D).GUID;
        void* texturePtr;
        HRESULT hr = device->OpenSharedResource((HANDLE)sharedHandle, &texture2DGuid, &texturePtr);
        if (hr.FAILED)
        {
            throw new Exception($"Failed to open CEF shared texture: {hr}");
        }

        texture = (ID3D11Texture2D*)texturePtr;
    }
```

- [ ] **Step 4: Add ReleaseCefTexture helper**

```csharp
    private static void ReleaseCefTexture(ref ID3D11Texture2D* texture)
    {
        if (texture != null)
        {
            texture->Release();
            texture = null;
        }
    }
```

- [ ] **Step 5: Commit**

```bash
git add Browsingway.Renderer/TextureRenderHandler.cs
git commit -m "feat: add BuildSharedTexture, BuildStagingTexture, and CEF texture helpers"
```

---

### Task 5: Implement OnAcceleratedPaint

**Files:**
- Modify: `Browsingway.Renderer/TextureRenderHandler.cs`

- [ ] **Step 1: Replace the stubbed OnAcceleratedPaint (lines 102-106) with full implementation**

Find the existing stubbed method:

```csharp
    public void OnAcceleratedPaint(PaintElementType type, Rect dirtyRect, AcceleratedPaintInfo acceleratedPaintInfo)
    {
        // TODO: use this instead of manual texture copying
        throw new NotImplementedException();
    }
```

Replace with:

```csharp
    public void OnAcceleratedPaint(PaintElementType type, Rect dirtyRect, AcceleratedPaintInfo acceleratedPaintInfo)
    {
        lock (_renderLock)
        {
            IntPtr cefHandle = acceleratedPaintInfo.SharedTextureHandle;

            if (type == PaintElementType.View)
            {
                if (_cefViewHandle != cefHandle)
                {
                    ReleaseCefTexture(ref _cefViewTexture);
                    OpenCefTexture(cefHandle, out _cefViewTexture);
                    _cefViewHandle = cefHandle;
                }

                ID3D11DeviceContext* context;
                DxHandler.Device->GetImmediateContext(&context);
                context->CopySubresourceRegion(
                    (ID3D11Resource*)_sharedTexture, 0, 0, 0, 0,
                    (ID3D11Resource*)_cefViewTexture, 0, null);
                context->Release();
            }
            else if (type == PaintElementType.Popup)
            {
                if (_cefPopupHandle != cefHandle)
                {
                    ReleaseCefTexture(ref _cefPopupTexture);
                    OpenCefTexture(cefHandle, out _cefPopupTexture);
                    _cefPopupHandle = cefHandle;
                }

                if (_popupVisible && _cefPopupTexture != null)
                {
                    Point popupPos = DpiScaling.ScaleScreenPoint(_popupRect.X, _popupRect.Y);
                    ID3D11DeviceContext* context;
                    DxHandler.Device->GetImmediateContext(&context);
                    context->CopySubresourceRegion(
                        (ID3D11Resource*)_sharedTexture, 0,
                        (uint)popupPos.X, (uint)popupPos.Y, 0,
                        (ID3D11Resource*)_cefPopupTexture, 0, null);
                    context->Release();
                }
            }

            // Clean up any obsolete textures
            ConcurrentBag<IntPtr> textures = _obsoleteTextures;
            _obsoleteTextures = new ConcurrentBag<IntPtr>();
            foreach (IntPtr texPtr in textures)
            {
                ((ID3D11Texture2D*)texPtr)->Release();
            }
        }
    }
```

- [ ] **Step 2: Remove the old OnPaint method entirely**

Delete the entire `OnPaint` method (everything from `public void OnPaint(...` through the closing `}` at line 212).

- [ ] **Step 3: Commit**

```bash
git add Browsingway.Renderer/TextureRenderHandler.cs
git commit -m "feat: implement OnAcceleratedPaint, remove OnPaint CPU path"
```

---

### Task 6: Rewrite GetAlphaAt with staging texture

**Files:**
- Modify: `Browsingway.Renderer/TextureRenderHandler.cs`

- [ ] **Step 1: Replace GetAlphaAt implementation (lines 301-322)**

Replace the entire existing `GetAlphaAt` method:

```csharp
    protected byte GetAlphaAt(int x, int y)
    {
        lock (_renderLock)
        {
            int rowPitch = _alphaLookupBufferWidth * _bytesPerPixel;
            int cursorAlphaOffset = 0
                                    + (Math.Min(Math.Max(x, 0), _alphaLookupBufferWidth - 1) * _bytesPerPixel)
                                    + (Math.Min(Math.Max(y, 0), _alphaLookupBufferHeight - 1) * rowPitch)
                                    + 3;
            cursorAlphaOffset = cursorAlphaOffset < 0 ? 0 : cursorAlphaOffset;

            if (cursorAlphaOffset < _alphaLookupBuffer.Length)
            {
                return _alphaLookupBuffer[cursorAlphaOffset];
            }

            Console.WriteLine("Could not determine alpha value");
            return 255;
        }
    }
```

With:

```csharp
    protected byte GetAlphaAt(int x, int y)
    {
        int clampX = Math.Clamp(x, 0, _texWidth - 1);
        int clampY = Math.Clamp(y, 0, _texHeight - 1);

        D3D11_BOX srcBox = new()
        {
            left = (uint)clampX,
            right = (uint)(clampX + 1),
            top = (uint)clampY,
            bottom = (uint)(clampY + 1),
            front = 0,
            back = 1
        };

        ID3D11DeviceContext* context;
        DxHandler.Device->GetImmediateContext(&context);

        // Copy 1 pixel from _sharedTexture to staging texture
        context->CopySubresourceRegion(
            (ID3D11Resource*)_stagingTexture, 0, 0, 0, 0,
            (ID3D11Resource*)_sharedTexture, 0, &srcBox);

        // Map staging texture to read the pixel
        D3D11_MAPPED_SUBRESOURCE mapped;
        HRESULT hr = context->Map((ID3D11Resource*)_stagingTexture, 0,
            D3D11_MAP.D3D11_MAP_READ, 0, &mapped);

        byte alpha = 255;
        if (hr.SUCCEEDED)
        {
            alpha = ((byte*)mapped.pData)[3]; // BGRA: offset 3 = alpha
            context->Unmap((ID3D11Resource*)_stagingTexture, 0);
        }
        else
        {
            Console.WriteLine($"Could not map staging texture for alpha read: {hr}");
        }

        context->Release();
        return alpha;
    }
```

- [ ] **Step 2: Commit**

```bash
git add Browsingway.Renderer/TextureRenderHandler.cs
git commit -m "feat: replace CPU alpha buffer with staging texture 1px readback"
```

---

### Task 7: Update Resize, OnPopupSize, and Dispose for new texture references

**Files:**
- Modify: `Browsingway.Renderer/TextureRenderHandler.cs`

- [ ] **Step 1: Replace Resize method (lines 283-299)**

Replace:

```csharp
    public void Resize(Size size)
    {
        lock (_renderLock)
        {
            // TODO: make this thread unsafe crap thread safe crap
            ID3D11Texture2D* oldTexture1 = _sharedTexture;
            ID3D11Texture2D* oldTexture2 = _viewTexture;
            _sharedTexture = BuildViewTexture(size, true);
            _viewTexture = BuildViewTexture(size, false);
            _obsoleteTextures.Add((IntPtr)oldTexture1);
            _obsoleteTextures.Add((IntPtr)oldTexture2);

            // Need to clear the cached handle value
            // TODO: Maybe I should just avoid the lazy cache and do it eagerly on _sharedTexture build.
            _sharedTextureHandle = IntPtr.Zero;
        }
    }
```

With:

```csharp
    public void Resize(Size size)
    {
        lock (_renderLock)
        {
            ID3D11Texture2D* oldTexture = _sharedTexture;
            _sharedTexture = BuildSharedTexture(size);
            _obsoleteTextures.Add((IntPtr)oldTexture);

            D3D11_TEXTURE2D_DESC desc;
            _sharedTexture->GetDesc(&desc);
            _texWidth = (int)desc.Width;
            _texHeight = (int)desc.Height;

            ReleaseCefTexture(ref _cefViewTexture);
            _cefViewHandle = IntPtr.Zero;
            ReleaseCefTexture(ref _cefPopupTexture);
            _cefPopupHandle = IntPtr.Zero;

            _sharedTextureHandle = IntPtr.Zero;
        }
    }
```

- [ ] **Step 2: Replace OnPopupSize (lines 219-242)**

Replace the method that creates `_popupTexture` with one that releases `_cefPopupTexture`:

```csharp
    public void OnPopupSize(Rect rect)
    {
        _popupRect = DpiScaling.ScaleScreenRect(rect);

        D3D11_TEXTURE2D_DESC texDesc;
        _sharedTexture->GetDesc(&texDesc);
        if (_popupRect.Width > texDesc.Width || _popupRect.Height > texDesc.Height)
        {
            Console.Error.WriteLine(
                $"Trying to build popup layer ({_popupRect.Width}x{_popupRect.Height}) larger than primary surface ({texDesc.Width}x{texDesc.Height}).");
        }

        ReleaseCefTexture(ref _cefPopupTexture);
        _cefPopupHandle = IntPtr.Zero;
    }
```

- [ ] **Step 3: Replace Dispose method (lines 72-85)**

Replace:

```csharp
    public void Dispose()
    {
        _sharedTexture->Release();
        _viewTexture->Release();
        if (_popupTexture != null)
        {
            _popupTexture->Release();
        }

        foreach (IntPtr texturePtr in _obsoleteTextures)
        {
            ((ID3D11Texture2D*)texturePtr)->Release();
        }
    }
```

With:

```csharp
    public void Dispose()
    {
        ReleaseCefTexture(ref _cefViewTexture);
        ReleaseCefTexture(ref _cefPopupTexture);

        if (_stagingTexture != null)
        {
            _stagingTexture->Release();
            _stagingTexture = null;
        }

        if (_sharedTexture != null)
        {
            _sharedTexture->Release();
            _sharedTexture = null;
        }

        foreach (IntPtr texturePtr in _obsoleteTextures)
        {
            ((ID3D11Texture2D*)texturePtr)->Release();
        }
    }
```

- [ ] **Step 4: Commit**

```bash
git add Browsingway.Renderer/TextureRenderHandler.cs
git commit -m "refactor: update Resize, OnPopupSize, Dispose for CEF-owned textures"
```

---

### Task 8: Remove dead code — old OnPaint artifacts

**Files:**
- Modify: `Browsingway.Renderer/TextureRenderHandler.cs`

- [ ] **Step 1: Verify no remaining references to removed fields**

Search the file for these removed identifiers and delete any remaining usage:
- `_alphaLookupBuffer` — should be gone (replaced by staging texture)
- `_alphaLookupBufferHeight` / `_alphaLookupBufferWidth` — should be gone
- `_viewTexture` — should be gone (replaced by `_cefViewTexture`)
- `_popupTexture` — should be gone (replaced by `_cefPopupTexture`)
- `BuildViewTexture` — should be gone (replaced by `BuildSharedTexture`)

Run: `rg "_alphaLookupBuffer|_viewTexture[^H]|_popupTexture[^H]|BuildViewTexture" Browsingway.Renderer/TextureRenderHandler.cs`
Expected: No output (no remaining references)

- [ ] **Step 2: Remove unused `using Range = CefSharp.Structs.Range;` if no longer needed**

Check if `Range` is still used. If only `OnImeCompositionRangeChanged` uses it, keep it. Otherwise remove.

- [ ] **Step 3: Remove unused import if any**

Check if `System.Collections.Concurrent` is still used (for `ConcurrentBag` in obsolete texture cleanup). It is — keep it.

- [ ] **Step 4: Commit**

```bash
git add Browsingway.Renderer/TextureRenderHandler.cs
git commit -m "chore: remove dead code from old OnPaint path"
```

---

### Task 9: Build and verify no compilation errors

**Files:**
- None (verification only)

- [ ] **Step 1: Build the solution**

Run: `dotnet build Browsingway.sln 2>&1`
Expected: Build succeeded with 0 errors

- [ ] **Step 2: Check for any CefSharp API break warnings**

If the build fails due to missing IRenderHandler interface members (new in CefSharp 147), add the required stubs. For example, if a new method `OnSomething` is required:

```csharp
public void OnSomething(int param)
{
    // Not used in offscreen rendering mode
}
```

- [ ] **Step 3: Verify the file has no compiler warnings about unused variables**

Review build output for warnings in `TextureRenderHandler.cs`. Fix any that appear.

- [ ] **Step 4: Commit**

```bash
git add -A  # only if there are build-triggered auto-changes (e.g., lock files)
git commit -m "build: verify solution compiles after OnAcceleratedPaint migration"
```

---

### Task 10: Final review — confirm spec coverage

- [ ] **Step 1: Verify each spec requirement is met**

Checklist:
- [x] `_alphaLookupBuffer` removed — Task 3 removes fields, Task 8 cleans up
- [x] `_viewTexture` / `_popupTexture` removed — Task 3
- [x] `OnAcceleratedPaint` implemented — Task 5
- [x] `OnPaint` removed — Task 5
- [x] `UpdateSubresource` calls removed — Task 5 (no more CPU→GPU upload)
- [x] `Flush()` removed — Task 5 (not needed with GPU-only ops)
- [x] Staging texture for alpha detection — Task 4 + Task 6
- [x] Popup compositing via CopySubresourceRegion — Task 5
- [x] Resize handles CEF texture release — Task 7
- [x] `BuildViewTexture` replaced with `BuildSharedTexture` — Task 3
- [x] `EnableAcceleratedPaint` enabled — Task 2
- [x] CefSharp 143 → 147 — Task 1
- [x] Plugin-side files unchanged — verified by plan scope

- [ ] **Step 2: Commit the plan itself**

```bash
git add docs/superpowers/plans/2026-05-13-accelerated-paint.md
git commit -m "docs: add OnAcceleratedPaint implementation plan"
```
