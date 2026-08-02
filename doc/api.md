# API Documentation

Default API server: `http://localhost:8080/`

## Usage Costs

The server deducts user balance according to `config/usageCosts.json` in the API server.

- `/api/images/jobs:thumbnail`: default cost `1`
- `/api/images/jobs:grayscale`: default cost `1`
- `/api/images/jobs:invert`: default cost `1`
- `/api/images`: default cost configured by the server
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

## Character Border Recognition

All requests in this section include `Authorization: Bearer <token>` when a token is saved.

### Create Job

- Method: `POST`
- Path: `/api/jobs`
- Content type: `multipart/form-data`
- Form fields:
  - `file`: PNG image, sent as `pinta.png`
- Response JSON:

```json
{
  "job_id": "job-id"
}
```

### Create Part

- Method: `POST`
- Path: `/api/jobs/{job_id}/parts`
- Content type: `application/json`
- Request JSON:

```json
{
  "name": "Detected Border",
  "segment_prompt": "object",
  "box": [0, 0, 100, 100],
  "part_type": "other"
}
```

- Response JSON:

```json
{
  "part": {
    "image_url": "/path/to/image.png",
    "mask_url": "/path/to/mask.png"
  }
}
```

### Download Generated Images

- Method: `GET`
- Path: value returned by `image_url` or `mask_url`
- Response: PNG bytes

## Image Editing

All requests in this section include `Authorization: Bearer <token>`.

### Generate Image

- Method: `POST`
- Path: `/api/images`
- Content type: `multipart/form-data`
- Form fields:
  - `reference_files`: optional repeated PNG files used as visual references; no files are required for text-to-image generation
  - `prompt`: image prompt text
  - `provider`: provider ID (`agnes`, `zzswitch`, or `lukyface`); omitted values use the server default
  - `size`: concrete output size, for example `1024x1024`; the server maps or validates it according to the provider's `imageApi`
- Response: `202 Accepted` with an image job object containing `id`, `status`, `operation`, and job metadata.
- Client behavior: polls `GET /api/images/jobs/{id}` while status is `queued` or `processing`. When status is `completed`, it reads `GET /api/images/jobs/{id}/result`.
- Results return `operation`, `prompt`, `provider`, `model`, `size`, `result_url`, and `result_b64_json`, plus the provider-specific raw response.
- When images are supplied, their multipart order is preserved. The first image is the primary edit image and subsequent images are additional references.
- Agnes omits its upstream image field when no references are supplied. GPT Image calls its generation endpoint with no references and its edit endpoint with one or more references.
- Agnes requests use the closest configured Agnes resolution that can contain the source image. Supported resolutions are the configured `1K` through `4K` sizes for ratios `1:1`, `3:4`, `4:3`, `16:9`, `9:16`, `2:3`, `3:2`, and `21:9`.
- GPT Image requests use dimensions divisible by `16`, between `655360` and `8294400` pixels, with a maximum edge of `3840` and a maximum aspect ratio of `3:1`.
- Failed jobs return `status: failed` and an `error_message` from the job status endpoint.
- Blank prompts are rejected with `400 Bad Request` before the job is queued or balance is deducted.

The client stores background prompts in `config/gpt-image-prompts.json` and reads the white-background prompt when opening the cleanup dialog. The prompt is editable and the submitted value is sent unchanged.

## Chat and Sprite Analysis

All requests in this section include `Authorization: Bearer <token>`.

### Provider Catalog

- Method: `GET`
- Path: `/api/providers`
- Auth: none
- Response: an array containing only `id`, `name`, `supports_chat`, and `supports_image` for each configured provider. Credentials, URLs, and model configuration are never returned.
- Client behavior: Pinta refreshes this catalog at startup and caches the last successful response. Chat features show only providers with `supports_chat=true`; image features can use the same catalog filtered by `supports_image=true`.

### Create Chat Job

- Method: `POST`
- Path: `/api/chat`
- Content type: `application/json`
- Request JSON fields:
  - `text`: required prompt sent to the selected provider's `/chat/completions` endpoint
  - `image_base64`: optional ordered array of Base64-encoded JPEG, PNG, or WebP images
  - `provider`: optional provider ID (`agnes`, `zzswitch`, or `lukyface`); omitted values use the server default
