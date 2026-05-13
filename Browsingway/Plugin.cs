using Browsingway.Common;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;
using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Text.Json;

namespace Browsingway;

public class Plugin : IDalamudPlugin
{
	private const string _command = "/bw";

	private readonly DependencyManager _dependencyManager;
	private readonly Dictionary<Guid, Overlay> _overlays = new();
	private readonly string _pluginConfigDir;
	private readonly string _pluginDir;

	private RenderProcess? _renderProcess;
	private ActHandler _actHandler;
	private Settings? _settings;
	private Services _services;

	public Plugin(IDalamudPluginInterface pluginInterface)
	{
		// init services
		_services = pluginInterface.Create<Services>()!;

		_pluginDir = pluginInterface.AssemblyLocation.DirectoryName ?? "";
		if (String.IsNullOrEmpty(_pluginDir))
		{
			throw new Exception("Could not determine plugin directory");
		}

		_pluginConfigDir = pluginInterface.GetPluginConfigDirectory();

		_actHandler = new ActHandler();

		_dependencyManager = new DependencyManager(_pluginDir, _pluginConfigDir);
		_dependencyManager.DependenciesReady += (_, _) => DependenciesReady();
		_dependencyManager.Initialise();

		// Hook up render hook
		pluginInterface.UiBuilder.Draw += Render;
	}

	// Required for LivePluginLoader support
	public string AssemblyLocation { get; } = Assembly.GetExecutingAssembly().Location;
	public string Name => "Browsingway";

	public void Dispose()
	{
		foreach (Overlay overlay in _overlays.Values) { overlay.Dispose(); }

		_overlays.Clear();

		_renderProcess?.Dispose();

		_settings?.Dispose();

		Services.CommandManager.RemoveHandler(_command);

		WndProcHandler.Shutdown();
		DxHandler.Shutdown();

		_dependencyManager.Dispose();
	}

	private void DependenciesReady()
	{
		// Spin up DX handling from the plugin interface
		DxHandler.Initialise(Services.PluginInterface);

		// Spin up WndProc hook
		WndProcHandler.Initialise(DxHandler.WindowHandle);
		WndProcHandler.WndProcMessage += OnWndProc;

		// Boot the render process. This has to be done before initialising settings to prevent a
		// race condition overlays receiving a null reference.
		int pid = Process.GetCurrentProcess().Id;
		_renderProcess = new RenderProcess(pid, _pluginDir, _pluginConfigDir, _dependencyManager, Services.PluginLog);
		_renderProcess.Rpc!.RendererReady += msg =>
		{
			if (!msg.HasDxSharedTexturesSupport)
			{
				Services.PluginLog.Error("Could not initialize shared textures transport. Browsingway will not work.");
				return;
			}

			Services.Framework.RunOnFrameworkThread(() =>
			{
				if (_settings is not null)
				{
					_settings.HydrateOverlays();
				}
				SyncFromHiAuRo();
			});
		};
		_renderProcess.Rpc.SetCursor += msg =>
		{
			Services.Framework.RunOnFrameworkThread(() =>
			{
				Guid guid = new(msg.Guid.Span);
				Overlay? overlay = _overlays.Values.FirstOrDefault(overlay => overlay.RenderGuid == guid);
				overlay?.SetCursor(msg.Cursor);
			});
		};
		_renderProcess.Rpc.UpdateTexture += msg =>
		{
			Services.Framework.RunOnFrameworkThread(() =>
			{
				Guid guid = new(msg.Guid.Span);
				if (_overlays.TryGetValue(guid, out Overlay? overlay))
				{
					overlay.SetTexture((IntPtr)msg.TextureHandle);
				}
				else
				{
					Services.PluginLog.Error("Overlay Id not found");
				}
			});
		};
		_renderProcess.Start();

		// Prep settings
		_settings = new Settings();
		if (_settings is not null)
		{
			_settings.OverlayAdded += OnOverlayAdded;
			_settings.OverlayNavigated += OnOverlayNavigated;
			_settings.OverlayDebugged += OnOverlayDebugged;
			_settings.OverlayRemoved += OnOverlayRemoved;
			_settings.OverlayZoomed += OnOverlayZoomed;
			_settings.OverlayMuted += OnOverlayMuted;
			_actHandler.AvailabilityChanged += OnActAvailabilityChanged;
			_settings.OverlayUserCssChanged += OnUserCssChanged;
			_settings.OverlaySettingsChanged += (_, config) =>
			{
				if (_overlays.TryGetValue(config.Guid, out var ov)) ov.Reload();
			};
		}

		RegisterIpc();

		SyncFromHiAuRo();

		// Hook up the main BW command
		Services.CommandManager.AddHandler(_command,
			new CommandInfo(HandleCommand) {HelpMessage = "Control Browsingway from the chat line! Type '/bw config' or open the settings for more info.", ShowInHelp = true});

		RegisterIpc();
	}

