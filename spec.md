
# Project-Focused Reconstruction API — Detailed Codex Spec

## Purpose

Implement a **project-focused API** for creating and processing reconstruction projects from image files.

This service is intentionally **narrow in scope**. It is **not** responsible for companies, sites, portfolios, permissions hierarchies, billing, or long-term business-domain modeling. Its job is to:

- create projects
- ingest image files
- ingest image URLs
- validate image inputs
- start processing runs
- track run/job status
- expose project artifacts and reports

The service should be designed so that **higher-level domain services** can later reference projects by ID without forcing this API to own those concepts.

---

# Product Boundary

## In scope

- Projects
- Project metadata
- Image uploads
- Image URL imports
- Image validation
- Pipeline runs
- Job orchestration
- Artifact tracking
- Status/reporting
- Logs
- Downloadable outputs

## Out of scope

- Companies
- Sites
- Users / RBAC beyond simple placeholder auth hooks
- Billing
- Multi-tenant business rules
- Browser viewer implementation
- Measurement APIs
- Incremental live reconstruction
- Domain-specific grouping of multiple point clouds

---

# High-Level Architecture

The system should be split into four main deployable concerns:

## 1. API (`Recon.Api`)
Owns:
- HTTP endpoints
- request validation
- project lifecycle
- image intake orchestration
- run creation
- status APIs
- artifact listing

The API must be **stateless** and must **not** run COLMAP or other long-running reconstruction tasks directly inside request handlers.

## 2. Worker (`Recon.Worker`)
Owns:
- background job execution
- image import/download jobs
- image validation jobs
- pipeline execution jobs
- artifact publishing
- report generation

The worker should reuse pipeline logic from shared libraries.

## 3. Core libraries (`Recon.Core`, `Recon.Domain`, `Recon.Infrastructure`)
Own:
- business logic for projects/runs/jobs/artifacts
- pipeline orchestration
- repository abstractions
- object storage abstraction
- process runner abstraction
- external tool adapters

## 4. Storage
Use:
- **Postgres** for relational metadata
- **S3-compatible object storage** for binary assets
- **local worker scratch disk** for temporary working files
- **Redis** OR **Postgres-backed queue** for background jobs

For MVP, a Postgres-backed queue is acceptable if you want to minimize infrastructure.

---

# Technology Requirements

Use the following stack unless there is a compelling implementation reason to vary:

- **.NET 8**
- **ASP.NET Core Web API**
- **C# 12**
- **PostgreSQL**
- **Entity Framework Core** for persistence
- **System.Text.Json**
- **Background worker service** in .NET
- **FluentValidation** or equivalent for request validation
- **Swagger / OpenAPI**
- **Serilog** for structured logging
- **Docker** support for local development
- **xUnit** for tests

Optional:
- **MediatR** if helpful
- a lightweight queue library if clearly abstracted

---

# Solution Structure

Create this solution layout:

```text
Recon.Api.sln
  src/
    Recon.Api/
    Recon.Worker/
    Recon.Core/
    Recon.Domain/
    Recon.Infrastructure/
  tests/
    Recon.Api.Tests/
    Recon.Core.Tests/
    Recon.Infrastructure.Tests/
```

## Responsibilities

### `Recon.Api`
- Controllers / minimal APIs
- DTOs
- request validation
- dependency injection wiring
- auth placeholder middleware
- OpenAPI config

### `Recon.Worker`
- background loop / queue consumer
- job handlers
- progress updates
- artifact publishing hooks

### `Recon.Core`
- project services
- run services
- job services
- artifact services
- application commands/queries
- image import orchestration
- pipeline orchestration contracts

### `Recon.Domain`
- entities
- enums
- value objects
- domain constants

### `Recon.Infrastructure`
- EF Core DbContext
- repositories
- migrations
- object storage client
- local file staging
- queue implementation
- external process runner
- pipeline adapters
- URL download client

---

# Domain Model

The API should be **project-centric**.

## Core entities

- `Project`
- `ProjectImage`
- `ImportBatch`
- `ImportBatchItem`
- `PipelineRun`
- `Job`
- `Artifact`
- `StageReport`

Do **not** add `Company`, `Site`, `Organization`, `Survey`, or similar domain entities.

