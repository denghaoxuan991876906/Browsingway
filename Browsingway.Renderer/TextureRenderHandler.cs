using Browsingway.Common.Ipc;
using CefSharp;
using CefSharp.Enums;
using CefSharp.OffScreen;
using CefSharp.Structs;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using Range = CefSharp.Structs.Range;
using Size = System.Drawing.Size;

namespace Browsingway.Renderer;

internal unsafe class TextureRenderHandler : IRenderHandler
{
	private const byte _bytesPerPixel = 4;

	private readonly object _lock = new();

	private byte[] _alphaLookupBuffer = Array.Empty<byte>();
	private int _alphaLookupBufferHeight;
	private int _alphaLookupBufferWidth;

	private Cursor _cursor;
	private bool _cursorOnBackground;

	private Rect _popupRect;
	private ID3D11Texture2D* _popupTexture;
	private bool _popupVisible;
	private ID3D11Texture2D* _sharedTexture;

	private IntPtr _sharedTextureHandle = IntPtr.Zero;
	private ID3D11Texture2D* _viewTexture;

	public TextureRenderHandler(Size size)
	{
		_sharedTexture = BuildViewTexture(size, true);
		_viewTexture = BuildViewTexture(size, false);
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
		_sharedTexture->Release();
		_viewTexture->Release();
		if (_popupTexture != null)
		{
			_popupTexture->Release();
		}
	}

	public Rect GetViewRect()
	{
		D3D11_TEXTURE2D_DESC texDesc;
		_sharedTexture->GetDesc(&texDesc);
		return DpiScaling.ScaleViewRect(new Rect(0, 0, (int)texDesc.Width, (int)texDesc.Height));
	}

	public void OnAcceleratedPaint(PaintElementType type, Rect dirtyRect, AcceleratedPaintInfo info)
	{
		lock (_lock)
		{
		ID3D11DeviceContext* context;
		DxHandler.Device->GetImmediateContext(&context);

		// 打开 CEF 提供的 GPU 纹理（handle 直达，无 CPU 拷贝）
		ID3D11Texture2D* cefTexture;
		Guid texture2DGuid = typeof(ID3D11Texture2D).GUID;
		void* texPtr;
		HRESULT hr = DxHandler.Device->OpenSharedResource((HANDLE)info.SharedTextureHandle, &texture2DGuid, &texPtr);
		if (hr.FAILED) { context->Release(); return; }
		cefTexture = (ID3D11Texture2D*)texPtr;

		// GPU→GPU 拷贝：CEF 纹理 → 内部纹理
		ID3D11Texture2D* target = type == PaintElementType.View ? _viewTexture : _popupTexture;
		if (target != null)
			context->CopySubresourceRegion((ID3D11Resource*)target, 0, 0, 0, 0, (ID3D11Resource*)cefTexture, 0, null);
		cefTexture->Release();

		// 合成到 _sharedTexture
		context->CopySubresourceRegion((ID3D11Resource*)_sharedTexture, 0, 0, 0, 0, (ID3D11Resource*)_viewTexture, 0, null);
		if (_popupVisible && _popupTexture != null)
		{
			Point popupPos = DpiScaling.ScaleScreenPoint(_popupRect.X, _popupRect.Y);
			context->CopySubresourceRegion((ID3D11Resource*)_sharedTexture, 0,
				(uint)popupPos.X, (uint)popupPos.Y, 0, (ID3D11Resource*)_popupTexture, 0, null);
		}

		context->Release();
		// 不需要 Flush — GPU→GPU 拷贝由驱动管线管理
		}
	}

	public void OnPaint(PaintElementType type, Rect dirtyRect, IntPtr buffer, int width, int height)
	{
		lock (_lock)
		{
		ID3D11Texture2D* targetTexture = type switch
		{
			PaintElementType.View => _viewTexture,
			PaintElementType.Popup => _popupTexture,
			_ => throw new Exception($"Unknown paint type {type}")
		};

		if (targetTexture == null) return;

		// 保留 alpha 缓冲区用于穿透检测
		if (type == PaintElementType.View)
		{
			int requiredBufferSize = width * height * _bytesPerPixel;
			_alphaLookupBufferWidth = width;
			_alphaLookupBufferHeight = height;
			if (_alphaLookupBuffer.Length < requiredBufferSize)
				_alphaLookupBuffer = new byte[requiredBufferSize];

			fixed (void* dstBuffer = _alphaLookupBuffer)
			{
				Buffer.MemoryCopy(buffer.ToPointer(), dstBuffer, _alphaLookupBuffer.Length, requiredBufferSize);
			}
		}

		int rowPitch = width * _bytesPerPixel;
		int depthPitch = rowPitch * height;

		D3D11_TEXTURE2D_DESC texDesc;
		targetTexture->GetDesc(&texDesc);

		IntPtr sourceRegionPtr = buffer + (dirtyRect.X * _bytesPerPixel) + (dirtyRect.Y * rowPitch);
		D3D11_BOX destinationBox = new()
		{
			top = (uint)Math.Min(dirtyRect.Y, (int)texDesc.Height),
			bottom = (uint)Math.Min(dirtyRect.Y + dirtyRect.Height, (int)texDesc.Height),
			left = (uint)Math.Min(dirtyRect.X, (int)texDesc.Width),
			right = (uint)Math.Min(dirtyRect.X + dirtyRect.Width, (int)texDesc.Width),
			front = 0,
			back = 1
		};

		ID3D11DeviceContext* context;
		DxHandler.Device->GetImmediateContext(&context);

		context->UpdateSubresource((ID3D11Resource*)targetTexture, 0, &destinationBox, sourceRegionPtr.ToPointer(), (uint)rowPitch, (uint)depthPitch);

		context->CopySubresourceRegion((ID3D11Resource*)_sharedTexture, 0, 0, 0, 0, (ID3D11Resource*)_viewTexture, 0, null);
		if (_popupVisible && _popupTexture != null)
		{
			Point popupPos = DpiScaling.ScaleScreenPoint(_popupRect.X, _popupRect.Y);
			context->CopySubresourceRegion((ID3D11Resource*)_sharedTexture, 0, (uint)popupPos.X, (uint)popupPos.Y, 0, (ID3D11Resource*)_popupTexture, 0, null);
		}

		context->Flush();
		context->Release();
		}
	}

