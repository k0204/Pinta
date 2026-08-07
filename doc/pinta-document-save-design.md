# Pinta 项目目录保存设计

## 目标

Pinta 的原生可编辑项目只使用 `.pintaproject` 目录格式。旧 `.pinta` ZIP 文件不再支持打开、迁移或保存。

普通图片格式仍用于导入和导出，但只有 `.pintaproject` 会成为 `Ctrl+S` 的原生保存目标。本格式不保存撤销/重做历史、缩放、滚动位置、当前工具或多个标签页。

## 目录结构

```text
example.pintaproject/
├── project.json
├── resources/
│   ├── layers/
│   ├── spritesheets/
│   └── single-direction-animations/
└── .staging/
```

- `project.json` 是当前已提交项目的权威 manifest。
- `resources/` 保存普通图层和动画帧 PNG。
- `.staging/{save-id}/` 保存一次提交尚未完成的临时文件。

manifest 的 `format` 固定为 `pinta-document`，`version` 从 `1` 开始。实现只读取当前版本，不包含旧 ZIP 格式或旧 manifest 版本的兼容分支。

## Manifest

`project.json` 直接序列化 `Document.Layers.RootLayers`，保持 tree-first 图层模型。主要数据包括：

- 画布宽高、资源根目录和当前选中图层 ID。
- 选区、辅助线和嵌套图层树。
- 图层名称、显示状态、透明度、混合模式、展开状态、变换和元数据。
- 普通图层的资源路径、像素尺寸和 `surfaceHash`。
- 引用图层的相对引用路径。
- 多方向动画的 `Action -> Direction -> Frame` 数据。
- 单方向动画的方向 ID 和 `Action -> Frame` 数据。
- 每个动画帧的资源路径、尺寸、位置、显示状态和 `surfaceHash`。

图层使用稳定的文档内 ID。资源文件名不使用用户输入的图层、动作或方向名称。

## 保存策略

保存前先提交当前工具状态，然后按以下顺序执行：

1. 为普通图层及每个动画帧计算 Cairo 像素数据的 SHA-256 `surfaceHash`。
2. 读取目标目录当前的 `project.json`。哈希相同且旧资源仍存在时，直接复用旧资源路径。
3. 只把发生变化的 surface 编码到 `.staging/{save-id}/`。
4. 将 staged PNG 移动到本次保存专用的 `resources/` 路径。
5. 在 staging 中完整写出新的 `project.json`，再原子替换项目根目录的 manifest。
6. manifest 提交成功后，删除不再被新 manifest 引用的旧资源，并清理 staging。

仅修改图层名称、选区、辅助线等元数据时不会重新编码 PNG。只修改一个图层或动画帧时，只写对应资源。

资源先使用新的保存代次路径，旧 manifest 在提交前始终只引用旧资源。保存失败或进程中断时，旧 `project.json` 和它引用的资源仍然可打开。manifest 替换是提交点；提交后的旧资源清理失败不会使已提交项目失效。

本版不对单个大图层做瓦片化。修改大图层后仍需重新编码该图层的完整 PNG。

## Save As

`Save As` 选择 `.pintaproject` 时创建一个完整的新项目目录。导出和 manifest 提交全部成功后，才更新：

- `Document.File`
- `Document.FileType`
- history clean 状态
- `HasBeenSavedInSession`

失败或取消时，文档继续关联原保存位置。

## 打开策略

原生项目打开入口使用目录选择器，只接受名称以 `.pintaproject` 结尾的目录。Importer 读取并校验 `project.json`，递归恢复图层树、选区、辅助线、引用图层和两类动画数据。

校验包括格式与版本、尺寸、图层 ID、枚举值、树节点类型、资源相对路径、资源存在性、动画键唯一性和引用根目录。资源路径必须位于项目的 `resources/` 下，不能包含绝对路径或 `..`。

## 验证清单

- 创建新项目目录，保存后重新打开。
- 嵌套图层、选区、辅助线、引用图层和动画数据 round-trip。
- 只改元数据时不生成新 PNG。
- 只改一个图层或帧时只更新对应资源。
- 保存失败或中断后，旧 `project.json` 仍能打开。
- 打开和保存入口不再出现 `.pinta`。
- 普通图片导出继续工作。
