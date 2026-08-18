# AI Asset Workbench

浏览器内可复用的图像元素工作流：导入场景与参考图、建立本地初始遮罩、画笔精修、导出透明 PNG、以可调不透明度/混合模式合成回原图，并可选接入 OpenAI 兼容服务生成变体和进行 AI 对话。

## 使用

```powershell
python -m http.server 4173 --directory ai-asset-workbench
```

然后打开 <http://localhost:4173>。载入示例会使用 `assets/scene.png`；第二张图可通过“添加参考图”导入。

## 本地 SAM 2

SAM 2 已安装到 `H:\AISpine\AIModel\sam2`，权重位于 `H:\AISpine\AIModel\models\sam2\sam2_hiera_base_plus.pt`。这是共享模型目录，不是工作台源码目录：同一份 323 MB 权重可被多个 AI 项目复用，避免每个项目重复下载和提交二进制模型。工作台只保存服务代码与流程配置。

如需项目专用模型位置，可在启动前设置 `SAM2_REPO` 和 `SAM2_CHECKPOINT`，服务会优先使用这两个环境变量。启动本地分割服务：

```powershell
& H:\AISpine\AIModel\venv\Scripts\python.exe .\ai-asset-workbench\sam2_service.py
```

服务地址为 <http://127.0.0.1:8765>。工作台在用户框选/点击并确认后调用 `/segment`；如果 SAM 2 服务失败，确认操作会保留选区并提示错误，不会把矩形选框直接当成前景。没有 SAM 2 时可使用“自动抠图”作为独立的本地启发式流程。

## AI 服务

在“API 设置”中填写上层项目的 API 地址（默认 `http://localhost:8080/`）和登录后的 Bearer Token。工作台按现有协议调用 `POST /api/images`（multipart，返回 job 后轮询 `/api/images/jobs/{id}` 和 `/result`）以及 `POST /api/chat`；Nano Banana 使用 `provider=tokenx24`、`resolution=1K` 等服务端字段。Token 只存于当前页面内存，不写入工作区。

没有 Key 时仍可使用本地自动遮罩、画笔精修、透明 PNG 导出和合成导出。自动遮罩是示例图友好的启发式起点，要求最终质量时应接入专业分割模型（rembg、SAM 或 remove.bg）并在画笔中检查细枝、半透明花瓣和反射边缘。

## 建议的无痕还原流程

1. 用“自动抠图”得到初始选区；用“添加/移除”画笔补齐草叶和花瓣。
2. 将“边缘羽化”保持在 2–8 px，透明 PNG 导出后再做合成。
3. 通过混合模式和不透明度匹配原图光照，最后导出合成图。避免重复压缩，优先使用 PNG。
