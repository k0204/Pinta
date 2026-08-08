# API Documentation

Default API server: `http://localhost:8080/`

## Usage Costs

The server deducts user balance according to `config/usageCosts.json` in the API server.

- `/api/images/jobs:thumbnail`: default cost `1`
- `/api/images/jobs:grayscale`: default cost `1`
- `/api/images/jobs:invert`: default cost `1`
- `/api/images`: default cost configured by the server
- `/api/videos`: default cost configured by the server
- `/api/baidu-images`: default cost `1`
- `/api/chat`: default cost configured by the server

Requests with insufficient balance return `402 Payment Required`.
There are no daily, monthly, or concurrent job-count limits. A valid request is accepted
when the account balance covers its configured usage cost.

## Image Job Retention

- Uploaded source and reference images are deleted after processing.
- Completed and failed job results remain available for `IMAGE_RESULT_RETENTION_HOURS`, which defaults to `24` hours.
- Expired result files and job records are removed when the next image job starts.
- Pinta routes chat, image generation, and Baidu cutout submissions through one client job service. Feature modules only prepare requests and parse completed results; the shared service extracts job IDs, polls status, retrieves results, and reports failures.

## Authentication

### Login

- Method: `POST`
- Path: `/api/auth/login`
- Auth: none
- Content type: `application/x-www-form-urlencoded`
- Form fields:
  - `grant_type`: `password`
  - `username`: user email
  - `password`: user password
  - `scope`: empty string

- Response JSON includes `access_token` and `token_type`.
- Client behavior: saves the token, username, API base URL, and account summary in Pinta settings. After login, the client calls `/api/me` to display the username and balance.

### Register

- Method: `POST`
- Path: `/api/auth/register`
- Auth: none
- Request JSON:

```json
{
  "email": "user@example.com",
  "password": "password",
  "full_name": "user@example.com"
}
```

- Response JSON includes user profile fields.
- Client behavior: logs in after successful registration, then saves the token, username, API base URL, and account summary in Pinta settings.

## Current User

All requests in this section include `Authorization: Bearer <token>`.

### Read Current User

- Method: `GET`
- Path: `/api/me`
- Response JSON includes `email`, `full_name`, `plan`, `balance`, and account flags.
- Client behavior: uses `email` as the displayed username and `balance` as the displayed balance.

### Read Current Plan

- Method: `GET`
- Path: `/api/me/plan`
- Response JSON includes job usage, upload limits, and allowed operations. The daily, monthly, and concurrent job limit fields are `null` because job counts are unlimited.
- The free plan accepts uploads up to `104857600` bytes (100 MiB); the previous 5 MiB limit has been removed.
- Client behavior: not used by the current Pinta account display.

## Smart Selection

- The `Detect Border` tool requires a visible rectangular selection and a logged-in AI account.
- After the rectangle is created, the client can record local keep-brush and erase-brush strokes in a temporary mask overlay. Transparent mask pixels mean no local override; green pixels force foreground retention and red pixels force exclusion. The tool keeps a local undo/redo stack for these strokes.
- It submits the flattened document to `/api/baidu-images` with `return_form=rgba`. A visible selection uses `method=control` with the selected box in Baidu's top-left-origin `position` coordinates; if the selection touches an image edge, only that edge is inset by one pixel because Baidu rejects edge-touching control boxes.
- The official Baidu endpoint receives the rectangle only; it does not receive the webpage's private per-stroke payload. After the completed Base64 PNG returns, Pinta applies local brush overrides to the returned alpha and uses the original flattened pixels for forced-retain strokes.
- The client creates a transparent detected layer, a hidden grayscale `Cutout Mask` layer, and a visible border-control overlay. The mask layer is derived from the final result alpha, so it includes both Baidu segmentation and local edits.
- The old `/api/jobs/{job_id}/parts` local recognition flow is no longer used by Pinta.

## Image Editing

All requests in this section include `Authorization: Bearer <token>`.

### Generate Image

- Method: `POST`
- Path: `/api/images`
- Content type: `multipart/form-data`
- Form fields:
  - `reference_files`: optional repeated PNG files used as visual references; no files are required for text-to-image generation
  - `prompt`: image prompt text
  - `provider`: provider/channel ID (`agnes`, `tokenx24`, `visionary`, `zzswitch`, or `lukyface`); omitted values use the server default
  - `size`: concrete output size, for example `1024x1024`; the server maps or validates it according to the provider's `imageApi`
