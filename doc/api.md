# API Documentation

Default API server: `http://localhost:8080/`

## Usage Costs

The server deducts user balance according to `config/usageCosts.json` in the API server.

- `/api/images/jobs:thumbnail`: default cost `1`
- `/api/images/jobs:grayscale`: default cost `1`
- `/api/images/jobs:invert`: default cost `1`
- `/api/agnes-images`: default cost `18`
- `/api/gpt-images`: default cost configured by the server
- `/api/baidu-images`: default cost `1`

Requests with insufficient balance return `402 Payment Required`.

## Image Job Retention

- Uploaded source and reference images are deleted after processing.
- Completed and failed job results remain available for `IMAGE_RESULT_RETENTION_HOURS`, which defaults to `24` hours.
- Expired result files and job records are removed when the next image job starts.

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
- Response JSON includes `daily_jobs_remaining`, `monthly_jobs_remaining`, upload limits, concurrency limits, and allowed operations.
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
- Path: `/api/agnes-images` or `/api/gpt-images`
- Content type: `multipart/form-data`
- Form fields:
  - `reference_files`: optional repeated PNG files used as visual references; no files are required for text-to-image generation
  - `prompt`: image prompt text
  - `provider`: GPT provider ID (`zzswitch` or `lukyface`), sent only to `/api/gpt-images`
  - `size`: concrete output size, for example `1024x1024`; its constraints depend on the selected endpoint
- Response: `202 Accepted` with an image job object containing `id`, `status`, `operation`, and job metadata.
- Client behavior: polls `GET /api/images/jobs/{id}` while status is `queued` or `processing`. When status is `completed`, it reads `GET /api/images/jobs/{id}/result`.
- Both endpoints return `operation`, `prompt`, `provider`, `model`, `size`, `result_url`, and `result_b64_json`, plus their provider-specific raw response.
- When images are supplied, their multipart order is preserved. The first image is the primary edit image and subsequent images are additional references.
- Agnes omits its upstream image field when no references are supplied. GPT Image calls its generation endpoint with no references and its edit endpoint with one or more references.
- Agnes requests use the closest configured Agnes resolution that can contain the source image. Supported resolutions are the configured `1K` through `4K` sizes for ratios `1:1`, `3:4`, `4:3`, `16:9`, `9:16`, `2:3`, `3:2`, and `21:9`.
- GPT Image requests use dimensions divisible by `16`, between `655360` and `8294400` pixels, with a maximum edge of `3840` and a maximum aspect ratio of `3:1`.
- Failed jobs return `status: failed` and an `error_message` from the job status endpoint.
- Blank prompts are rejected with `400 Bad Request` before the job is queued or balance is deducted.

The client stores background prompts in `config/gpt-image-prompts.json` and reads the white-background prompt when opening the cleanup dialog. The prompt is editable and the submitted value is sent unchanged.

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
- Both services use repeated `reference_files` plus the same prompt and size fields, but the client selects sizes according to each service's own constraints. Only GPT Image receives a provider field.
- The layer toolbar also provides AI image generation. It accepts a required prompt, a service-specific output size, and optional reference layers/files, then opens the result as a new document at the selected size. GPT Image offers the standard, common-aspect, 2K, and 4K presets from ImageLayer plus custom width and height values; custom values are validated against the GPT Image constraints above before submission.
- The layer toolbar provides a separate spritesheet generator that uses the same `/api/agnes-images` and `/api/gpt-images` multipart contract; spritesheet fields are client-side metadata and are not additional API form fields. Direction-sheet mode combines selected canonical views with one frame per direction. Action mode combines one action, selected directions, and one shared frame count for every direction into a single row-major spritesheet prompt.
- A successful spritesheet request stays in the active document and creates `spritesheet/<action>/attempt-NN/source-sheet` in the authoritative layer tree. The attempt stores generation type, action ID, ordered direction IDs, frame count, grid, size, background ID, and final prompt in the `.pinta` layer metadata. Network failure, cancellation, or invalid PNG data creates no attempt.
- After the user marks an approved direction-sheet `source-sheet` as the character anchor, it is selected by default as `character-anchor.png` for later action requests. It remains a normal repeated `reference_files` part; multipart order and endpoint behavior are unchanged.
- Splitting is local and sends no API request. It reads the saved row-major mapping, allows explicit columns, rows, cell size, offsets, and gaps, then creates direction groups with `frame-NN` child layers. Character registration correction is enabled by default: when foreground bounds drift beyond tolerance, the client horizontally centers the foreground and aligns grounded frames to a common baseline; jump frames preserve vertical motion. Re-splitting an attempt that already has direction groups creates a new attempt and preserves the original source and frames.
- Cutout of a spritesheet frame continues to use the existing Agnes, GPT Image, or Baidu endpoint. The client sends only that frame at its cell size and creates a numbered `frame-NN-cutout-NN` sibling without replacing the original frame.
- Spritesheet prompt rules are loaded from `config/spritesheet-prompts/`: shared direction/background rules are in `common.json`, direction-sheet rules are in `direction-sheet.json`, and each action has its own file under `actions/`. The assembled prompt remains editable and is sent unchanged.
- If the document size is unsupported, the client chooses the closest supported size that can contain it, pads the source image equally on each side to center it, and center-crops the result back to the document size. It only resizes as a fallback when a provider returns an unexpected size.