You may include flexible optional external reference fields.

---

# Entity Definitions

## Project

```csharp
public sealed class Project
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public ProjectStatus Status { get; set; }
    public string? ExternalReference { get; set; }
    public string? OwnerReference { get; set; }
    public string? SiteReference { get; set; }
    public string? SourceType { get; set; }
    public string? ConfigJson { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
```

## ProjectImage

```csharp
public sealed class ProjectImage
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }

    public string OriginalFileName { get; set; } = "";
    public string StorageKey { get; set; } = "";
    public string SourceType { get; set; } = "";
    public string? SourceUrl { get; set; }

    public string MimeType { get; set; } = "";
    public long FileSizeBytes { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }

    public string Sha256 { get; set; } = "";
    public bool IsValidImage { get; set; }
    public string ValidationStatus { get; set; } = "";
    public string? ValidationError { get; set; }

    public string? ExifJson { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
```

## ImportBatch

```csharp
public sealed class ImportBatch
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public ImportBatchStatus Status { get; set; }
    public int RequestedCount { get; set; }
    public int SucceededCount { get; set; }
    public int FailedCount { get; set; }
    public string? RequestJson { get; set; }
    public string? ResultJson { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
```

## ImportBatchItem

```csharp
public sealed class ImportBatchItem
{
    public Guid Id { get; set; }
    public Guid ImportBatchId { get; set; }
    public string SourceUrl { get; set; } = "";
    public ImportItemStatus Status { get; set; }
    public string? ErrorMessage { get; set; }
    public Guid? ProjectImageId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
```

## PipelineRun

```csharp
public sealed class PipelineRun
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public PipelineRunStatus Status { get; set; }
    public string PipelineVersion { get; set; } = "";
    public string? RequestedStagesJson { get; set; }
    public string? ConfigSnapshotJson { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? FinishedAtUtc { get; set; }
}
```

## Job

```csharp
public sealed class Job
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid? PipelineRunId { get; set; }
    public JobType Type { get; set; }
    public JobStatus Status { get; set; }

    public int Priority { get; set; }
    public int AttemptCount { get; set; }
    public int MaxAttempts { get; set; }

    public string InputJson { get; set; } = "";
    public string? OutputJson { get; set; }
    public string? ErrorJson { get; set; }

    public decimal? ProgressPercent { get; set; }
    public string? ProgressMessage { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? FinishedAtUtc { get; set; }
}
```

## Artifact

```csharp
public sealed class Artifact
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid? PipelineRunId { get; set; }

    public ArtifactType Type { get; set; }
    public ArtifactStatus Status { get; set; }

    public string StorageKey { get; set; } = "";
    public string FileName { get; set; } = "";
    public string MimeType { get; set; } = "";
    public long? FileSizeBytes { get; set; }

    public string? ChecksumSha256 { get; set; }
    public string? MetadataJson { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
```

## StageReport

```csharp
public sealed class StageReport
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid PipelineRunId { get; set; }

    public PipelineStage Stage { get; set; }
    public bool Success { get; set; }

    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset FinishedAtUtc { get; set; }

    public double DurationSeconds { get; set; }
    public string JsonPayload { get; set; } = "";
}
```

---

# Enums

Codex should define:

## ProjectStatus
- Draft
- ReadyForProcessing
- Processing
- Succeeded
- Failed
- Archived

## ImportBatchStatus
- Pending
- Running
- Completed
- Failed
- PartiallyCompleted

## ImportItemStatus
- Pending
- Downloading
- Succeeded
- Failed
- Rejected

## PipelineRunStatus
- Queued
- Running
- Succeeded
- Failed
- Cancelled

## JobStatus
- Queued
- Running
- Succeeded
- Failed
- Cancelled
- RetryScheduled

## JobType
- ValidateUploadedImage
- ImportImageFromUrl
- StartPipelineRun
- InspectProject
- RunSparseReconstruction
- RunDenseReconstruction
- ExportArtifacts
- PublishArtifacts
- GenerateProjectSummary

## ArtifactType
- OriginalImage
- Thumbnail
- InspectReport
- SparseModel
- DensePointCloud
- DenseReport
- SparseReport
- ExportReport
- OctreePackage
- PotreePackage
- LogFile
- SummaryJson

