# Fetchit.Storage.Service

Temporary file storage service for audio files. Provides short-lived public URLs that auto-expire.

**Domain:** `cdn.fetchitdata.cloud`

## Purpose

- Accept audio file uploads (via API key) and return a public URL
- Serve files publicly so external services (Knowlez STT, clients) can download them
- Auto-delete expired files based on a configurable TTL

## Endpoints

### Upload a file

```
POST https://cdn.fetchitdata.cloud/api/files
Headers:
  X-API-Key: <your-api-key>
  Content-Type: multipart/form-data
Body:
  file: <binary file>
```

**Response (200):**
```json
{
  "url": "https://cdn.fetchitdata.cloud/files/a1b2c3d4e5f6/recording.wav",
  "expiresAt": "2026-08-27T15:30:00Z",
  "id": "a1b2c3d4e5f6",
  "filename": "recording.wav",
  "size": 1048576
}
```

### Download a file (public, no auth)

```
GET https://cdn.fetchitdata.cloud/files/{id}/{filename}
```

Returns the file directly. No authentication required.

### Delete a file

```
DELETE https://cdn.fetchitdata.cloud/api/files/{id}
Headers:
  X-API-Key: <your-api-key>
```

## Configuration

Set in `config/appsettings.json` or via the K8s ConfigMap:

| Key | Default | Description |
|-----|---------|-------------|
| `Storage:Path` | `/data/files` | Disk path for stored files |
| `Storage:BaseUrl` | `https://cdn.fetchitdata.cloud` | Public base URL for generated links |
| `Storage:ApiKey` | — | Required API key for upload/delete |
| `Storage:TtlMinutes` | `60` | Minutes before files are auto-deleted |
| `Storage:CleanupIntervalMinutes` | `5` | How often the cleanup job runs |

## Deployment

### Prerequisites

- Kubernetes cluster with `nginx` ingress controller
- `cert-manager` installed for ACME TLS
- DNS: `cdn.fetchitdata.cloud` → ingress external IP (Cloudflare)
- Container registry: `schultzregistry.azurecr.io`

### Build & Push

```bash
docker build -t schultzregistry.azurecr.io/fetchit-storage-service:latest .
docker push schultzregistry.azurecr.io/fetchit-storage-service:latest
```

### Deploy

1. Update the API key in `Kubernetes.Configurations/fetchit-storage-service/config.yml`
2. Apply:
```bash
kubectl apply -f Kubernetes.Configurations/fetchit-storage-service/config.yml
```
3. Add a Cloudflare DNS A record for `cdn.fetchitdata.cloud` pointing to the ingress external IP.

### Usage from Fetchit.STT.API

Upload audio → get URL → pass `audio_url` to Knowlez instead of `audio_base64`:

```csharp
// Upload to storage service
var content = new MultipartFormDataContent();
content.Add(new ByteArrayContent(audioBytes), "file", "audio.wav");
var uploadResponse = await _storageClient.PostAsync("/api/files", content);
var result = await uploadResponse.Content.ReadFromJsonAsync<UploadResult>();

// Send URL to Knowlez
var payload = new Dictionary<string, string?> { ["audio_url"] = result.Url };
var sttResponse = await _knowlezClient.PostAsJsonAsync("/v1/stt/transcribe", payload);
```

## Local Development

```bash
cd Fetchit.Storage.Service
dotnet run
```

Upload test:
```bash
curl -X POST http://localhost:5253/api/files \
  -H "X-API-Key: dev-key-123" \
  -F "file=@test.wav"
```
