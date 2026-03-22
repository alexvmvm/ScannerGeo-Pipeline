# ScannerGeo-Pipeline

`ScannerGeo-Pipeline` is a .NET 8 reconstruction service built around a narrow, project-centric workflow:

1. Create a project.
2. Upload images or import them from URLs.
3. Validate inputs in background jobs.
4. Start a reconstruction run.
5. Download artifacts and inspect job/run status.

The solution is split into a stateless HTTP API, a background worker, shared core/domain libraries, and Postgres-backed metadata storage. Binary assets are stored in S3-compatible object storage by default, with a filesystem storage option available for tests and local wiring.

## What is in this repo

```text
Recon.Api.sln
src/
  Recon.Api/            HTTP API, Swagger, ops UI
  Recon.Worker/         Background job processor
  Recon.Core/           Application services and abstractions
  Recon.Domain/         Domain models and enums
  Recon.Infrastructure/ EF Core, storage, pipeline adapters
tests/
  Recon.Api.Tests/
  Recon.Core.Tests/
  Recon.Infrastructure.Tests/
```

## Main capabilities

- Create and list reconstruction projects
- Upload image files with size, count, and extension validation
- Import images from remote HTTP/HTTPS URLs with SSRF-style network checks
- Queue and process background jobs in Postgres
- Run a staged reconstruction pipeline: `Inspect`, `Sparse`, `Dense`, `Export`
- Store generated reports and artifacts
- Expose health endpoints, Swagger, and a simple ops page for job inspection

## Pipeline modes

The service supports two pipeline providers:

- `Simulated`
  Good default for local development and CI. Produces synthetic artifacts without requiring COLMAP.
- `Colmap`
  Uses the configured `colmap` binary for sparse, dense, and export stages. The worker validates the COLMAP runtime on startup when this mode is enabled.

## Default local endpoints

- API: `http://localhost:5216`
- Swagger UI: `http://localhost:5216/swagger`
- Ops page: `http://localhost:5216/ops/jobs.html`
- Liveness: `http://localhost:5216/health/live`
- Readiness: `http://localhost:5216/health/ready`

When running with Docker Compose:

- API: `http://localhost:8080`
- Swagger UI: `http://localhost:8080/swagger`
- MinIO API: `http://localhost:9000`
- MinIO console: `http://localhost:9001`
- Postgres: `localhost:5432`

## Configuration

Shared settings live in:

- [reconsettings.json](/C:/Projects/ScannerGeo-Pipeline/reconsettings.json)
- [reconsettings.Development.json](/C:/Projects/ScannerGeo-Pipeline/reconsettings.Development.json)
- [reconsettings.Docker.json](/C:/Projects/ScannerGeo-Pipeline/reconsettings.Docker.json)
- [.env.example](/C:/Projects/ScannerGeo-Pipeline/.env.example)

Important `Recon` settings:

- `PipelineProvider`: `Simulated` or `Colmap`
- `ColmapBinaryPath`: path to the `colmap` executable
- `ColmapUseGpu`: enables COLMAP GPU flags when `true`
- `ObjectStorageProvider`: `Minio` or `FileSystem`
- `ObjectStorageEndpoint`, `ObjectStorageBucket`, `ObjectStorageAccessKey`, `ObjectStorageSecretKey`
- `StorageRootPath`: filesystem storage root when using `FileSystem`
- `ScratchRootPath`: worker scratch directory for pipeline execution
- `MinimumValidImageCount`: minimum validated images required before starting a run

Notes:

- The API and worker both load `reconsettings*.json` from the project and solution root.
- Database initialization is automatic on startup. Postgres uses EF Core migrations guarded by an advisory lock.
- `.env.example` is geared toward COLMAP-based Docker runs. If you do not have COLMAP available, use `Simulated`.

## Running locally with Docker Compose

For the fastest local bring-up, use Compose:

```powershell
docker compose up --build
```

This starts:

- Postgres
- MinIO
- `Recon.Api`
- `Recon.Worker`

By default the Compose file uses the simulated pipeline unless overridden by environment variables.

To enable COLMAP inside the Docker images, set `RECON_INSTALL_COLMAP=true` and switch `RECON_PIPELINE_PROVIDER=Colmap`. The image build uses GCC 10 and explicit CUDA architectures for Ubuntu 22.04 compatibility.

## Running locally without Docker

Prerequisites:

- .NET 8 SDK
- PostgreSQL
- MinIO or another reachable S3-compatible endpoint
- Optional: COLMAP if you want `PipelineProvider=Colmap`

Start infrastructure, then run:

```powershell
dotnet restore
dotnet run --project src/Recon.Api
dotnet run --project src/Recon.Worker
```

Development settings expect MinIO at `http://localhost:9000`.

## Testing

```powershell
dotnet test
```

The test suite covers API endpoints, core services, storage wiring, queue behavior, image inspection, and pipeline configuration.

## API workflow

Typical happy path:

1. `POST /api/v1/projects`
2. `POST /api/v1/projects/{projectId}/images` or `POST /api/v1/projects/{projectId}/imports`
3. Wait for validation jobs to succeed
4. `POST /api/v1/projects/{projectId}/runs`
5. Inspect `GET /api/v1/projects/{projectId}/runs/{runId}`
6. Download artifacts from `GET /api/v1/projects/{projectId}/artifacts/{artifactId}`

Useful endpoints:

- `GET /api/v1/projects`
- `GET /api/v1/projects/{projectId}`
- `GET /api/v1/projects/{projectId}/images`
- `GET /api/v1/projects/{projectId}/artifacts`
- `GET /api/v1/jobs/{jobId}`
- `GET /api/v1/ops/jobs`
- `POST /api/v1/ops/jobs/{jobId}/process`
- `POST /api/v1/ops/jobs/process-next`

Example project creation:

```http
POST /api/v1/projects
Content-Type: application/json

{
  "name": "Warehouse Scan 01",
  "description": "Initial indoor capture",
  "sourceType": "scanner"
}
```

Example run request:

```http
POST /api/v1/projects/{projectId}/runs
Content-Type: application/json

{
  "stages": ["Inspect", "Sparse", "Dense", "Export"],
  "forceRebuild": false
}
```

## Artifacts and outputs

The system records both source assets and generated outputs. Depending on the pipeline mode, artifacts can include:

- original uploaded/imported images
- thumbnails
- per-stage JSON reports
- sparse model outputs
- dense point cloud outputs
- export bundles
- summary JSON
- failure log files

## Ops and debugging

- Use Swagger for endpoint discovery during local development.
- Use `/ops/jobs.html` for a lightweight browser-based job view.
- Use `/api/v1/ops/jobs` and `/api/v1/ops/jobs/{jobId}` to inspect serialized inputs, outputs, errors, and retry state.
- Use the ops process endpoints to execute queued jobs inside the API process for debugging without the worker.

## Current status

This repository is an MVP-style reconstruction backend with a clean separation between API, queue-driven background execution, and pipeline implementation. It is intentionally scoped around projects, images, runs, jobs, and artifacts rather than broader business-domain concepts.
