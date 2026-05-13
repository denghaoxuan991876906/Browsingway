# Browsingway IPC API

Browsingway 通过 Dalamud `IPluginIpc` 暴露 5 个端点，供其他插件（HiAuRo）程序化控制浏览器 overlay。

## 端点

### Browsingway.IsReady

检查 Browsingway 插件及 CEF 渲染进程是否就绪。

```
Func<void, bool>
```

```csharp
// HiAuRo 调用示例
var isReady = pluginInterface.GetIpcSubscriber<bool>("Browsingway.IsReady").InvokeFunc();
```

返回 `true` 时可创建 overlay。

---

### Browsingway.Overlay.Exists

查询指定 overlay 是否存在。

```
Func<string, bool>
```

```csharp
var exists = pluginInterface.GetIpcSubscriber<string, bool>("Browsingway.Overlay.Exists").InvokeFunc("HiAuRo.MainWindow");
```

---

### Browsingway.Overlay.CreateOrUpdate

创建或更新 overlay。不存在时新建，已存在时更新属性。**Idempotent，可重复调用。**

```
Action<CreateOrUpdateArgs>
```

```csharp
pluginInterface.GetIpcSubscriber<CreateOrUpdateArgs, object>("Browsingway.Overlay.CreateOrUpdate").InvokeAction(new()
{
    Name   = "HiAuRo.MainWindow",
    Url    = "http://localhost:5678/main.html",
    Width  = 310,
    Height = 480,
    Zoom   = 100f,
    Locked = true
});
```

| 字段 | 类型 | 说明 |
|------|------|------|
| Name | string | 唯一标识，建议前缀 `HiAuRo.xxx` |
| Url | string | 完整 URL |
| Width | int | 窗口宽度 (px) |
| Height | int | 窗口高度 (px) |
| Zoom | float | 缩放百分比，100 = 正常 |
| Locked | bool | 锁定窗口（禁止用户拖动/调整大小） |

---

### Browsingway.Overlay.SetVisibility

显示或隐藏 overlay。隐藏时保留 CEF 实例，再次显示无需重载。

```
Action<SetVisibilityArgs>
```

```csharp
pluginInterface.GetIpcSubscriber<SetVisibilityArgs, object>("Browsingway.Overlay.SetVisibility").InvokeAction(new()
{
    Name    = "HiAuRo.MainWindow",
    Visible = true
});
```

| 字段 | 类型 | 说明 |
|------|------|------|
| Name | string | overlay 名称 |
| Visible | bool | true=显示, false=隐藏 |

---

### Browsingway.Overlay.SetPosition

设置 overlay 窗口位置。X/Y 传 null 表示不强制位置（用户自由拖动）。

```
Action<SetPositionArgs>
```

```csharp
// 强制移动到 (100, 200)
pluginInterface.GetIpcSubscriber<SetPositionArgs, object>("Browsingway.Overlay.SetPosition").InvokeAction(new()
{
    Name = "HiAuRo.MainWindow",
    X    = 100,
    Y    = 200
});

// 取消强制位置，恢复用户自由拖动
pluginInterface.GetIpcSubscriber<SetPositionArgs, object>("Browsingway.Overlay.SetPosition").InvokeAction(new()
{
    Name = "HiAuRo.MainWindow",
    X    = null,
    Y    = null
});
```

| 字段 | 类型 | 说明 |
|------|------|------|
| Name | string | overlay 名称 |
| X | int? | 屏幕 X 坐标，null 不强制 |
| Y | int? | 屏幕 Y 坐标，null 不强制 |

---

## 典型调用流程

```csharp
// 1. 等待就绪
while (!pluginInterface.GetIpcSubscriber<bool>("Browsingway.IsReady").InvokeFunc())
    await Task.Delay(1000);

// 2. 创建 overlay
pluginInterface.GetIpcSubscriber<CreateOrUpdateArgs, object>("Browsingway.Overlay.CreateOrUpdate").InvokeAction(new()
{
    Name = "HiAuRo.MainWindow", Url = "http://localhost:5678/main.html",
    Width = 310, Height = 480, Zoom = 100f, Locked = true
});

// 3. 设置位置
pluginInterface.GetIpcSubscriber<SetPositionArgs, object>("Browsingway.Overlay.SetPosition").InvokeAction(new()
{
    Name = "HiAuRo.MainWindow", X = 100, Y = 200
});

// 4. 显示
pluginInterface.GetIpcSubscriber<SetVisibilityArgs, object>("Browsingway.Overlay.SetVisibility").InvokeAction(new()
{
    Name = "HiAuRo.MainWindow", Visible = true
});
```

## 注意事项

- 位置**不持久化**，每次启动需重新 SetPosition
- Overlay **不会自动删除**，HiAuRo 卸载时只需 SetVisibility(false)
- CreateOrUpdate 可重复调用，不会创建重复 overlay
- IPC overlay 和用户在设置面板创建的 overlay **统一管理**
