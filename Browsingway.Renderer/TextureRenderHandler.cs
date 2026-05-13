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

	private static void OpenCefTexture(IntPtr sharedHandle, out ID3D11Texture2D* texture)
	{
		// CEF passes a pointer to the HANDLE, not the HANDLE itself
		void* handlePtr = sharedHandle.ToPointer();
		HANDLE actualHandle = *(HANDLE*)handlePtr;

		ID3D11Device* device = DxHandler.Device;
		Guid texture2DGuid = typeof(ID3D11Texture2D).GUID;
		void* texturePtr;
		HRESULT hr = device->OpenSharedResource(actualHandle, &texture2DGuid, &texturePtr);
		if (hr.FAILED)
		{
			throw new Exception($"Failed to open CEF shared texture: {hr}");
		}

		texture = (ID3D11Texture2D*)texturePtr;
	}

	private static void ReleaseCefTexture(ref ID3D11Texture2D* texture)
	{
		if (texture != null)
		{
			texture->Release();
			texture = null;
		}
	}

	public IntPtr SharedTextureHandle
	{
		get
		{
			if (_sharedTextureHandle == IntPtr.Zero)
			{
				IDXGIResource* resource;
				Guid resourceGuid = typeof(IDXGIResource).GUID;
				HRESULT hr = ((IUnknown*)_sharedTexture)->QueryInterface(&resourceGuid, (void**)&resource);
				if (hr.SUCCEEDED)
				{
					HANDLE sharedHandle;
					resource->GetSharedHandle(&sharedHandle);
					_sharedTextureHandle = (IntPtr)sharedHandle.Value;
					resource->Release();
				}
			}

			return _sharedTextureHandle;
		}
	}

	public event EventHandler<Cursor>? CursorChanged;

	public void Dispose()
	{
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

	public Rect GetViewRect()
	{
		// There's a very small chance that OnPaint's cleanup will delete the current _sharedTexture midway through this function -
		// Try a few times just in case before failing out with an obviously-wrong value
		// hi adam
		// TODO: proper threading model instead of shitty hacks
		for (int i = 0; i < 5; i++)
		{
			try { return GetViewRectInternal(); }
			catch (NullReferenceException) { }
		}

		return new Rect(0, 0, 1, 1);
	}

	public void OnPaint(PaintElementType type, Rect dirtyRect, nint buffer, int width, int height)
	{
	}

	public void OnAcceleratedPaint(PaintElementType type, Rect dirtyRect, AcceleratedPaintInfo acceleratedPaintInfo)
	{
		IntPtr cefHandle = acceleratedPaintInfo.SharedTextureHandle;
		if (cefHandle == IntPtr.Zero) return;

		lock (_renderLock)
		{
			ID3D11Texture2D* cefTexture = null;
			try
			{
				OpenCefTexture(cefHandle, out cefTexture);

				ID3D11DeviceContext* context;
				DxHandler.Device->GetImmediateContext(&context);

				if (type == PaintElementType.View)
				{
					context->CopySubresourceRegion(
						(ID3D11Resource*)_sharedTexture, 0, 0, 0, 0,
						(ID3D11Resource*)cefTexture, 0, null);
				}
				else if (type == PaintElementType.Popup && _popupVisible)
				{
					Point popupPos = DpiScaling.ScaleScreenPoint(_popupRect.X, _popupRect.Y);
					context->CopySubresourceRegion(
						(ID3D11Resource*)_sharedTexture, 0,
						(uint)popupPos.X, (uint)popupPos.Y, 0,
						(ID3D11Resource*)cefTexture, 0, null);
				}

				context->Release();
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine($"OnAcceleratedPaint({type}) failed: {ex.Message} (handle=0x{cefHandle:X})");
			}
			finally
			{
				if (cefTexture != null) cefTexture->Release();
			}

			ConcurrentBag<IntPtr> textures = _obsoleteTextures;
			_obsoleteTextures = new ConcurrentBag<IntPtr>();
			foreach (IntPtr texPtr in textures)
			{
				((ID3D11Texture2D*)texPtr)->Release();
			}
		}
	}


	public void OnPopupShow(bool show)
	{
		_popupVisible = show;
	}

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
	}

	public ScreenInfo? GetScreenInfo()
	{
		return new ScreenInfo {DeviceScaleFactor = DpiScaling.GetDeviceScale()};
	}

	public bool GetScreenPoint(int viewX, int viewY, out int screenX, out int screenY)
	{
		screenX = viewX;
		screenY = viewY;

		return false;
	}

	public void OnVirtualKeyboardRequested(IBrowser browser, TextInputMode inputMode)
	{
	}

	public void OnImeCompositionRangeChanged(Range selectedRange, Rect[] characterBounds)
	{
	}

	public void OnCursorChange(IntPtr cursorPtr, CursorType type, CursorInfo customCursorInfo)
	{
		_cursor = EncodeCursor(type);

		// If we're on background, don't flag a cursor change
		if (!_cursorOnBackground) { CursorChanged?.Invoke(this, _cursor); }
	}

	public bool StartDragging(IDragData dragData, DragOperationsMask mask, int x, int y)
	{
		// Returning false to abort drag operations.
		return false;
	}

	public void UpdateDragCursor(DragOperationsMask operation)
	{
	}

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
			_sharedTextureHandle = IntPtr.Zero;
		}
	}

	protected byte GetAlphaAt(int x, int y)
	{
		lock (_renderLock)
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

			context->CopySubresourceRegion(
				(ID3D11Resource*)_stagingTexture, 0, 0, 0, 0,
				(ID3D11Resource*)_sharedTexture, 0, &srcBox);

			// Note: CopySubresourceRegion returns void in D3D11 — errors surface
			// through device state (ID3D11Device::GetDeviceRemovedReason), not HRESULT.

			D3D11_MAPPED_SUBRESOURCE mapped;
			HRESULT hr = context->Map((ID3D11Resource*)_stagingTexture, 0,
				D3D11_MAP.D3D11_MAP_READ, 0, &mapped);

			byte alpha = 255;
			if (hr.SUCCEEDED)
			{
				alpha = ((byte*)mapped.pData)[3];
				context->Unmap((ID3D11Resource*)_stagingTexture, 0);
			}
			else
			{
				Console.WriteLine($"Could not map staging texture for alpha read: {hr}");
			}

			context->Release();
			return alpha;
		}
	}

	private Rect GetViewRectInternal()
	{
		D3D11_TEXTURE2D_DESC texDesc;
		_sharedTexture->GetDesc(&texDesc);
		return DpiScaling.ScaleViewRect(new Rect(0, 0, (int)texDesc.Width, (int)texDesc.Height));
	}

	public void SetMousePosition(int x, int y)
	{
		byte alpha = GetAlphaAt(x, y);

		// We treat 0 alpha as click through - if changed, fire off the event
		bool currentlyOnBackground = alpha == 0;
		if (currentlyOnBackground != _cursorOnBackground)
		{
			_cursorOnBackground = currentlyOnBackground;

			// EDGE CASE: if cursor transitions onto alpha:0 _and_ between two native cursor types, I guess this will be a race cond.
			// Not sure if should have two separate upstreams for them, or try and prevent the race. consider.
			CursorChanged?.Invoke(this, currentlyOnBackground ? Cursor.BrowsingwayNoCapture : _cursor);
		}
	}

	private Cursor EncodeCursor(CursorType cursor)
	{
		switch (cursor)
		{
			// CEF calls default "pointer", and pointer "hand".
			case CursorType.Pointer: return Cursor.Default;
			case CursorType.Cross: return Cursor.Crosshair;
			case CursorType.Hand: return Cursor.Pointer;
			case CursorType.IBeam: return Cursor.Text;
			case CursorType.Wait: return Cursor.Wait;
			case CursorType.Help: return Cursor.Help;
			case CursorType.EastResize: return Cursor.EResize;
			case CursorType.NorthResize: return Cursor.NResize;
			case CursorType.NortheastResize: return Cursor.NeResize;
			case CursorType.NorthwestResize: return Cursor.NwResize;
			case CursorType.SouthResize: return Cursor.SResize;
			case CursorType.SoutheastResize: return Cursor.SeResize;
			case CursorType.SouthwestResize: return Cursor.SwResize;
			case CursorType.WestResize: return Cursor.WResize;
			case CursorType.NorthSouthResize: return Cursor.NsResize;
			case CursorType.EastWestResize: return Cursor.EwResize;
			case CursorType.NortheastSouthwestResize: return Cursor.NeswResize;
			case CursorType.NorthwestSoutheastResize: return Cursor.NwseResize;
			case CursorType.ColumnResize: return Cursor.ColResize;
			case CursorType.RowResize: return Cursor.RowResize;

			// There isn't really support for panning right now. Default to all-scroll.
			case CursorType.MiddlePanning:
			case CursorType.EastPanning:
			case CursorType.NorthPanning:
			case CursorType.NortheastPanning:
			case CursorType.NorthwestPanning:
			case CursorType.SouthPanning:
			case CursorType.SoutheastPanning:
			case CursorType.SouthwestPanning:
			case CursorType.WestPanning:
				return Cursor.AllScroll;

			case CursorType.Move: return Cursor.Move;
			case CursorType.VerticalText: return Cursor.VerticalText;
			case CursorType.Cell: return Cursor.Cell;
			case CursorType.ContextMenu: return Cursor.ContextMenu;
			case CursorType.Alias: return Cursor.Alias;
			case CursorType.Progress: return Cursor.Progress;
			case CursorType.NoDrop: return Cursor.NoDrop;
			case CursorType.Copy: return Cursor.Copy;
			case CursorType.None: return Cursor.None;
			case CursorType.NotAllowed: return Cursor.NotAllowed;
			case CursorType.ZoomIn: return Cursor.ZoomIn;
			case CursorType.ZoomOut: return Cursor.ZoomOut;
			case CursorType.Grab: return Cursor.Grab;
			case CursorType.Grabbing: return Cursor.Grabbing;

			// Not handling custom for now
			case CursorType.Custom: return Cursor.Default;
		}

		// Unmapped cursor, log and default
		Console.WriteLine($"Switching to unmapped cursor type {cursor}.");
		return Cursor.Default;
	}
}
