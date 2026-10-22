# Fetchit Transcription Infrastructure

## System Overview

This document describes the full transcription pipeline — how phone call recordings are fetched, stored, transcribed, analyzed, and delivered as alerts. All services run in a shared Kubernetes cluster (`schultzregistry.azurecr.io`) and communicate over internal cluster DNS.

```
Dashmanager/Skyswitch ──┐              ┌── Coredial/CoreNexa
                        ▼              ▼
                 Fetchit.GetRecordings.API
                        │
              GET /v1/recordings/search
              POST /v1/recordings/download
                        │
                        ▼
            Fetchit.Transcription.Service  ◄── DB (fetchit-orion): TranscriptionConfigs, Licenses
                   │          │
         ┌─────────┘          └──────────┐
         ▼                               ▼
  Fetchit.Storage.Service         Fetchit.STT.API
  POST /api/files (upload .wav)     POST /v1/transcribe
  Returns temporary URL                  │
         │                     ┌─────────┴─────────┐
         │                     ▼                   ▼
         │             Fetchit.Whisper.Stt      Knowlez
         │             (self-hosted, GPU)    (external SaaS)
         │
         └───── Storage URL passed to STT.API ─────┘
                        │
                        ▼
            Transcription JSON returned
                        │
               ┌────────┴────────┐
               ▼                 ▼
        Azure OpenAI         DB write
     (sentiment/summary)   (Transcriptions table)
               │
               ▼
         Alert delivery
      (webhook / email via MS Graph)
```

## Services

### Fetchit.Transcription.Service (this repo)

**Language:** C# / .NET 8 Worker Service  
**Role:** Orchestrator — the central polling loop that drives the entire pipeline.

**How it works:**
1. `Worker.cs` runs a `BackgroundService` polling every 15 minutes (configurable via `PollIntervalMinutes`).
2. Each cycle, `TranscriptionOrchestrator.ProcessAllClientsAsync` loads all enabled `TranscriptionConfig` rows from the `fetchit-orion` DB.
3. For each client config:
   - Checks for a valid `Transcriptions` product license via the `Licenses`/`Products` tables.
   - Calls **GetRecordings.API** to search for recordings in the time window (`LastPolledAt` → now, or backfill range).
   - Downloads each recording's .wav bytes via **GetRecordings.API**.
   - Deduplicates by SHA-256 hash of the audio (`AudioHash` column).
   - Uploads .wav to **Storage.Service**, gets a temporary URL.
   - Sends the URL to **STT.API** for transcription (authenticated with the license key).
   - Optionally runs **Azure OpenAI** analysis (sentiment 0-100, 2-4 sentence summary) via `TranscriptionAnalysisService`.
   - Stores the result in the `Transcriptions` table.
   - Fires alerts via `TranscriptionAlertService`: webhook (curl template evaluated via Roslyn scripting) and/or email (MS Graph API).
4. Backfill mode: when `BackfillRequested` is set on a config, it processes a specific date range and also runs analysis on existing transcriptions missing sentiment/summary.
5. Consecutive failure circuit breaker: stops a client's batch after 5 failures or a 503 from STT.API.

**Key config (appsettings.json):**
- `PostgresConnectionString` — fetchit-orion DB
- `GetRecordingsApiUrl` — internal cluster URL to GetRecordings.API
- `SttApiUrl` — internal cluster URL to STT.API
- `StorageApiUrl` / `StorageApiKey` — internal cluster URL to Storage.Service
- `AzureOpenAI:Endpoint` / `ApiKey` / `DeploymentName` — Azure OpenAI for analysis
- `MicrosoftGraphAPI:EmailSettings` — MS Graph credentials for email alerts

**Deployment:** Docker image → `schultzregistry.azurecr.io/fetchit-transcription-service:latest`. CI pushes on `v*` tags. Kubernetes manifest in `Kubernetes.Configurations/`. Config injected via ConfigMap mounted at `/app/config`.