	private void SyncFromHiAuRo()
	{
		try
		{
			var ipc = Services.PluginInterface;
			var isWebUiSub = ipc.GetIpcSubscriber<bool>("HiAuRo.IsWebUIMode");
			bool isWebUi = isWebUiSub.InvokeFunc();
			if (!isWebUi) return;

			var overlaysJson = ipc.GetIpcSubscriber<string>("HiAuRo.GetOverlaysJson").InvokeFunc();
			if (string.IsNullOrEmpty(overlaysJson)) return;

			using var doc = JsonDocument.Parse(overlaysJson);
			foreach (var item in doc.RootElement.EnumerateArray())
			{
				string name = item.GetProperty("name").GetString()!;
				string url  = item.GetProperty("url").GetString()!;
				int w       = item.GetProperty("width").GetInt32();
				int h       = item.GetProperty("height").GetInt32();
				float zoom  = item.GetProperty("zoom").GetSingle();
				bool locked = item.GetProperty("locked").GetBoolean();

				var existing = _settings?.Config.Inlays.Find(o => o.Name == name);
				if (existing == null)
				{
					existing = new InlayConfiguration
					{
						Guid = Guid.NewGuid(),
						Name = name,
						Url = url,
						Width = w,
						Height = h,
						Zoom = zoom,
						Locked = locked,
						Framerate = 30,
						Hidden = false
					};
					_settings?.Config.Inlays.Add(existing);
				}
				else
				{
					existing.Url = url;
					existing.Width = w;
					existing.Height = h;
					existing.Zoom = zoom;
					existing.Locked = locked;
					existing.Hidden = false;
				}
			}

			if (_settings != null)
				Services.PluginInterface.SavePluginConfig(_settings.Config);
			_settings?.HydrateOverlays();
		}
		catch { /* HiAuRo IPC not available — silently skip */ }
	}

	private void RegisterIpc()
	{
		var ipc = Services.PluginInterface;

		// Browsingway.IsReady
		ipc.GetIpcProvider<bool>("Browsingway.IsReady").RegisterFunc(() =>
		{
			unsafe { return _renderProcess?.Rpc != null && DxHandler.Device != null; }
		});

		// Browsingway.Overlay.Exists
		ipc.GetIpcProvider<string, bool>("Browsingway.Overlay.Exists").RegisterFunc(name =>
			_overlays.Values.Any(o => o.Name == name));

		// Browsingway.Overlay.CreateOrUpdate
		ipc.GetIpcProvider<CreateOrUpdateArgs, object>("Browsingway.Overlay.CreateOrUpdate").RegisterAction(args =>
		{
			InlayConfiguration? existing = _settings?.Config.Inlays.Find(o => o.Name == args.Name);
			if (existing == null)
			{
				existing = new InlayConfiguration
				{
					Guid = Guid.NewGuid(),
					Name = args.Name,
					Url = args.Url,
					Zoom = args.Zoom,
					Locked = args.Locked,
					Framerate = 30,
					Hidden = true,
					Width = args.Width,
					Height = args.Height
				};
				_settings?.Config.Inlays.Add(existing);
			}
			else
			{
				existing.Url = args.Url;
				existing.Zoom = args.Zoom;
				existing.Locked = args.Locked;
				existing.Width = args.Width;
				existing.Height = args.Height;
			}

			if (_settings != null)
				Services.PluginInterface.SavePluginConfig(_settings.Config);

		_settings?.HydrateOverlays();
		_ = _renderProcess?.Rpc?.ResizeOverlay(existing.Guid, args.Width, args.Height);
		if (_overlays.TryGetValue(existing.Guid, out var ov)) ov.Reload();
		});

		// Browsingway.Overlay.SetVisibility
		ipc.GetIpcProvider<SetVisibilityArgs, object>("Browsingway.Overlay.SetVisibility").RegisterAction(args =>
		{
			InlayConfiguration? cfg = _settings?.Config.Inlays.Find(o => o.Name == args.Name);
			if (cfg != null)
		{
			cfg.Hidden = !args.Visible;
			if (_settings != null)
				Services.PluginInterface.SavePluginConfig(_settings.Config);
			if (_overlays.TryGetValue(cfg.Guid, out var ov)) ov.Reload();
		}
		});

		// Browsingway.Overlay.SetPosition
		ipc.GetIpcProvider<SetPositionArgs, object>("Browsingway.Overlay.SetPosition").RegisterAction(args =>
		{
			Overlay? overlay = _overlays.Values.FirstOrDefault(o => o.Name == args.Name);
			overlay?.SetPosition(args.X, args.Y);
		});

		// Browsingway.Overlay.SetDisabled
		ipc.GetIpcProvider<SetDisabledArgs, object>("Browsingway.Overlay.SetDisabled").RegisterAction(args =>
		{
			InlayConfiguration? cfg = _settings?.Config.Inlays.Find(o => o.Name == args.Name);
			if (cfg == null) return;
			cfg.Disabled = args.Disabled;
			if (_settings != null)
				Services.PluginInterface.SavePluginConfig(_settings.Config);
			// Disable: 销毁 CEF，移除 overlay
			// Enable:  重新创建 overlay
			if (args.Disabled)
			{
				if (_overlays.Remove(cfg.Guid, out var ov))
					ov.Dispose();
			}
			else
			{
				_settings?.HydrateOverlays();
			}
		});
	}