- Response: `202 Accepted` with an image job object. The client polls the standard image job endpoints.
- Result JSON includes `operation`, `provider`, `model`, `text`, `finish_reason`, and `usage`.
- `/api/chat` is a generic pass-through: it does not select prompts, request a JSON schema, or parse model text. Smart spritesheet analysis lists the catalog entries with `supports_chat=true`, persists that selection independently from image generation settings, loads its prompt text from `config/sprite-segmentation-prompt.txt`, appends the uploaded PNG's exact dimensions, and sends that PNG as the first `image_base64` array item. The analysis prompt explicitly allows non-grid layouts and independent per-frame bbox sizes: the model returns only absolute source-image `bbox` rectangles and `foot_anchor` points, while Pinta performs the crop and output placement. If the source PNG exceeds 5 MiB, Pinta proportionally downsizes it before upload, changes the prompt dimensions to the uploaded size, and restores returned `bbox` and `foot_anchor` coordinates to the original source dimensions before validation and cropping. Pinta saves each request and response under `ai-sprite-segmentation-logs/<timestamp>/` in the application directory (`request.png`, `request.json`, `accepted.json`, `status.json`, and `result.json`); `request.json` includes the selected `provider`. It extracts the first complete JSON object from the returned text, allowing a model preamble or Markdown code fence, then requires the reported `image_width` and `image_height` to match the uploaded PNG and validates the returned sprite count, unique indices, bounding boxes, and foot anchors before using each AI-returned `items[].bbox` as a source crop. `foot_anchor` must be an object containing numeric `x` and `y`; other shapes are rejected. AI analysis does not request or consume grid dimensions or cell positions; the returned `items` are authoritative and sorted by `index`. It keeps the configured output canvas unchanged and preserves foot-anchor-relative placement inside the output parent.

## Baidu Human Segmentation

All client requests in this section include `Authorization: Bearer <token>`. Baidu credentials exist only on the API server in `config/baiduConfig.json`. `BAIDU_CONFIG_PATH` optionally selects another config file.

### Create Cutout Job

- Method: `POST`
- Path: `/api/baidu-images`
- Content type: `multipart/form-data`
- Form fields:
  - `file`: PNG image, sent as `pinta.png`
- Response: `202 Accepted` with an image job object. The client polls the standard image job endpoints.
- Result JSON:
  - `operation`: `baidu_cutout`
  - `provider`: `baidu`
  - `result_b64_json`: Base64-encoded transparent PNG returned from Baidu's `foreground` field
  - `person_num`: number of detected people
- Server behavior: obtains and caches the Baidu access token, sends `type=foreground` to Baidu, and never exposes Baidu credentials or access tokens to the client.
- Limits: the server accepts JPEG and PNG; after Base64 and URL encoding the request image must not exceed 4 MB; shortest edge is at least 50 px and longest edge is at most 4096 px. Invalid input is rejected before balance deduction.
- Scope: this endpoint segments people. It is not a general object cutout endpoint.

### Client Background Cleanup and Cutout Flow