## ArtifactStatus
- Pending
- Available
- Failed
- Superseded

## PipelineStage
- Inspect
- Sparse
- Dense
- Export
- Publish

---

# Database Design

Use EF Core migrations.

## Required tables

- projects
- project_images
- import_batches
- import_batch_items
- pipeline_runs
- jobs
- artifacts
- stage_reports

## Required indexes

### `projects`
- PK on `id`
- index on `status`
- index on `created_at_utc`

### `project_images`
- PK on `id`
- index on `project_id`
- index on (`project_id`, `sha256`)
- index on `validation_status`

### `import_batches`
- PK on `id`
- index on `project_id`
- index on `status`

### `import_batch_items`
- PK on `id`
- index on `import_batch_id`
- index on `status`

### `pipeline_runs`
- PK on `id`
- index on `project_id`
- index on `status`
- index on `created_at_utc`

### `jobs`
- PK on `id`
- index on `status`
- index on `type`
- index on `project_id`
- index on `pipeline_run_id`
- index on (`status`, `priority`, `created_at_utc`)

### `artifacts`
- PK on `id`
- index on `project_id`
- index on `pipeline_run_id`
- index on `type`
- index on `status`

### `stage_reports`
- PK on `id`
- index on `project_id`
- index on `pipeline_run_id`
- index on `stage`

---

# Object Storage Layout

Suggested object key layout:

```text
projects/{projectId}/images/{imageId}/original/{fileName}
projects/{projectId}/images/{imageId}/thumbnail/thumb.jpg

projects/{projectId}/runs/{runId}/reports/inspect.json
projects/{projectId}/runs/{runId}/reports/sparse.json
projects/{projectId}/runs/{runId}/reports/dense.json
projects/{projectId}/runs/{runId}/reports/export.json

projects/{projectId}/runs/{runId}/sparse/
projects/{projectId}/runs/{runId}/dense/fused.ply
projects/{projectId}/runs/{runId}/octree/
projects/{projectId}/runs/{runId}/logs/{jobId}.log

projects/{projectId}/current/summary.json
```

## Rules
- Storage keys must be deterministic
- Binary assets must not be stored in Postgres
- All downloadable outputs must be represented as `Artifact` rows

---

# API Design

Base route:
```text
/api/v1
```

## 1. Create Project

### `POST /api/v1/projects`

Request:
```json
{
  "name": "Wapping Site Scan 01",
  "description": "Initial scan of site",
  "externalReference": "client-123",
  "ownerReference": "owner-x",
  "siteReference": "site-42",
  "sourceType": "manual_upload",
  "config": {
    "matcherType": "exhaustive",
    "enablePotreeExport": false
  }
}
```

Response:
```json
{
  "id": "GUID",
  "name": "Wapping Site Scan 01",
  "status": "Draft",
  "createdAtUtc": "..."
}
```

Validation:
- name required
- max name length 200

## 2. List Projects

### `GET /api/v1/projects`

Supports filters:
- `status`
- `createdAfter`
- `createdBefore`
- `search`
- `page`
- `pageSize`

## 3. Get Project

### `GET /api/v1/projects/{projectId}`

Returns:
- project metadata
- latest run summary
- image counts
- artifact summary

## 4. Upload Images

### `POST /api/v1/projects/{projectId}/images`

Accepts multipart form-data.

Requirements:
- support multiple files
- per-file validation
- file size limits
- supported file type checks
- object storage upload
- creation of `ProjectImage` rows
- enqueue validation jobs for each uploaded image

## 5. Import Images from URLs

### `POST /api/v1/projects/{projectId}/imports`

Request:
```json
{
  "urls": [
    "https://example.com/photo1.jpg",
    "https://example.com/photo2.jpg"
  ]
}
```

Behavior:
- API stores batch and batch items
- enqueues `ImportImageFromUrl` jobs
- returns immediately

## 6. Get Import Batch

### `GET /api/v1/projects/{projectId}/imports/{importBatchId}`

Returns:
- batch summary
- per-item status
- errors
- linked created images

## 7. List Project Images

### `GET /api/v1/projects/{projectId}/images`

Supports filters:
- validation status
- source type