- Response: `202 Accepted` with an image job object containing `id`, `status`, `operation`, and job metadata.
- Client behavior: polls `GET /api/images/jobs/{id}` while status is `queued` or `processing`. When status is `completed`, it reads `GET /api/images/jobs/{id}/result`.
- Results return `operation`, `prompt`, `provider`, `model`, `size`, `result_url`, and `result_b64_json`, plus the provider-specific raw response.
- When images are supplied, their multipart order is preserved. The first image is the primary edit image and subsequent images are additional references.
- Agnes omits its upstream image field when no references are supplied. GPT Image calls its generation endpoint with no references and its edit endpoint with one or more references.
- Agnes requests use the closest configured Agnes resolution that can contain the source image. Supported resolutions are the configured `1K` through `4K` sizes for ratios `1:1`, `3:4`, `4:3`, `16:9`, `9:16`, `2:3`, `3:2`, and `21:9`.
- Nano Banana requests read `config/NanoBanana.json`, display its configured `1K` through `4K` resolutions, and use the closest configured size that can contain the source image. Pinta sends the selected Nano Banana channel ID (`tokenx24` or `visionary`) as the `provider` field.
- `GET /api/providers` returns safe `image_type` and `channel` metadata. Nano Banana channels are separate providers even though they share the `nano-banana` image type.
- GPT Image requests use dimensions divisible by `16`, between `655360` and `8294400` pixels, with a maximum edge of `3840` and a maximum aspect ratio of `3:1`.
- Failed jobs return `status: failed` and an `error_message` from the job status endpoint.
- Blank prompts are rejected with `400 Bad Request` before the job is queued or balance is deducted.

The client stores background prompts in `config/gpt-image-prompts.json` and reads the white-background prompt when opening the cleanup dialog. The prompt is editable and the submitted value is sent unchanged.

### Generate Video from Image

- Method: `POST`
- Path: `/api/videos/image`
- Content type: `multipart/form-data`
- Form fields:
  - `reference_image`: one or more PNG, JPEG, or WEBP images. Repeat this field for multiple references; the first image is the first video frame/reference and subsequent images are additional references.
  - `prompt`: required video prompt text
  - `provider`: optional video provider ID; omitted values use the server default
  - `model`: optional video model ID; omitted values use the provider default model
  - `mode`: optional generation mode: `first_frame` requires one image, `first_last_frame` requires two ordered images, and `multi_image` accepts 2-10 ordered images. Pinta always sends this field; omission retains the provider's legacy image-input behavior.
  - `parameters`: optional JSON object string containing provider-specific video parameters
- Response: `202 Accepted` with a video job object containing `id`, `status`, `operation`, and job metadata.
- Client behavior: polls `GET /api/videos/jobs/{id}` while status is `queued` or `processing`. When status is `completed`, it reads `GET /api/videos/jobs/{id}/result`.
- Result JSON includes `operation`, `provider`, `model`, `prompt`, `video_mode`, `task_id`, `request_id`, and `video_url`.
- Pinta sends the selected layer first, followed by any selected reference files, as repeated `reference_image` fields. The server maps those ordered files to the selected mode's typed media input. Pinta downloads `video_url` after the job completes and saves the result through the user's `.mp4` file selection.
- Failed jobs return `status: failed` and an `error_message` from the video job status endpoint.
- Blank prompts, unsupported image content types, invalid modes, and image counts that do not match the mode are rejected with `400 Bad Request` before the job is queued or balance is deducted.

## Chat and Sprite Analysis

All requests in this section include `Authorization: Bearer <token>`.

### Provider Catalog

- Method: `GET`
- Path: `/api/providers/catalog`
- Auth: none
- Response: an object with separate `image_providers` and `video_providers` arrays. Image entries contain `id`, `name`, `supports_chat`, `supports_image`, `models`, `image_type`, `channel`, `image_sizes`, `image_resolutions`, and `image_cost`. Video entries contain `id`, `name`, `models`, `default_model`, `supports_video`, `supports_image_to_video`, `supports_reference_video`, and `video_cost`. Credentials and provider URLs are never returned.
- Client behavior: Pinta refreshes this catalog once during startup and caches the last successful response. Chat and image features filter `image_providers`; the image-to-video dialog lists `video_providers` as channels and excludes reference-video (`r2v`) models. Generation dialogs use cached costs and capabilities without making a per-request query.

### Create Chat Job

- Method: `POST`
- Path: `/api/chat`
- Content type: `application/json`
- Request JSON fields:
  - `text`: required prompt sent to the selected provider's `/chat/completions` endpoint
  - `image_base64`: optional ordered array of Base64-encoded JPEG, PNG, or WebP images
  - `provider`: optional provider ID; it must identify a provider with `supports_chat=true`, and omitted values use the server default
