# Pinta 作为 UI 编辑器的调研与实施方案

## 1. 结论

Pinta 可以作为 UI 布局编辑器使用，但不应把它改造成完整的 Unity 或 Godot UI 运行时编辑器。最小可行路线是：

1. 引擎插件把 UI 节点导出为 Pinta 的图片图层树。
2. Pinta 使用现有的图层树、图片预览、移动、缩放和重排能力编辑界面。
3. Pinta 保存为现有的 `.pintaproject` 目录格式。
4. 引擎插件读取 `.pintaproject`，按照稳定节点 ID 回写原 UI。

Pinta 只负责：

- 图层父子关系
- 图层顺序
- 位置和尺寸
- 图片预览
- 节点的稳定 ID 和引擎元数据保存

Pinta 不负责修改已有节点的：

- 脚本和事件绑定
- 文字内容、字体和材质
- Sprite、SpriteAtlas、Texture 或 AtlasTexture 的原生引用
- 引擎专有组件和属性

首批目标为 Unity 2022 LTS uGUI + TextMeshPro Prefab，以及 Godot 4.x `Control` 场景。Unreal、Cocos 等引擎后续复用同一套 `.pintaproject` 交换约定。

## 2. 当前 Pinta 的可复用基础

当前代码已经具备实现基础：

- [`UserLayer.cs`](../Pinta.Core/Classes/UserLayer.cs) 支持父子图层树，一个节点可以同时拥有图片和子节点。
- [`DocumentLayers.cs`](../Pinta.Core/Classes/DocumentLayers.cs) 已提供插入、移动、重排、重父和树范围变换操作。
- [`PintaDocumentManifest.cs`](../Pinta.Core/ImageFormats/PintaDocumentManifest.cs) 已将图层递归保存为树。
- [`PintaDocumentFormat.cs`](../Pinta.Core/ImageFormats/PintaDocumentFormat.cs) 已支持目录式项目、PNG 资源、稳定图层 ID、路径校验和原子保存。
- [`ImageConverterManager.cs`](../Pinta.Core/Managers/ImageConverterManager.cs) 已提供文档格式注册入口。
- `LayersListView` 已支持树形拖拽和父子节点重排。

当前图层渲染是基于图片 Surface 和 Transform 的，因此最小方案应继续使用这些数据，而不是另建一套平行 UI 树。

## 3. `.pintaproject` 交换约定

不新增 `.pintaui` 格式，直接使用现有目录结构：

```text
example.pintaproject/
├── project.json
└── resources/
    └── layers/
        └── <layer-id>/
            └── <save-id>.png
```

### 3.1 项目级元数据

在 `project.json` 的 manifest 增加可选 `Metadata` 字典。普通 Pinta 项目没有这些字段时，行为保持不变。

推荐字段：

| Key | 用途 |
| --- | --- |
| `pinta.ui.enabled` | 标记该项目来自 UI 布局工作流 |
| `pinta.ui.source-engine` | `unity-ugui` 或 `godot-control` |
| `pinta.ui.source-document` | Prefab 或 `.tscn` 的源标识 |
| `pinta.ui.source-root-id` | 托管 UI 根节点 ID |
| `pinta.ui.reference-width` | 引擎 UI 参考宽度 |
| `pinta.ui.reference-height` | 引擎 UI 参考高度 |
| `pinta.ui.revision` | 导出版本或内容哈希 |

这些字段是内部元数据，不翻译。

### 3.2 图层元数据

继续使用现有 `PintaDocumentLayerNode.Metadata` 和 `UserLayer.Metadata` 保存引擎信息：

| Key | 用途 |
| --- | --- |
| `pinta.ui.node-role` | `image`、`container`、`text`、`button`、`unknown` |
| `pinta.ui.source-type` | 引擎原生组件类型 |
| `pinta.ui.asset-id` | 跨引擎映射用的逻辑资源 ID |
| `pinta.ui.asset-binding` | Unity/Godot 原生资源定位信息 |
| `pinta.ui.managed` | 是否由插件托管 |
| `pinta.ui.layout-controller` | 是否受 Unity LayoutGroup 或 Godot Container 控制 |
| `pinta.ui.placeholder` | 是否为未知节点占位符 |

图层 ID直接复用现有 `DocumentId`。引擎插件生成 UUID，Pinta 保存时保持不变；不能使用图层名称、路径或数组索引作为 ID。

## 4. 引擎到 Pinta

### Unity uGUI

Unity 插件独立为 Editor/UPM 包，不把 Unity 程序集加入 Pinta 解决方案。

导出选定 Prefab 时：

1. 递归扫描 Prefab 子树。
2. 为每个 GameObject 保存稳定 `PintaUiNodeId` 组件。
3. 有自身视觉内容的节点导出为普通 `UserLayer`，Surface 是该节点的预览 PNG。
4. 无自身视觉内容但有子节点的节点导出为 `GroupLayer`。
5. 未识别节点仍导出预览图，并标记为 `unknown` 和 `placeholder`。
6. 记录 `RectTransform` 的最终画布矩形、节点顺序和父子关系。
7. 记录 Sprite、SpriteAtlas、TMP 字体等原生资源绑定，但不要求 Pinta解析它们。

Unity 插件使用 Prefab 编辑器 API 操作资源，不直接改写 YAML 文件。相关操作应通过 Unity Undo 系统执行。

### Godot 4.x

Godot 插件使用 `EditorPlugin`，目标是选定的 `.tscn` 场景：