	public void OnPopupShow(bool show)
	{
		_popupVisible = show;
	}

	public void OnPopupSize(Rect rect)
	{
		_popupRect = DpiScaling.ScaleScreenRect(rect);

		// I'm really not sure if this happens. If it does, frequently - will probably need 2x shared textures and some jazz.
		D3D11_TEXTURE2D_DESC texDesc;
		_sharedTexture->GetDesc(&texDesc);
		if (_popupRect.Width > texDesc.Width || _popupRect.Height > texDesc.Height)
		{
			Console.Error.WriteLine(
				$"Trying to build popup layer ({_popupRect.Width}x{_popupRect.Height}) larger than primary surface ({texDesc.Width}x{texDesc.Height}).");
		}

		// Get a reference to the old _sharedTexture, we'll make sure to assign a new _sharedTexture before disposing the old one.
		ID3D11Texture2D* oldTexture = _popupTexture;

		// Build a _sharedTexture for the new sized popup
		_popupTexture = BuildViewTexture(new Size(_popupRect.Width, _popupRect.Height), false);

		if (oldTexture != null)
		{
			oldTexture->Release();
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
		if (!_cursorOnBackground) { CursorChanged?.Invoke(this, _cursor); }
	}

	public bool StartDragging(IDragData dragData, DragOperationsMask mask, int x, int y)
	{
		return false;
	}

	public void UpdateDragCursor(DragOperationsMask operation)
	{
	}

	public void Resize(Size size)
	{
		lock (_lock)
		{
		ID3D11Texture2D* oldTexture1 = _sharedTexture;
		ID3D11Texture2D* oldTexture2 = _viewTexture;
		_sharedTexture = BuildViewTexture(size, true);
		_viewTexture = BuildViewTexture(size, false);
		oldTexture1->Release();
		oldTexture2->Release();
		_sharedTextureHandle = IntPtr.Zero;
		}
	}

	public void SetMousePosition(int x, int y)
	{
		byte alpha = GetAlphaAt(x, y);

		bool currentlyOnBackground = alpha == 0;
		if (currentlyOnBackground != _cursorOnBackground)
		{
			_cursorOnBackground = currentlyOnBackground;
			CursorChanged?.Invoke(this, currentlyOnBackground ? Cursor.BrowsingwayNoCapture : _cursor);
		}
	}

	private byte GetAlphaAt(int x, int y)
	{
		lock (_lock)
		{
			int rowPitch = _alphaLookupBufferWidth * _bytesPerPixel;
			int cursorAlphaOffset = 0
				+ (Math.Min(Math.Max(x, 0), _alphaLookupBufferWidth - 1) * _bytesPerPixel)
				+ (Math.Min(Math.Max(y, 0), _alphaLookupBufferHeight - 1) * rowPitch)
				+ 3;
			cursorAlphaOffset = cursorAlphaOffset < 0 ? 0 : cursorAlphaOffset;

			if (cursorAlphaOffset < _alphaLookupBuffer.Length)
				return _alphaLookupBuffer[cursorAlphaOffset];

			return 255;
		}
	}

	private ID3D11Texture2D* BuildViewTexture(Size size, bool isShared)
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
			MiscFlags = isShared ? (uint)D3D11_RESOURCE_MISC_FLAG.D3D11_RESOURCE_MISC_SHARED : 0
		};

		ID3D11Texture2D* texture;
		HRESULT hr = DxHandler.Device->CreateTexture2D(&desc, null, &texture);
		if (hr.FAILED)
		{
			throw new Exception($"Failed to create texture: {hr}");
		}

		return texture;
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