- Response: `202 Accepted` with an image job object. The client polls the standard image job endpoints.
- Result JSON includes `operation`, `provider`, `model`, `text`, `finish_reason`, and `usage`.
- `/api/chat` is a generic pass-through: it does not select prompts, request a JSON schema, or parse model text. Smart spritesheet analysis lists the catalog entries with `supports_chat=true`, persists that selection independently from image generation settings, loads its prompt text from `config/sprite-segmentation-prompt.txt`, appends the uploaded PNG's exact dimensions, and sends that PNG as the first `image_base64` array item. The analysis prompt explicitly allows non-grid layouts and independent per-frame bbox sizes: the model returns only absolute source-image `bbox` rectangles and `foot_anchor` points, while Pinta performs the crop and output placement. If the source PNG exceeds 5 MiB, Pinta proportionally downsizes it before upload, changes the prompt dimensions to the uploaded size, and restores returned `bbox` and `foot_anchor` coordinates to the original source dimensions before validation and cropping. Pinta saves each request and response under `ai-sprite-segmentation-logs/<timestamp>/` in the application directory (`request.png`, `request.json`, `accepted.json`, `status.json`, and `result.json`); `request.json` includes the selected `provider`. It extracts the first complete JSON object from the returned text, allowing a model preamble or Markdown code fence, then requires the reported `image_width` and `image_height` to match the uploaded PNG and validates the returned sprite count, unique indices, bounding boxes, and foot anchors before using each AI-returned `items[].bbox` as a source crop. `foot_anchor` must be an object containing numeric `x` and `y`; other shapes are rejected. AI analysis does not request or consume grid dimensions or cell positions; the returned `items` are authoritative and sorted by `index`. It keeps the configured output canvas unchanged and preserves foot-anchor-relative placement inside the output parent.

- The AI image-generation dialog can send a chat request with the currently selected reference layers and files to optimize the editable prompt. The original prompt is the source of truth; references only clarify or enrich compatible visual details. The dialog lists providers with `supports_chat=true` and sends the provider selected by the user. Selecting the `zzChat` provider uses the configuration from `config/zzChatConfig.json`; no chat operation is forced to that provider. The result contains optimized Chinese and English text, the Chinese text replaces the editable prompt for review, the English text is sent to image generation, and the original prompt is used when no optimized English text is available.

## Baidu Intelligent Cutout

All client requests in this section include `Authorization: Bearer <token>`. Baidu credentials exist only on the API server in `config/baiduConfig.json`. `BAIDU_CONFIG_PATH` optionally selects another config file.

### Create Cutout Job

- Method: `POST`
- Path: `/api/baidu-images`
- Content type: `multipart/form-data`
- Form fields:
  - `file`: PNG image, sent as `pinta.png`
  - `method`: optional `auto` or `control`; Pinta sends `control` when an image selection is visible
  - `refine_mask`: optional boolean, defaults to `true`
  - `return_form`: optional `rgba` or `mask`; Pinta uses `rgba` to create the result layer directly
  - `position`: JSON `[[[x1,y1],[x2,y2]]]`, required by `control` mode
- Response: `202 Accepted` with an image job object. The client polls the standard image job endpoints.
- Result JSON:
  - `operation`: `baidu_cutout`
  - `provider`: `baidu`
  - `method`, `refine_mask`, `return_form`, and optional `log_id`
  - `result_b64_json`: Base64-encoded PNG returned from Baidu's `image` field; `rgba` is transparent output and `mask` is a grayscale mask
- Server behavior: obtains and caches the Baidu access token, sends the selected method to Baidu's intelligent cutout endpoint, and never exposes Baidu credentials or access tokens to the client.
- Limits: the server accepts BMP, JPEG, PNG, and WEBP; the Base64 image must not exceed 10 MB; the shortest edge is at least 128 px and the longest edge is at most 3000 px. Invalid input is rejected before balance deduction.

### Client Background Cleanup and Cutout Flow