---

### Fetchit.GetRecordings.API

**Repo:** `Fetchit.GetRecordings.API`  
**Language:** C# / ASP.NET Core Web API (.NET 8)  
**Role:** Abstraction layer over phone system recording APIs.

**What it does:**
- `POST /v1/recordings/search` — Searches for call recordings by date range. Dispatches to the correct provider based on the `service` field: **DashManager** (Skyswitch) via OAuth2 + CDR queries, or **ClearVoice/CoreNexa** via JWT + Oreka search.
- `POST /v1/recordings/download` — Downloads a specific recording file by URL/ID, proxying through the appropriate provider's download endpoint.

**Connections:**
- **Upstream:** Called by Fetchit.Transcription.Service (steps 3-5 in the diagram).
- **Downstream:** DashManager API (`23917-hpbx.dashmanager.com`), CoreNexa API (`api.corenexa.com`, `m.accessapi.com`).

**Key to know:** Phone system credentials (username, password, domain) come from the `TranscriptionConfig.Settings` JSON column, passed through from the Transcription Service per-request.

---

### Fetchit.STT.API

**Repo:** `Fetchit.STT.API`  
**Language:** C# / ASP.NET Core Web API (.NET 8)  
**Role:** Speech-to-text gateway with fallback logic.

**What it does:**
- `POST /v1/transcribe` — Accepts `audio_url` or `audio_base64`, returns transcription JSON with text + segments + optional speaker labels. Bearer-token protected (license key).
- Uses a `FallbackSttService`: tries **Knowlez** (external SaaS STT) first, then falls back to the self-hosted **Whisper** instance.
- For Knowlez, if audio is provided as base64, it first uploads to **Storage.Service** to get a URL (Knowlez needs a reachable URL).

**Connections:**
- **Upstream:** Called by Fetchit.Transcription.Service (step 7.1 in the diagram).
- **Downstream:** Knowlez API (`/v1/stt/transcribe`), Fetchit.Whisper.Stt (`/v1/audio/transcriptions`), Fetchit.Storage.Service (`/api/files`).

**Key to know:** The fallback order matters — Knowlez is preferred (faster, higher quality for supported languages), Whisper is the reliable self-hosted backup.

---

### Fetchit.Storage.Service

**Repo:** `Fetchit.Storage.Service`  
**Language:** C# / ASP.NET Core Web API (.NET 8)  
**Role:** Temporary file storage with auto-cleanup.

**What it does:**
- `POST /api/files` — Upload a file (up to 100MB), returns a URL + expiry. Protected by `X-API-Key` header.
- `GET /files/{id}/{filename}` — Serve stored files (public static file serving).
- `DELETE /api/files/{id}` — Manual deletion.
- `FileCleanupService` background worker deletes files after a configurable TTL (default 60 minutes).

**Connections:**
- **Upstream:** Called by Fetchit.Transcription.Service (step 6) and Fetchit.STT.API (step 7.2).
- **Downstream:** None — serves files to anyone with the URL (Knowlez, Whisper, etc.).

**Key to know:** Files are ephemeral. The 60-minute TTL means transcription must complete before the audio URL expires. GUID-based file IDs prevent enumeration.

---

### Fetchit.Whisper.Stt

**Repo:** `Fetchit.Whisper.Stt`  
**Language:** Python / FastAPI + faster-whisper (CTranslate2) + sherpa-onnx  
**Role:** Self-hosted speech-to-text engine.

**What it does:**
- `POST /v1/audio/transcriptions` — OpenAI-compatible transcription endpoint. Accepts audio files, returns text with segments and timestamps.
- Uses **faster-whisper** (CTranslate2 optimized Whisper) for transcription with configurable model size and beam search.
- Optional **speaker diarization** via sherpa-onnx ONNX models.
- Supports word-level timestamps, language detection, and translation to English.
- Includes OpenTelemetry tracing.