	private (bool, long) OnWndProc(WindowsMessage msg, ulong wParam, long lParam)
	{
		// Notify all the overlays of the wndproc, respond with the first capturing response (if any)
		// TODO: Yeah this ain't great but realistically only one will capture at any one time for now.
		IEnumerable<(bool, long)> responses = _overlays.Select(pair => pair.Value.WndProcMessage(msg, wParam, lParam));
		return responses.FirstOrDefault(pair => pair.Item1);
	}

	private void OnActAvailabilityChanged(object? sender, bool e)
	{
		_settings?.OnActAvailabilityChanged(e);
	}

	private void OnOverlayAdded(object? sender, InlayConfiguration overlayConfig)
	{
		if (_renderProcess is null || _settings is null)
		{
			return;
		}

		Overlay overlay = new(_renderProcess, overlayConfig, _pluginDir);
		_overlays.TryAdd(overlayConfig.Guid, overlay);
	}

	private void OnOverlayNavigated(object? sender, InlayConfiguration config)
	{
		if (_overlays.TryGetValue(config.Guid, out var overlay))
			overlay.Navigate(config.Url);
	}

	private void OnOverlayDebugged(object? sender, InlayConfiguration config)
	{
		if (_overlays.TryGetValue(config.Guid, out var overlay))
			overlay.Debug();
	}

	private void OnOverlayRemoved(object? sender, InlayConfiguration config)
	{
		if (_overlays.Remove(config.Guid, out var overlay))
		{
			overlay.Dispose();
		}
	}

	private void OnOverlayZoomed(object? sender, InlayConfiguration config)
	{
		if (_overlays.TryGetValue(config.Guid, out var overlay))
			overlay.Zoom(config.Zoom);
	}

	private void OnOverlayMuted(object? sender, InlayConfiguration config)
	{
		if (_overlays.TryGetValue(config.Guid, out var overlay))
			overlay.Mute(config.Muted);
	}

	private void OnUserCssChanged(object? sender, InlayConfiguration config)
	{
		Overlay overlay = _overlays[config.Guid];
		overlay.InjectUserCss(config.CustomCss);
	}

	private void Render()
	{
		_dependencyManager.Render();
		_settings?.Render();

		ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(0, 0));

		_renderProcess?.EnsureRenderProcessIsAlive();
		_actHandler.Check();

		foreach (Overlay overlay in _overlays.Values) { overlay.Render(); }

		ImGui.PopStyleVar();
	}

	private void HandleCommand(string command, string rawArgs)
	{
		// Docs complain about perf of multiple splits.
		// I'm not convinced this is a sufficiently perf-critical path to care.
		string[] args = rawArgs.Split(null as char[], 2, StringSplitOptions.RemoveEmptyEntries);

		if (args.Length == 0)
		{
			Services.Chat.PrintError(
				"No subcommand specified. Valid subcommands are: config,overlay.");
			return;
		}

		string subcommandArgs = args.Length > 1 ? args[1] : "";

		switch (args[0])
		{
			case "config":
				_settings?.HandleConfigCommand(subcommandArgs);
				break;
			case "inlay":
				_settings?.HandleOverlayCommand(subcommandArgs);
				break;
			case "overlay":
				_settings?.HandleOverlayCommand(subcommandArgs);
				break;
			default:
				Services.Chat.PrintError(
					$"Unknown subcommand '{args[0]}'. Valid subcommands are: config,overlay,inlay.");
				break;
		}
	}
}