- Each layer row has one cutout button. Clicking it opens AI Request Settings with `agnes`, `gpt-image`, `nano-banana`, and `baidu` as peer services.
- Agnes, GPT Image, and Nano Banana show the white-background and cutout operations. Baidu shows only cutout.
- Operation buttons immediately save the selected service and close the dialog; there are no separate Cancel or Save buttons.
- White-image generation opens the additional prompt/reference dialog, sends the selected layer and references to Agnes, GPT Image, or Nano Banana, and creates a white-background layer.
- Agnes/GPT/Nano Banana cutout treats the selected layer as the white-background input, generates a black-background image, and diffs the pair into a transparent layer.
- Baidu cutout sends the selected layer to `/api/baidu-images`; with a valid visible Pinta selection it uses `control` mode and sends the selection box, insetting edge-touching edges by one pixel to satisfy Baidu's coordinate limits. It creates one transparent layer from `result_b64_json`.
- GPT provider and Nano Banana channel settings are shown only for their respective image services.
- Agnes, GPT Image, and Nano Banana call `/api/images` with repeated `reference_files`, `prompt`, `size`, and `provider`; the client selects sizes according to the selected provider's constraints.
- When a source image falls between service sizes, the client preserves aspect ratio by scaling and padding it to the selected request size. If both a smaller and larger candidate exist, it asks the user to choose and shows the generation cost before submitting; for example, `2050x2050` can be adapted to `2048x2048` instead of silently choosing a more expensive larger request.
- Generated and edited images are shown in a confirmation preview before a layer or animation attempt is created. The preview compares the original and generated image side by side and exposes previous/next candidate controls; canceling creates no layer.
- Image-generation charges apply to `/api/images` requests. `ImageSplit` opens the `AI Image Generation` dialog with generation type `Split Image`, then opens an `Image Split Preview` dialog showing the service, provider/channel, cost, original image, and the adapted request image. The preview offers the closest smaller and larger provider resolutions, GPT Image custom sizes, and white or transparent padding; no AI request is sent until the user confirms. The confirmed adapted image is uploaded as the primary reference followed by any optional layer or file references chosen by the user, and the result is normalized back to the source layer's dimensions before insertion as one child `UserLayer` beneath that source. Multi-direction and single-direction animation creation remain separate commands and retain their own spritesheet input and split data.
- The layer toolbar also provides AI image generation. It accepts a required prompt, a service-specific output size, and optional reference layers/files, then adds the result as a new undoable layer in the current document. GPT Image exposes distinct 1K, 2K, and 4K resolution tiers with aspect-ratio presets plus custom width and height values; custom values are validated against the GPT Image constraints above and labeled by total pixels: up to `1024x1024` is 1K, up to `2048x2048` is 2K, and larger valid sizes are 4K. The 16:9 reference sizes are `1280x720`, `2560x1440`, and `3840x2160`. Nano Banana exposes the same distinct 1K, 2K, and 4K resolution tiers with configured aspect ratios and does not expose custom width and height inputs; the selected pair is sent as the concrete `size` value.
- The layer toolbar provides separate multi-direction and single-direction animation generators that use the same `/api/images` multipart contract; animation fields are client-side metadata and are not additional API form fields. Multi-direction direction-sheet and action modes use the fixed eight directions in canonical clockwise order, with a white background. Multi-direction action mode combines one action and one shared frame count for every direction into a single row-major spritesheet prompt. Single-direction mode uses one `default` direction and sends the complete user-entered prompt unchanged; it has no action or frame-count controls, and changing the requested resolution or aspect ratio does not rewrite that prompt.
- A successful multi-direction request stays in the active document and creates `multi-direction-animation/actions/<action>/attempt-NN/source-sheet` for an action request or `multi-direction-animation/direction-set/attempt-NN/source-sheet` for a direction sheet. The attempt stores generation type, action ID, ordered direction IDs, frame count, grid, size, background ID, and final prompt in the `.pinta` layer metadata. Network failure, cancellation, or invalid PNG data creates no attempt.
- After the user marks an approved direction-sheet `source-sheet` as the character anchor, it is selected by default as `character-anchor.png` for later action requests. It remains a normal repeated `reference_files` part; multipart order and endpoint behavior are unchanged.
- Ordinary editable layers expose separate `Create Multi-Direction Animation` and `Create Single-Direction Animation` commands. An existing `SpriteSheetLayer` opens the `MultiDirectionAnimationDialog` with its dedicated `MultiDirectionAnimationEditor`, and an existing `SingleDirectionAnimationLayer` opens the `SingleDirectionAnimationDialog` with its dedicated `SingleDirectionAnimationEditor`. The two editors have separate creation and frame-navigation logic; shared low-level preview controls do not merge their data models. The create commands no longer check the layer name. Manual grid extraction is local and sends no API request; optional AI analysis uses `/api/chat` with the provider selected in the dialog. Grid mode allows independent row/column counts, cell size, offsets, and gaps. Changing the grid row or column count recomputes the matching cell dimension, keeps an uncustomized output canvas in sync, and selects every new grid frame by default. AI mode creates one frame for each returned item and uses its bounding box, including for sparse sheets with empty grid positions. A successful analysis stores its strongly typed split data on the source layer so reopening the dialog restores the analysis. Both modes share output-canvas sizing, frame visibility, X/Y placement, preview dragging, and guides. Character registration correction is enabled by default for manual grids; grid frames default to the crop center and bottom edge, while AI analysis preserves foot-anchor-relative frame positions. Both modes reposition frames immediately when the dialog's output canvas width or height changes. Multi-direction creation keeps the source and creates or updates one sibling `SpriteSheetLayer`; single-direction creation creates or updates one sibling `SingleDirectionAnimationLayer`. Editing an existing animation layer preserves its other animation data. The main canvas renders visible frames from the first action and, for multi-direction data, the first direction. Previous/next buttons switch frames within the editor; multi-direction navigation applies canonical direction ordering, while single-direction navigation follows its sequential frame order, wraps at the ends, and skips hidden frames when at least two frames are visible.
- SpriteSheetLayer frame pixels are read-only in the main canvas, so cutout and other pixel-edit commands are disabled for the specialized layer.
- Spritesheet generation rules are loaded from `config/spritesheet-prompts/direction-sheet.txt`; the file contains the fixed eight directions, canonical order, shared character rules, forbidden content, and the white background rule. Each action still has its own file under `actions/`. The assembled prompt remains editable and is sent unchanged. `config/sprite-segmentation-prompt.txt` remains separate for the optional `/api/chat` JSON analysis step.
- For background cleanup and cutout, the client uploads the selected layer's actual surface dimensions instead of the document canvas size, creates result layers at those same dimensions, and normalizes the returned image back to the layer size. Image split uses the selected layer's actual dimensions to calculate the closest supported generation request size, previews the same centered aspect-preserving scale and padding that will be uploaded, and creates the child result at the original source dimensions. If the source size is unsupported, the client offers the closest smaller and larger provider sizes, GPT Image custom validation, the padding mode, and the generation cost before submission.