## 8. Start Pipeline Run

### `POST /api/v1/projects/{projectId}/runs`

Request:
```json
{
  "stages": ["Inspect", "Sparse", "Dense", "Export"],
  "forceRebuild": false
}
```

Behavior:
- validate project has enough valid images
- create `PipelineRun`
- enqueue first job(s)
- update project status to `Processing`

## 9. List Project Runs

### `GET /api/v1/projects/{projectId}/runs`

## 10. Get Project Run

### `GET /api/v1/projects/{projectId}/runs/{runId}`

Returns:
- run metadata
- stage statuses
- jobs
- progress
- linked reports/artifacts

## 11. List Project Artifacts

### `GET /api/v1/projects/{projectId}/artifacts`

Supports filters:
- type
- runId
- status

## 12. Download Artifact

### `GET /api/v1/projects/{projectId}/artifacts/{artifactId}`

Either:
- streams content
- or returns signed URL

## 13. Get Job

### `GET /api/v1/jobs/{jobId}`

## 14. Health Endpoints

### `GET /health/live`
### `GET /health/ready`

---

# DTOs

Implement explicit DTOs:
- `CreateProjectRequest`
- `ProjectResponse`
- `ProjectListItemResponse`
- `UploadImagesResponse`
- `CreateImportBatchRequest`
- `ImportBatchResponse`
- `ProjectImageResponse`
- `CreatePipelineRunRequest`
- `PipelineRunResponse`
- `ArtifactResponse`
- `JobResponse`

Do not expose EF entities directly.

---

# Validation Rules

## Project creation
- name required
- max length 200

## Upload
- file count limit configurable
- file size limit configurable
- allowed extensions: `.jpg`, `.jpeg`, `.png`, `.tif`, `.tiff`
- reject empty files

## URL import
- only `http` and `https`
- max URL count configurable
- reject invalid URLs
- block private IP ranges / localhost / loopback / link-local
- limit redirects
- limit download size
- timeout downloads

## Start run
- require minimum count of valid images, e.g. 3
- reject if run already active unless explicitly allowed

---

# Security Requirements

## URL Import Security
Protect against SSRF:
- block `localhost`
- block private IPv4 ranges
- block private IPv6 ranges
- block link-local
- only allow `http` / `https`
- enforce max response size
- enforce request timeout
- validate actual image decode

## File Upload Security
- do not trust extension only
- validate image can actually be decoded
- sanitize file names
- never use raw file names as full storage keys

## General
- no raw exception leaks
- use problem-details responses
- logs must avoid secrets

---

# Pipeline Orchestration

Suggested pipeline:

```text
Uploaded/Imported Images
→ Validate Images
→ Inspect Project
→ Sparse Reconstruction
→ Dense Reconstruction
→ Export Artifacts
→ Publish Reports
```

## Job chaining
1. `StartPipelineRun`
2. `InspectProject`
3. `RunSparseReconstruction`
4. `RunDenseReconstruction`
5. `ExportArtifacts`
6. `GenerateProjectSummary`

## Failure behavior
- mark job failed
- mark run failed
- update project status
- preserve partial artifacts and logs
- allow reruns

---

# Queue Abstraction

```csharp
public interface IJobQueue
{
    Task EnqueueAsync(Job job, CancellationToken ct);
    Task<Job?> DequeueNextAsync(CancellationToken ct);
    Task MarkRunningAsync(Guid jobId, CancellationToken ct);
    Task MarkSucceededAsync(Guid jobId, string? outputJson, CancellationToken ct);
    Task MarkFailedAsync(Guid jobId, string errorJson, bool shouldRetry, CancellationToken ct);
    Task ReportProgressAsync(Guid jobId, decimal percent, string? message, CancellationToken ct);
}
```

Preferred MVP implementation:
- Postgres-backed queue

Requirements:
- atomic claiming
- priority support
- retries

---

# Worker Design

Create a separate .NET worker host.

## Responsibilities
- poll queue
- claim jobs
- execute handlers
- update progress
- upload artifacts
- write logs
- retry transient failures