**Connections:**
- **Upstream:** Called by Fetchit.STT.API's `WhisperSttService` as the fallback transcription backend.
- **Downstream:** None.

**Key to know:** Runs with GPU support (CUDA) when available via `docker-compose.cuda.yml`. This is the compute-heavy component — resource limits matter. Model files are loaded at startup.

---

### Knowlez (external)

**Role:** External SaaS speech-to-text provider.  
**Endpoint:** `POST /v1/stt/transcribe`  
**Key to know:** Requires a publicly reachable audio URL (hence the Storage.Service dependency). Preferred over Whisper when available. Not self-hosted — availability is outside our control.

---

### Fetchit.Orion

**Repo:** `Fetchit.Orion`  
**Language:** C# / ASP.NET Core MVC + Razor Pages + Auth0  
**Role:** Management portal — the admin UI where transcription configs are created and managed.

**What it does:**
- Full web app for managing veterinary clinic clients, licensing, PMS integrations, and infrastructure.
- The `TranscriptionConfig` rows that drive the Transcription Service are created/edited here.
- Product licenses (including the `Transcriptions` product) are managed here.
- Uses EF Core with the `FetchitOrionContext` against the `fetchit-orion` PostgreSQL database.

**Connections:**
- **Database:** Owns the `fetchit-orion` DB schema that all other services read from.
- **Downstream services:** Manages Kubernetes deployments via KubeBridge, MQTT messaging, PMS integrations.

**Key to know:** This is where the `TranscriptionConfig` settings (phone system type, credentials, sentiment/summary toggles, webhook templates, email addresses, backfill requests) are configured. If transcription isn't working for a client, check the config here first.

---

### Database (fetchit-orion, PostgreSQL)

**Host:** `20.124.201.192:5432`  
**User:** `fetchit_orion`  
**ORM:** EF Core via `DataAccess.PostgresEntityFramework` (shared NuGet package from `SchultzTechnology` GitHub Packages)

**Key tables for transcription:**
- `TranscriptionConfigs` — Per-client settings: enabled, phone system type, credentials (JSON), sentiment/summary toggles, webhook/email config, backfill state, `LastPolledAt`.
- `Transcriptions` — Stored results: transcript JSON, audio hash, caller number, timestamps, sentiment score, summary text.
- `Licenses` / `Products` — License validation: client must have an active `Transcriptions` product license.

---

## Deployment & CI

All services use the same pattern:
1. Push a `v*` tag to trigger GitHub Actions.
2. Docker image built and pushed to `schultzregistry.azurecr.io`.
3. Kubernetes manifests applied (`kubectl apply`).
4. Deployment rolled out (`kubectl rollout restart`).

**Shared NuGet packages** (e.g., `DataAccess.PostgresEntityFramework`) are hosted on GitHub Packages (`nuget.pkg.github.com/SchultzTechnology`). Docker builds require `NUGET_AUTH_TOKEN` to restore them.

**Cluster DNS pattern:** Services communicate via `http://{service-name}.{namespace}.svc.cluster.local:{port}`.

## Debugging Tips

- **Transcription not running for a client?** Check `TranscriptionConfigs.Enabled` and that a `Transcriptions` license exists in Orion.
- **Recordings not found?** Verify the phone system credentials in `TranscriptionConfig.Settings` JSON and that `GetRecordingsApiUrl` is reachable.
- **STT returning errors?** Check if Knowlez is down (503 triggers fallback), then check Whisper pod health/GPU availability.
- **Duplicate transcriptions?** The `AudioHash` dedup should prevent this. Check for hash collisions or config pointing at overlapping time ranges.
- **Email alerts not sending?** Verify MS Graph credentials and that `EmailEnabled` + `EmailAddress` are set on the config.
- **Backfill stuck?** If the service crashes mid-backfill, `BackfillRequested` stays true and retries next cycle. Check logs for consecutive failure circuit breaker.
