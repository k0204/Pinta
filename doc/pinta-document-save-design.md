# Pinta 文档级保存设计

## 目标

当前 Pinta 的保存逻辑是图片级保存：`Save` / `Save As` 保存的是当前 `Document` 对应的一张图片文件，`Save All` 也只是遍历多个打开的 `Document` 逐个保存。

本文档设计一个单文档级的原生保存格式，用于保存一个可继续编辑的 Pinta 文档。它不是多文档项目，也不保存撤销/重做历史。

## 当前数据结构

### Document

位置：`Pinta.Core/Classes/Document.cs`

`Document` 是当前图片/文档主体，已经包含：

- `ImageSize`：画布尺寸。
- `File` / `FileType` / `HasFile`：当前文件关联。
- `DisplayName`：窗口和标签页显示名。
- `IsDirty`：是否需要保存。
- `Layers`：文档图层树。
- `Selection` / `PreviousSelection`：当前选择区域。
- `Workspace`：当前文档的视图和历史状态。

缺口：

- 没有原生文档格式版本。
- 没有文档级 manifest。
- 没有专门保存“可编辑文档状态”的入口。

### DocumentLayers

位置：`Pinta.Core/Classes/DocumentLayers.cs`

`DocumentLayers` 是当前权威图层树 API。它已经支持：

- `RootLayers`：根图层集合。
- `AllLayers`：树结构展开后的全部图层。
- `CurrentUserLayer`：当前选中图层。
- `Insert(UserLayer, LayerPosition)`：按树位置插入图层。
- `MoveLayer(UserLayer, LayerPosition)`：按树位置移动图层。
- `LayerTreeChanged` / `SelectedLayerChanged`：图层树事件。

项目规则要求图层编辑 tree-first，因此文档保存应直接序列化 `RootLayers` 树，不要新增旧式 flat layer 兼容路径。

缺口：

- 当前选中图层没有稳定 ID 可持久化。
- 图层树没有专用序列化模型。

### UserLayer / Layer

位置：

- `Pinta.Core/Classes/UserLayer.cs`
- `Pinta.Core/Classes/Layer.cs`

`Layer` 已经包含可保存的核心渲染状态：

- `Surface`
- `Transform`
- `Opacity`
- `Hidden`
- `Name`
- `BlendMode`

`UserLayer` 在此基础上增加：

- `Parent`
- `Children`
- `Expanded`
- `TextEngine`
- `TextBounds`
- `PreviousTextBounds`
- `ReEditableLayers`

缺口：

- 图层节点需要文档内稳定 `id`。
- `Expanded` 目前只是 UI 状态，但文档级保存需要持久化。
- `TextEngine` / `ReEditableLayers` 是否完整可编辑保存需要后续单独设计；本版保存前提交工具状态，优先保存当前图层结果。

### DocumentSelection

位置：`Pinta.Core/Classes/DocumentSelection.cs`

`DocumentSelection` 已经包含：

- `Visible`
- `SelectionPolygons`
- `HandleBounds`

这些数据可以直接进入文档 manifest。

缺口：

- 当前没有文档文件中的 selection 模型。

### DocumentHistory

位置：`Pinta.Core/Classes/DocumentHistory.cs`

`DocumentHistory` 是会话内状态，包含：

- undo / redo 栈。
- clean pointer。
- dirty 变更逻辑。

本版不保存历史。打开文档后应创建新的历史起点，并将文档标记为 clean。

## 推荐文件结构

新增 `.pinta` 原生文档格式，使用 zip 包保存：

```text
example.pinta
├── project.json
└── layers/
    ├── layer-0001.png
    ├── layer-0002.png
    └── layer-0003.png
```

### project.json

`project.json` 保存文档 manifest：

```json
{
  "format": "pinta-document",
  "version": 3,
  "width": 800,
  "height": 600,
  "selectedLayerId": "layer-0002",
  "selection": {
    "visible": false,
    "handleBounds": { "x": 0, "y": 0, "width": 0, "height": 0 },
    "polygons": []
  },
  "layers": [
    {
      "id": "layer-0001",
      "name": "Background",
      "hidden": false,
      "opacity": 1.0,
      "blendMode": "Normal",
      "expanded": true,
      "surface": "layers/layer-0001.png",
      "surfaceWidth": 800,
      "surfaceHeight": 600,
      "metadata": {},
      "transform": { "xx": 1, "yx": 0, "xy": 0, "yy": 1, "x0": 0, "y0": 0 },
      "children": []
    }
  ]
}
```