## Job handlers
Implement:
- `ValidateUploadedImageJobHandler`
- `ImportImageFromUrlJobHandler`
- `StartPipelineRunJobHandler`
- `InspectProjectJobHandler`
- `RunSparseReconstructionJobHandler`
- `RunDenseReconstructionJobHandler`
- `ExportArtifactsJobHandler`
- `GenerateProjectSummaryJobHandler`

---

# External Pipeline Reuse

Design to reuse shared pipeline logic.

```csharp
public interface IProjectPipelineService
{
    Task<PipelineExecutionResult> RunInspectAsync(ProjectPipelineContext context, CancellationToken ct);
    Task<PipelineExecutionResult> RunSparseAsync(ProjectPipelineContext context, CancellationToken ct);
    Task<PipelineExecutionResult> RunDenseAsync(ProjectPipelineContext context, CancellationToken ct);
    Task<PipelineExecutionResult> RunExportAsync(ProjectPipelineContext context, CancellationToken ct);
}
```

Implementation can wrap:
- COLMAP
- optional Potree conversion later
- future custom octree export later

---

# Local Worker Scratch Model

Workers should:
1. create a local temp working directory
2. download inputs from object storage
3. run external tools locally
4. upload outputs back
5. clean temp files afterward

Example:
```text
/tmp/recon/{runId}/input/images/
/tmp/recon/{runId}/work/
/tmp/recon/{runId}/output/
```

---

# Reports and Logs

Required reports:
- inspect report
- sparse report
- dense report
- export report
- summary report

These should be:
- stored in object storage
- represented as artifacts
- optionally summarized into `stage_reports`

Logs should include:
- stdout/stderr
- progress
- command invocations
- timestamps

---

# Project Status Rules

Suggested statuses:
- `Draft`
- `ReadyForProcessing`
- `Processing`
- `Succeeded`
- `Failed`
- `Archived`

Centralize transition logic in a service.

---

# Artifact Rules

Artifacts are first-class.

Rules:
- every meaningful output file must have an `Artifact` row
- artifacts tie to a project and optionally a run
- API responses must not leak storage internals unnecessarily

Minimum artifact types:
- OriginalImage
- Thumbnail
- InspectReport
- SparseModel
- DensePointCloud
- SparseReport
- DenseReport
- ExportReport
- LogFile
- SummaryJson

Optional:
- PotreePackage
- OctreePackage

---

# Auth and Multi-Tenancy Stance

The API is intentionally not doing full multi-tenant modeling yet.

Requirements:
- include an abstraction point for caller identity
- do not hard-wire company/site assumptions into schema
- keep controllers/services easy to extend with auth later

For MVP:
- allow anonymous or simple API-key auth

---

# OpenAPI / Swagger

Configure OpenAPI with tags:
- Projects
- Images
- Imports
- Runs
- Artifacts
- Jobs
- Health

Include example payloads where practical.

---

# Error Handling

Use RFC 7807 Problem Details.

Requirements:
- validation errors return 400
- not found 404
- conflict 409 where appropriate
- unexpected failures return 500 with safe message only

Implement global exception handling middleware.

---

# Observability

## Logging
Use structured logs with Serilog.

Include:
- project id
- run id
- job id
- image id where relevant
- artifact id where relevant

## Metrics
Optional but preferred:
- total projects created
- images uploaded
- imports succeeded/failed
- active jobs
- average run duration
- failure count by job type

---

# Testing Requirements

## `Recon.Api.Tests`
- project creation endpoint
- image upload endpoint
- import batch creation endpoint
- start run endpoint
- list/get project endpoint
- problem-details behavior

## `Recon.Core.Tests`
- project status transitions
- run creation rules
- artifact registration
- job chaining logic
- validation orchestration

## `Recon.Infrastructure.Tests`
- queue claiming logic
- object storage key generation
- URL validation / SSRF blocking logic
- image metadata extraction

Do not require real COLMAP in automated tests.

---

# Coding Standards

- nullable reference types enabled
- async/await throughout
- explicit cancellation token usage
- no static mutable global state
- DI for all external integrations
- avoid fat controllers
- keep DTOs separate from entities

---

# Implementation Milestones

## Milestone 1: Foundation
- solution structure
- domain entities and enums
- EF Core DbContext
- migrations
- repositories
- health endpoints
- OpenAPI

## Milestone 2: Projects
- create/list/get projects
- project status model
- DTOs/validators

