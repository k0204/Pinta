# 动画输出层设计

## 目标

动画制作分成两个入口：

- 多方向图片动作制作：先生成或导入方向图，再把方向图拆成多个方向，最后为对应方向制作动作帧。
- 单图动画制作：输入被视为一个方向，跳过方向图拆分，直接制作一个方向的动作帧。

两个入口使用独立窗口和独立数据层，但共享拆帧预览、画布定位、帧可见性、缩略图和历史基础组件。单图不能因为输入本身看起来像网格就自动变成多方向；方向类型由入口选择决定。

## 图层类型

```text
GroupLayer
└── AnimationOutputLayer
    ├── SpriteSheetLayer
    └── SingleDirectionAnimationLayer
```

`AnimationOutputLayer` 只保存和处理两种层共有的输出行为：

- 输出画布尺寸和整体位置偏移。
- 帧的渲染变换、图层平移和文档画布变化后的变换更新。
- 第一组显示帧的缩略图。
- 禁止普通像素编辑、裁剪、缩放和旋转。
- 统一的动画输出层识别入口 `DocumentLayers.IsAnimationOutputLayer`。

基类不包含动作、方向或快照接口。这样单方向层不需要伪造 `Directions`，后续增加其他动画输出类型时也不会污染已有数据模型。

## 通用序列帧结构

`AnimationFrameData` 表示一帧的公共数据：帧序号、输出位置、可见性和像素表面。

`AnimationFrameSequenceData` 表示有序帧序列，是两个动画层共同使用的容器：

```text
AnimationFrameSequenceData
└── Frames: List<AnimationFrameData>
```

运行时数据保持明确的嵌套关系：

```text
SpriteSheetLayer
└── Action
    └── Direction
        └── Sequence
            └── Frame

SingleDirectionAnimationLayer
└── Action
    └── Sequence
        └── Frame
```

`Frames` 属性只作为已有拆帧代码的便捷访问；持久化、快照和复制都通过序列帧容器复制帧数据。两种层仍使用各自的动画数据、校验键和合并规则：

- 多方向键为 `(ActionId, DirectionId, FrameIndex)`。
- 单方向键为 `(ActionId, FrameIndex)`，方向 ID 单独保存在层上，默认是 `default`。

## 画布和图层窗口

主画布默认显示：

- `SpriteSheetLayer` 的第一动作、第一方向的可见帧。
- `SingleDirectionAnimationLayer` 的第一动作的可见帧。

动画预览窗口可以选择动作和方向，但不改变主画布的默认显示规则。主画布只允许平移整个动画输出层，不直接编辑单帧像素。

图层树中的动画输出层是不可展开的叶子层，即使运行时类型仍然继承 `GroupLayer`：

- 显示第一动作第一帧缩略图。
- 不显示文件夹图标和展开入口。
- `SpriteSheetLayer` 的右键菜单进入多方向动画编辑窗口。
- `SingleDirectionAnimationLayer` 的右键菜单进入单方向动画编辑窗口。
- 普通 `UserLayer` 显示两个创建入口：多方向动画和单方向动画；动画输出层只显示各自的编辑入口。

## 多方向工作流

```text
multi-direction-animation
├── direction-set
│   ├── attempt-01
│   │   └── source-sheet
│   └── direction-<id>
│       └── source-image
└── actions
    └── action-<id>
        └── attempt-01
            ├── source-sheet
            └── SpriteSheetLayer
```

一个多方向动作请求生成一个包含多个方向序列帧的 `SpriteSheetLayer`。同一个输出层不能重复挂在多个方向节点下；方向节点保存输入和尝试记录，输出层保存最终可编辑数据。

## 单方向工作流

```text
single-direction-animation
├── source-image
└── actions
    └── action-<id>
        └── attempt-01
            ├── source-sequence
            └── SingleDirectionAnimationLayer
```

单方向入口固定生成一个 `default` 方向。可以连续选择不同动作，每个动作拥有独立尝试记录和输出层；编辑窗口只显示当前单方向层的动作帧，不显示多方向选择控件。

如果同一单方向输出层需要保存多个动作，使用其 `Animations` 集合追加新的 `SingleDirectionAnimationData`，不把帧伪装成方向集合。

## 快照、历史和持久化

两种层分别使用：

- `SpriteSheetLayerSnapshot` 和多方向合并规则。
- `SingleDirectionAnimationLayerSnapshot` 和单方向合并规则。

`.pintaproject` 项目格式版本为 1。多方向帧写入 `resources/spritesheets/{layer-id}/`，单方向帧写入 `resources/single-direction-animations/{layer-id}/`。manifest 使用不同的 `Kind` 和独立数据字段：

- `spritesheet` + `SpriteSheetAnimations`。
- `single-direction-animation` + `SingleDirectionId` + `SingleDirectionAnimations`。

不把 `Directions` 设计成可选字段，也不自动把已有 `SpriteSheetLayer` 转换为单方向层。

## 组件边界

两个窗口共享以下组件：

- `AnimationFrameData` / `AnimationFrameSequenceData`。
- 拆帧输入、网格/AI 分析、帧列表、帧预览和定位控件。
- 输出画布尺寸、位置偏移、可见性和帧历史。

多方向窗口额外负责方向顺序、方向合并和动作的多方向生成；单方向窗口隐藏方向集合和方向合并，只负责单个方向下的动作序列。