## 需要新增的数据模型

建议新增到 `Pinta.Core`：

- `PintaDocumentManifest`
- `PintaDocumentLayerNode`
- `PintaDocumentSelection`
- `PintaDocumentRectangle`
- `PintaDocumentMatrix`

这些类型只用于文件格式读写，不替代运行时的 `Document` / `DocumentLayers` / `UserLayer`。

### PintaDocumentManifest

字段：

- `Format`
- `Version`
- `Width`
- `Height`
- `SelectedLayerId`
- `Selection`
- `Layers`

### PintaDocumentLayerNode

字段：

- `Id`
- `Name`
- `Hidden`
- `Opacity`
- `BlendMode`
- `Expanded`
- `Surface`
- `SurfaceWidth` / `SurfaceHeight`: v3 起保存每个普通图层的真实像素尺寸；允许精灵帧等图层小于文档画布
- `Metadata`: v3 起保存功能级字符串元数据，例如精灵图的动作、方向、帧数和拆分参数
- `Transform`
- `Children`

### PintaDocumentSelection

字段：

- `Visible`
- `HandleBounds`
- `Polygons`

## 保存逻辑

1. 入口仍走现有 `Document.Save(bool saveAs)`。
2. `.pinta` 作为一个新格式注册到 `ImageConverterManager`。
3. 保存前调用 `tools.Commit()`，让当前工具状态落到文档。
4. 从 `Document.Layers.RootLayers` 开始递归生成 `PintaDocumentLayerNode`。
5. 为每个 `UserLayer` 分配稳定的保存 ID，例如 `layer-0001`。
6. 将每个 `UserLayer.Surface` 写入 `layers/{id}.png`。
7. 将图层属性、树结构、selection、选中图层写入 `project.json`。
8. 成功后：
   - 更新 `Document.File`
   - 更新 `Document.FileType`
   - 调用 `document.Workspace.History.SetClean()`
   - 设置 `HasBeenSavedInSession = true`

## 打开逻辑

1. 通过 `.pinta` 扩展名找到 importer。
2. 打开 zip，读取 `project.json`。
3. 校验：
   - `format == "pinta-document"`
   - `version` 是当前支持版本。当前写入 v3，并继续读取 v1/v2；旧版本缺失的尺寸按文档画布处理，元数据按空集合处理。
   - manifest 中引用的 layer PNG 都存在。
4. 创建新的 `Document`。
5. 递归读取 `layers` 树：
   - 创建 `UserLayer`
   - 载入 PNG 到 `Surface`
   - 恢复 `Name`、`Hidden`、`Opacity`、`BlendMode`、`Transform`、`Expanded`
   - 使用 `DocumentLayers.Insert(layer, new LayerPosition(parent, index))` 插入树
6. 根据 `SelectedLayerId` 恢复当前图层。
7. 恢复 `Document.Selection`。
8. 添加一条“Open Document”历史项并标记 clean。

## 不保存的数据

本版明确不保存：

- `DocumentHistory` 的 undo / redo 栈。
- 当前缩放比例和滚动位置。
- 当前激活工具。
- 未提交的工具临时层。
- 多个打开标签页组成的工作区。

## 测试场景

- 单图层 `.pinta` 保存后重新打开，像素和尺寸一致。
- 嵌套图层树 round-trip 后父子关系一致。
- `Hidden`、`Opacity`、`BlendMode`、`Transform`、`Expanded` 保存并恢复。
- 当前选中图层保存并恢复。
- selection 的 `Visible`、`HandleBounds`、`SelectionPolygons` 保存并恢复。
- 损坏 zip、缺失 `project.json`、缺失 layer PNG、未知版本时能给出明确错误。
- 保存成功后 `IsDirty == false`。

## 实现约束

- 使用现有树 API，不增加 flat layer 兼容分支。
- `.pinta` 是原生文档格式，不替代普通图片导出。
- `.ora` 仍作为 OpenRaster 图片格式存在。
- 先实现当前可编辑结果的保存，复杂可重编辑对象的完整语义可作为后续版本扩展。