1. 为每个托管 Node 保存稳定 UUID metadata。
2. `Control` 节点映射为 `GroupLayer` 或带预览的 `UserLayer`。
3. `TextureRect`、`Label`、`Button` 等节点导出为图片预览图层。
4. 保留脚本、Signal、主题、材质和未知属性。
5. 记录 Texture、AtlasTexture 的原生路径、UID 和区域信息。

## 5. Pinta 到引擎

引擎插件读取 `.pintaproject/project.json` 和 `resources/layers/`，不需要 Pinta 生成额外的引擎文件。

### 已有节点

插件按稳定 ID查找原节点，只修改：

- 位置
- 尺寸
- 父节点
- sibling 顺序

已有的脚本、事件、文字、图片、材质和未知组件全部保留。

### Pinta 新建节点

- 普通图片图层默认创建 Unity `Image` 或 Godot `TextureRect`。
- 组图层默认创建容器节点。
- 新建图片优先使用 `pinta.ui.asset-binding` 指向的目标引擎资源。
- 没有资源绑定时，插件可以使用 Pinta 项目中的 PNG 作为新的普通图片资源，并给出资源导入提示。
- Pinta 不创建跨引擎的脚本事件绑定。

### 删除和冲突

导入前生成差异预览：

- 新增节点
- 移动节点
- 尺寸变化
- 父子关系变化
- 待删除节点
- 资源无法映射的节点

只有用户确认后才删除 Pinta 中已删除的托管节点。若引擎侧在导出后也修改了同一节点，插件比较导出时的布局哈希，冲突节点默认跳过并要求明确确认。

## 6. 图片、图集和占位符

引擎导出的图层 Surface 只是 Pinta 编辑时的预览，不替代引擎原生资源。

- 同引擎回写：按原生绑定继续使用原 Sprite、SpriteAtlas、Texture 或 AtlasTexture。
- 跨引擎导入：按逻辑 `asset-id`、名称、尺寸和图集区域自动匹配。
- 无法匹配时：保留 Pinta 中的预览图，目标引擎创建占位图片并显示警告。
- 未知节点回写原引擎：通过稳定 ID命中原节点，只调整布局，原未知组件继续存在。
- 未知节点导入另一引擎：保留树节点和预览图，作为占位节点处理。

Unity 资源根目录和 Godot `res://` 资源根目录由各自插件配置，不扫描整个工程。

布局控制器节点，例如 Unity `HorizontalLayoutGroup`、`VerticalLayoutGroup`、`GridLayoutGroup` 或 Godot `Container`，必须被标记为受约束节点。插件不能静默修改会被引擎自动布局覆盖的子节点矩形。

## 7. Pinta 侧实现范围

Pinta 不新增独立 UI 文档类型和独立 UI 图层类型，只增加以下能力：

- manifest 项目级元数据。
- UI 元数据键常量和校验。
- 对 UI 图层禁用像素编辑命令。
- 在现有图层树上显示 UI 节点角色和占位状态。
- 在现有移动、缩放、重排和重父操作基础上完成布局编辑。
- 保存时保持所有 UI 元数据和资源引用。

普通图片项目没有 `pinta.ui.enabled` 时，继续按照普通 Pinta 项目处理。

所有新增菜单、提示、错误、状态和诊断文本使用 `Translations.GetString`。本功能不新增 HTTP API，因此 `doc/api.md` 不需要新增接口条目。

## 8. 分阶段实施

### 阶段一：Pinta 格式和元数据

- 扩展 manifest 项目级 Metadata。
- 定义 UI 元数据键和稳定 ID规则。
- 保存、加载、复制、删除图层时保持元数据。
- 增加 UI 图层保护和诊断显示。

### 阶段二：Unity Prefab 插件

- 导出 Prefab 为 `.pintaproject`。
- 导入 `.pintaproject` 并按 ID更新 Prefab。
- 实现差异预览、删除确认和冲突检测。
- 验证 SpriteAtlas、TMP 和 LayoutGroup 场景。

### 阶段三：Godot 插件

- 导出 `.tscn` 为 `.pintaproject`。
- 按 UUID回写 Control 场景。
- 实现 Texture/AtlasTexture 资源映射。

### 阶段四：跨引擎资源映射

- 增加逻辑资源 ID和资源根目录索引。
- 支持资源自动匹配和手动选择。
- 对无法映射的资源和未知节点显示占位符。

## 9. 验收清单

不新增或修改测试文件，使用临时工程、现有测试项目和构建检查：

- Unity Prefab 导出 → Pinta 打开 → 移动/缩放/重排 → Unity 回写。
- Godot `.tscn` 完成同样往返流程。
- 稳定 ID在重命名、重排和重新保存后不变化。
- 已有脚本、Button 事件、TMP、SpriteAtlas 和未知组件不被覆盖。
- Pinta 删除节点时插件要求确认。
- LayoutGroup/Container 控制的节点不会静默失效。
- 资源缺失、重复 ID、非法路径、损坏 JSON和未知版本均能报告错误。
- 普通 `.pintaproject`、PNG、ORA 和现有保存流程保持不变。
- 编译前停止已有 Pinta 和 `.NET Host` 进程。

## 10. 明确不做的内容

- 不新增 `.pintaui` 文件格式。
- 不直接解析或改写 Unity Prefab YAML。
- 不把 Pinta 变成 Unity/Godot 组件属性编辑器。
- 不转换脚本、事件或 Signal。
- 不保证未知节点跨引擎完整重建，只保证原引擎按 ID回写时保留。
- 首版不实现 Unreal、Cocos 等引擎插件。
