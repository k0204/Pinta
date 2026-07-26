# API Documentation

Default API server: `http://101.42.29.148:8080/`

## Usage Costs

The server deducts user balance according to `config/usageCosts.json` in the API server.

- `/api/images/jobs:thumbnail`: default cost `1`
- `/api/images/jobs:grayscale`: default cost `1`
- `/api/images/jobs:invert`: default cost `1`
- `/api/agnes-images/white-background`: default cost `10`
- `/api/agnes-images/black-background`: default cost `8`
- `/api/agnes-images/cutout-backgrounds`: default cost `18`
- `/api/gpt-images`: default cost configured by the server

Requests with insufficient balance return `402 Payment Required`.

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

## Agnes Background Cutout

All requests in this section include `Authorization: Bearer <token>`.

### Generate White Background

- Method: `POST`
- Path: `/api/agnes-images/white-background`
- Content type: `multipart/form-data`
- Form fields:
  - `file`: PNG image, sent as `pinta.png`
  - `size`: Agnes image size, one of `1K`, `2K`, `3K`, or `4K`
  - `ratio`: Agnes image ratio, one of `1:1`, `3:4`, `4:3`, `16:9`, `9:16`, `2:3`, `3:2`, or `21:9`
- Response: `202 Accepted` with an image job object. Poll `GET /api/images/jobs/{id}` and read `GET /api/images/jobs/{id}/result` when completed.
- Result JSON includes `operation`, `prompt`, `size`, `ratio`, `resolution`, `result_url`, `result_b64_json`, and `agnes_response`. The server requests `b64_json` from Agnes for this endpoint so Pinta does not need to download private external image URLs.

### Generate Black Background

- Method: `POST`
- Path: `/api/agnes-images/black-background`
- Content type: `multipart/form-data`
- Form fields:
  - `file`: PNG image, sent as `pinta.png`
  - `size`: Agnes image size, one of `1K`, `2K`, `3K`, or `4K`
  - `ratio`: Agnes image ratio, one of `1:1`, `3:4`, `4:3`, `16:9`, `9:16`, `2:3`, `3:2`, or `21:9`
- Response: `202 Accepted` with an image job object. Poll `GET /api/images/jobs/{id}` and read `GET /api/images/jobs/{id}/result` when completed.
- Result JSON includes `operation`, `prompt`, `size`, `ratio`, `resolution`, `result_url`, `result_b64_json`, and `agnes_response`. The server requests `b64_json` from Agnes for this endpoint so Pinta does not need to download private external image URLs.

### Generate Validated Cutout Background Pair

- Method: `POST`
- Path: `/api/agnes-images/cutout-backgrounds`
- Content type: `multipart/form-data`
- Form fields:
  - `file`: PNG image, sent as `pinta.png`
  - `size`: Agnes image size, one of `1K`, `2K`, `3K`, or `4K`
  - `ratio`: Agnes image ratio, one of `1:1`, `3:4`, `4:3`, `16:9`, `9:16`, `2:3`, `3:2`, or `21:9`
- Response: `202 Accepted` with an image job object containing `id`, `status`, `operation`, and job metadata.
- Client behavior: polls `GET /api/images/jobs/{id}` while status is `queued` or `processing`. When status is `completed`, it reads `GET /api/images/jobs/{id}/result`.
- Result JSON includes `white_result_b64_json`, `black_result_b64_json`, `prompt`, `size`, `ratio`, `resolution`, and `agnes_response`.
- Server behavior: generates the white-background image and black-background image from the original upload, then returns both images without pixel comparison.

## GPT Image Background Cutout

All requests in this section include `Authorization: Bearer <token>`.

The client stores background prompts in `config/gpt-image-prompts.json` and reads the file at the start of each cutout run, so prompt changes apply to the next cutout.

### Generate Image

- Method: `POST`
- Path: `/api/gpt-images`
- Content type: `multipart/form-data`
- Form fields:
  - `file`: PNG image, sent as `pinta.png`
  - `size`: concrete output size, for example `1024x1024`
  - `provider`: GPT image provider ID, currently `zzswitch` or `lukyface`; if omitted, the server uses its configured default provider
  - `prompt`: image prompt text
- Response: `202 Accepted` with an image job object containing `id`, `status`, `operation`, and job metadata.
- Client behavior: polls `GET /api/images/jobs/{id}` while status is `queued` or `processing`. When status is `completed`, it reads `GET /api/images/jobs/{id}/result`.
- Result JSON includes `operation`, `prompt`, `provider`, `model`, `quality`, `size`, `result_url`, `result_b64_json`, and `gpt_response`.
- Failed jobs return `status: failed` and an `error_message` from the job status endpoint.

### Client Cutout Flow

- The layer row cutout button renders the selected layer to PNG.
- The AI Request Settings dialog selects `agnes` or `gpt-image`; GPT Image also selects `zzswitch` or `lukyface` as the request `provider`.
- With Agnes selected, the client sends one request to `/api/agnes-images/cutout-backgrounds` using `2K` and the closest supported aspect ratio.
- With GPT Image selected, the client sends the source PNG with the white prompt to `/api/gpt-images`, then sends the white result with the black prompt to the same endpoint. Both requests include the selected `provider`.
- The client resizes returned images to the document size when needed.
- The client diffs the saved white and black results for alpha, applies that alpha to the original layer pixels, and creates a new transparent layer after both images are available. AI output is not used for foreground color.