### SpriteSheetLayer document model

- Animation output is one childless SpriteSheetLayer below the attempt; source-sheet remains a separate source layer.
- The layer stores ordered actions, directions, and `AnimationFrameSequenceData` sequences internally. Each `AnimationFrameData` has an embedded PNG, frame index, X/Y placement, and visibility; action data stores the output canvas dimensions.
- Recreating the current attempt replaces the existing layer data in place. Adding directions to another attempt merges by (action, direction, frame index) and replaces duplicate keys.
- The layer-list thumbnail is the first action, first direction, lowest-index frame. The canvas renders visible frames from that first direction, while the canvas only permits translating the whole layer. Pixel edits and non-translation transforms are disabled.
- .pinta format version 5 stores frame PNGs at generated paths such as spritesheets/layer-0001/frame-0000.png; action and direction names are never used in archive paths. Document-size changes recompute the center/bottom anchor without resizing frame PNGs, preserving the user translation offset.

### Single-direction animation workflow

- The `Single-Direction Animation` menu is separate from multi-direction spritesheet generation. It treats the generated or selected input as one direction and uses the fixed direction ID `default`.
- One request generates one prompt-driven sequence attempt under `single-direction-animation/actions/prompt/attempt-NN/source-sequence` and adds a `SingleDirectionAnimationLayer` output. The editor starts in AI analysis mode so the generated prompt determines the number and bounds of frames; the fixed internal frame metadata is only a fallback for the shared editor model.
- The runtime data shape is `SingleDirectionAnimationLayer -> Action -> AnimationFrameSequenceData -> AnimationFrameData`. It intentionally has no `Directions` collection. Multi-direction data remains `SpriteSheetLayer -> Action -> Direction -> AnimationFrameSequenceData -> AnimationFrameData`.
- Both animation outputs share frame extraction, frame preview, output-canvas sizing, position editing, visibility, thumbnails, history, and read-only canvas behavior. The single-direction editor hides direction selection and direction-merge controls; the multi-direction editor retains them.
- The canvas and layer tree identify both types through `AnimationOutputLayer`. A single-direction layer is an unexpandable leaf, shows the first action's first frame as its thumbnail, and opens the single-direction editor from its context menu.
- `.pinta` version 5 stores single-direction frames at `single-direction-animations/{layer-id}/frame-XXXX.png`, with `Kind: "single-direction-animation"`, `SingleDirectionId`, and independent `SingleDirectionAnimations` manifest data. No conversion is performed between the two animation layer types.

There are no new HTTP endpoints for animation layers. AI generation continues to use `POST /api/images` and optional frame analysis continues to use `POST /api/chat`; animation mode, action, direction, grid, and output-layer metadata are client-side data.
