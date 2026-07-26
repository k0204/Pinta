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
- Response JSON includes `operation`, `prompt`, `size`, `ratio`, `resolution`, `result_url`, `result_b64_json`, and `agnes_response`. The server requests `b64_json` from Agnes for this endpoint so Pinta does not need to download private external image URLs.

### Generate Black Background

- Method: `POST`
- Path: `/api/agnes-images/black-background`
- Content type: `multipart/form-data`
- Form fields:
  - `file`: PNG image, sent as `pinta.png`
  - `size`: Agnes image size, one of `1K`, `2K`, `3K`, or `4K`
  - `ratio`: Agnes image ratio, one of `1:1`, `3:4`, `4:3`, `16:9`, `9:16`, `2:3`, `3:2`, or `21:9`
- Response JSON includes `operation`, `prompt`, `size`, `ratio`, `resolution`, `result_url`, `result_b64_json`, and `agnes_response`. The server requests `b64_json` from Agnes for this endpoint so Pinta does not need to download private external image URLs.

### Generate Validated Cutout Background Pair

- Method: `POST`
- Path: `/api/agnes-images/cutout-backgrounds`
- Content type: `multipart/form-data`
- Form fields:
  - `file`: PNG image, sent as `pinta.png`
  - `size`: Agnes image size, one of `1K`, `2K`, `3K`, or `4K`
  - `ratio`: Agnes image ratio, one of `1:1`, `3:4`, `4:3`, `16:9`, `9:16`, `2:3`, `3:2`, or `21:9`
- Response JSON includes `white_result_b64_json`, `black_result_b64_json`, `prompt`, `size`, `ratio`, `resolution`, and `agnes_response`.
- Server behavior: generates the white-background image and black-background image from the original upload, then returns both images without pixel comparison.

## GPT Image Background Cutout

All requests in this section include `Authorization: Bearer <token>`.

The client stores background prompts in `config/gpt-image-prompts.json` and rereads the file immediately before each white- or black-background request, so prompt changes do not require restarting Pinta.

### Generate Image

- Method: `POST`
- Path: `/api/gpt-images`
- Content type: `multipart/form-data`
- Form fields:
  - `file`: PNG image, sent as `pinta.png`
  - `size`: concrete output size, for example `1024x1024`
  - `prompt`: image prompt text
- Response: `202 Accepted` with an image job object containing `id`, `status`, `operation`, and job metadata.
- Client behavior: polls `GET /api/images/jobs/{id}` while status is `queued` or `processing`. When status is `completed`, it reads `GET /api/images/jobs/{id}/result`.
- Result JSON includes `operation`, `prompt`, `provider`, `model`, `quality`, `size`, `result_url`, `result_b64_json`, and `gpt_response`.
- Failed jobs return `status: failed` and an `error_message` from the job status endpoint.

### Client Cutout Flow

- The layer row `抠图` button renders the selected layer to PNG.
- The client sends that PNG with the white prompt to `/api/gpt-images` first and saves the returned image.
- The client then sends the white image with the black prompt to `/api/gpt-images`.
- The client diffs the saved white and black results to create a new transparent layer after both images are available.