- Each layer row has one cutout button. Clicking it opens AI Request Settings with `agnes`, `gpt-image`, and `baidu` as peer services.
- Agnes and GPT Image show `生成白图` and `抠图` operations. Baidu shows only `抠图`.
- Operation buttons immediately save the selected service and close the dialog; there are no separate Cancel or Save buttons.
- White-image generation opens the additional prompt/reference dialog, sends the selected layer and references to Agnes or GPT Image, and creates a white-background layer.
- Agnes/GPT cutout treats the selected layer as the white-background input, generates a black-background image, and diffs the pair into a transparent layer.
- Baidu cutout sends the selected layer to `/api/baidu-images` and creates one transparent layer from `result_b64_json`.
- The GPT provider setting is shown only for `gpt-image`.
- Agnes and GPT Image both call `/api/images` with repeated `reference_files`, `prompt`, `size`, and `provider`; the client selects sizes according to the selected provider's constraints.
- The layer toolbar also provides AI image generation. It accepts a required prompt, a service-specific output size, and optional reference layers/files, then adds the result as a new undoable layer in the current document. GPT Image offers the standard, common-aspect, 2K, and 4K presets from ImageLayer plus custom width and height values; custom values are validated against the GPT Image constraints above before submission.
- The layer toolbar provides a separate spritesheet generator that uses the same `/api/images` multipart contract; spritesheet fields are client-side metadata and are not additional API form fields. Both direction-sheet and action modes always generate the fixed eight directions in canonical clockwise order, with a white background. Action mode combines one action and one shared frame count for every direction into a single row-major spritesheet prompt.
- A successful spritesheet request stays in the active document and creates `spritesheet/<action>/attempt-NN/source-sheet` in the authoritative layer tree. The attempt stores generation type, action ID, ordered direction IDs, frame count, grid, size, background ID, and final prompt in the `.pinta` layer metadata. Network failure, cancellation, or invalid PNG data creates no attempt.
- After the user marks an approved direction-sheet `source-sheet` as the character anchor, it is selected by default as `character-anchor.png` for later action requests. It remains a normal repeated `reference_files` part; multipart order and endpoint behavior are unchanged.
- Ordinary editable layers expose `Create Animation Frames`; an existing `SpriteSheetLayer` exposes `Edit Animation Frames`. The create command no longer checks the layer name. Manual grid extraction is local and sends no API request; optional AI analysis uses `/api/chat` with the provider selected in the dialog. Grid mode allows independent row/column counts, cell size, offsets, and gaps. AI mode creates one frame for each returned item and uses its bounding box, including for sparse sheets with empty grid positions. A successful analysis stores its strongly typed split data on the source layer so reopening the dialog restores the analysis. Both modes share output-canvas sizing, frame visibility, X/Y placement, preview dragging, and guides. Character registration correction is enabled by default for manual grids; grid frames default to the crop center and bottom edge, while AI analysis preserves foot-anchor-relative frame positions. Both modes reposition frames immediately when the dialog's output canvas width or height changes. Creating frames from an ordinary layer keeps the source and creates or updates one sibling SpriteSheetLayer. Editing an existing SpriteSheetLayer opens every direction and frame from its first action without flattening or replacing other animation data. The main canvas renders visible frames from its first action and first direction. Previous/next frame navigation follows the canonical clockwise direction order, wraps at the ends, and skips hidden frames when at least two frames are visible.
- SpriteSheetLayer frame pixels are read-only in the main canvas, so cutout and other pixel-edit commands are disabled for the specialized layer.
- Spritesheet generation rules are loaded from `config/spritesheet-prompts/direction-sheet.txt`; the file contains the fixed eight directions, canonical order, shared character rules, forbidden content, and the white background rule. Each action still has its own file under `actions/`. The assembled prompt remains editable and is sent unchanged. `config/sprite-segmentation-prompt.txt` remains separate for the optional `/api/chat` JSON analysis step.
- For background cleanup and cutout, the client uploads the selected layer's actual surface dimensions instead of the document canvas size, and creates the result layers at those same dimensions. If that layer size is unsupported, the client chooses the closest supported size that can contain it, pads the source image equally on each side to center it, and center-crops the result back to the layer size. It only resizes as a fallback when a provider returns an unexpected size.

### SpriteSheetLayer document model

- Animation output is one childless SpriteSheetLayer below the attempt; source-sheet remains a separate source layer.
- The layer stores ordered actions, directions, and frames internally. Each frame has an embedded PNG, frame index, X/Y placement, visibility, and the output canvas dimensions.
- Recreating the current attempt replaces the existing layer data in place. Adding directions to another attempt merges by (action, direction, frame index) and replaces duplicate keys.
- The layer-list thumbnail is the first action, first direction, lowest-index frame. The canvas renders visible frames from that first direction, while the canvas only permits translating the whole layer. Pixel edits and non-translation transforms are disabled.
- .pinta format version 4 stores frame PNGs at generated paths such as spritesheets/layer-0001/frame-0000.png; action and direction names are never used in archive paths. Document-size changes recompute the center/bottom anchor without resizing frame PNGs, preserving the user translation offset.
