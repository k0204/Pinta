# Pinta 项目长期备忘

## 动画图层（AnimationOutputLayer）Resize 陷阱（2026-08-14/15）
- `AnimationOutputLayer.Resize` 缩小帧时必须**同步**：按比例缩放每帧 `frame.X/Y`、用 `SetOutputGeometry` 更新图层级 `CanvasWidth/Height`，并由调用方（如 `ResizeLayerAction`）在 Resize 后调 `UpdateTransforms (document.ImageSize)` 重建帧 Transform。
- 原因：帧 Transform（`CreateFrameTransform`）基于 CanvasWidth/Height + frame.X/Y + PositionOffset 计算，只缩帧 surface 会导致帧显示错位、内容被挤出画布。
- **2026-08-15 起动画图层直接禁用「调整图层大小」**：`LayerActions.currentResizable` 排除 `AnimationOutputLayer`（菜单置灰）+ `ResizeLayerAction.Activated` 防御 return。`ResizeAnimationLayer` 等动画分支为死代码保留。

## 图层命中（hit-test）约定（2026-08-14 确认）
- 移动工具点击图层时**不穿透**：命中判定 = 图层内容包围矩形（`TryGetLayerBounds` + `ContainsPoint`），透明孔内点击仍属于该图层；仅点击到所有图层内容矩形之外（空白画布）才穿透/取消选择。
- 隐藏层、锁定层、Opacity=0 层不参与命中；组图层自身无像素（只命中子层）。
- 选区优先：当前层有活动选区时，选区内点击（即使该点透明）命中当前层。

## 工作方式约定（用户明确要求）
- **只修改代码，不要自动编译/构建/运行**。用户自己会用 `./run.bat`（= dev.ps1 -Run，Debug 构建到 `build/bin` 后启动）验证。
- 编译验证仅在被要求时才做；做之前按 AGENTS.md 停掉残留 Pinta/.NET 进程。

## Gtk.ScrolledWindow 与自动 viewport（重要陷阱）
- `Gtk.ScrolledWindow` 对**非 scrollable** 子控件会自动包一个 `Gtk.Viewport`，该 viewport 用自己创建的 `Gtk.Adjustment`。
- `scrolled_window.Hadjustment` getter 在这种场景下是**懒构造的全新死对象**，与 viewport 的实际 adjustment **不是同一个**——订阅 `OnChanged` 永不触发，`PageSize` 永远 0，写入不影响滚动。
- 正确做法：从 `scrolled_window.Child`（即自动 viewport）调 `GetHadjustment()`/`GetVadjustment()`（参考 `CanvasWindow.cs` / `DocumentWorkspace.cs`）。
- 适用判断：`Gtk.Picture`、`Gtk.Box` 等非 scrollable 子控件作为 scrolled window 的 child 时。
- 现象：滚动条可能出现（viewport 自己的 adjustment 配置了），但通过 `scrolled_window.Hadjustment` 读/写都不生效。

## 构建环境注意事项（Windows 沙箱机）
- 本机为沙箱环境（ACL 含 `CodexSandboxUsers` 组）。
- `Pinta.Resources` 图标输出（bin\Debug|Release\net10.0\icons\...）会被进程句柄锁定（写入/删除均 Access denied，handle64 无管理员权限看不到持有者，疑似 SYSTEM 级进程或沙箱文件层）。失败构建留下的**残留 MSBuild 节点（dotnet.exe MSBuild.dll /nodemode:1）**也会持有输出文件句柄，导致后续构建连锁失败。
- 若构建报 MSB3021/MSB4018 Access denied：先 `Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'"` 并 `Stop-Process` 全部清理，再重试；或重跑一次即可（瞬时锁）。
- `dotnet build Pinta.Gui.Widgets -c Release` 在锁出现前可通过；Debug 全量走 `build/bin` 不受项目级 bin 锁影响。