## Milestone 3: Image ingestion
- upload images endpoint
- object storage abstraction
- project image records
- validation job enqueueing

## Milestone 4: URL import
- import batch endpoints
- import batch item model
- worker URL download job
- SSRF protections
- validation

## Milestone 5: Runs and jobs
- start run endpoint
- run records
- job queue
- worker polling/claiming
- job status endpoint

## Milestone 6: Pipeline orchestration
- inspect / sparse / dense / export handlers
- artifact registration
- stage reports
- logs

## Milestone 7: Polish
- Swagger examples
- better error handling
- integration tests
- Docker compose for API + worker + Postgres

---

# Docker / Local Development

Provide:
- API service
- Worker service
- Postgres
- optional MinIO for local S3-compatible object storage

Suggested files:
- `docker-compose.yml`
- `.env.example`

---

# Non-Goals for This Spec

Do not implement:
- sites
- companies
- billing
- full auth system
- measurement tools
- browser viewer
- live octree streaming
- Gaussian splats
- custom octree builder

The API must remain **project-focused**.

---

# Definition of Done

The implementation is done when all of the following work:

## Project lifecycle
- create a project
- get a project
- list projects

## Image lifecycle
- upload one or more image files
- create URL import batches
- process URL imports in background
- validate images and persist metadata

## Pipeline lifecycle
- start a project run
- enqueue and execute background jobs
- inspect/sparse/dense/export stages represented as jobs or handlers
- stage reports and artifacts persisted
- job and run status queryable

## Artifact lifecycle
- artifacts registered in DB
- downloadable files can be retrieved or signed for retrieval

## Quality
- OpenAPI works
- tests exist
- no raw stack traces leak
- project-focused boundary preserved

---

# Final Codex Prompt Block

```text
Implement a .NET 8 ASP.NET Core API and .NET worker service for a project-focused reconstruction pipeline.

Core boundary:
- The system manages only reconstruction projects created from image files.
- Do NOT implement companies, sites, organizations, surveys, billing, or complex multi-tenant domain objects.
- A project may contain optional opaque fields like OwnerReference and SiteReference, but they are strings only and not foreign keys.

Use this solution structure:
- Recon.Api
- Recon.Worker
- Recon.Core
- Recon.Domain
- Recon.Infrastructure
- tests for API/Core/Infrastructure

Use:
- .NET 8
- C# 12
- ASP.NET Core
- EF Core + PostgreSQL
- S3-compatible object storage abstraction
- structured logging
- Swagger/OpenAPI
- xUnit

Implement entities:
- Project
- ProjectImage
- ImportBatch
- ImportBatchItem
- PipelineRun
- Job
- Artifact
- StageReport

Implement endpoints:
- POST /api/v1/projects
- GET /api/v1/projects
- GET /api/v1/projects/{projectId}
- POST /api/v1/projects/{projectId}/images
- GET /api/v1/projects/{projectId}/images
- POST /api/v1/projects/{projectId}/imports
- GET /api/v1/projects/{projectId}/imports/{importBatchId}
- POST /api/v1/projects/{projectId}/runs
- GET /api/v1/projects/{projectId}/runs
- GET /api/v1/projects/{projectId}/runs/{runId}
- GET /api/v1/projects/{projectId}/artifacts
- GET /api/v1/projects/{projectId}/artifacts/{artifactId}
- GET /api/v1/jobs/{jobId}
- GET /health/live
- GET /health/ready

Implement background worker job handling for:
- uploaded image validation
- URL import
- starting pipeline runs
- inspect
- sparse reconstruction
- dense reconstruction
- export
- project summary generation

Implement queue abstraction and a Postgres-backed queue for MVP.
Implement request validation.
Implement RFC 7807 problem details.
Implement SSRF protections for URL import:
- block localhost
- block private IP ranges
- allow only http/https
- max size
- timeouts
- redirect limits

Artifacts must be first-class and stored as metadata rows in DB with binary content stored in object storage.

The API must remain project-focused and reusable later from a higher-level domain service.
Do not leak EF entities directly in API responses.
Use DTOs and validators.
Add tests for endpoints, queue logic, status transitions, import validation, and orchestration rules.
```
