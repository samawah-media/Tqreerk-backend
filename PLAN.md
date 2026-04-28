# Taqreerk Backend — Master Plan & Requirements Tracker

## Context

Taqreerk (تقريرك) is an Arabic-first SaaS platform for aggregating, searching, and AI-analyzing Arabic reports. The client is شركة سماوة. The backend is `Taqreerk.back` — ASP.NET Core 8, PostgreSQL (Cloud SQL on GCP), Clean Architecture, deployed on Google Cloud Run.

**Current state (as of 2026-04-28):** Phase 1 is ~95% done. Python AI service deployed to staging with full feature set: streaming chat (SSE), hybrid retrieval (dense vector + sparse tsvector), Gemini fallback for path-rendered PDFs, bulk async endpoints with `ai_jobs` tracking. Vertex AI Gemini via ADC (with API-key fallback). .NET side has full auth (incl. email verification, OTP, password reset), user profile + interests, reports upload with GCS + AI integration, organizations portal, public reports, RBAC, reference data, invitations, and a background worker that calls the Python AI service. Remaining: payments (Phase 2), seed data tweaks, observability polish.

**Purpose of this file:** Track every deliverable from the PRD and project brief. Mark `[x]` when done so any agent picking up the project knows exactly what's left.

---

## Architecture Overview

```
Tqreerk-backend/
├── .github/workflows/
│   ├── ci.yml                              ← Build & test on push/PR ✅
│   ├── deploy-staging.yml                  ← .NET → Cloud Run staging ✅
│   ├── deploy-production.yml               ← .NET → Cloud Run production ✅
│   ├── deploy-ai-service-staging.yml       ← Python → Cloud Run staging ✅
│   └── deploy-ai-service-production.yml    ← Python → Cloud Run production ✅
├── Dockerfile                              ← .NET multi-stage build ✅
├── .dockerignore                           ✅
├── production-db-setup.md                  ← runbook for prod DB (pgvector, GIN index, EF history) ✅
├── test-chat-stream.html                   ← local test page for SSE chat streaming ✅
├── ai-service/                             ← Python FastAPI RAG + chat service ✅
│   ├── main.py                             ← FastAPI app entry point ✅
│   ├── Dockerfile                          ← python:3.12-slim + libmupdf ✅
│   ├── requirements.txt                    ✅
│   ├── core/config.py                      ← Settings (Vertex/AI Studio dual mode + .NET-style DATABASE_URL normalizer) ✅
│   ├── core/db.py                          ← async psycopg3 + pgvector (conn_ctx + get_conn dep) ✅
│   ├── core/prompts.py                     ← centralized prompts library + JSON-schema constants ✅
│   ├── services/gemini.py                  ← google-genai SDK: vision, embed, chat (+ stream), summarize, verify, translate ✅
│   ├── services/translate.py               ← Google Translate v3 + Gemini fallback for path-rendered PDFs ✅
│   ├── pipelines/ingest.py                 ← PDF (gs:// or https) → PNG → Gemini → embedding → DB ✅
│   ├── models/chat.py                      ← Pydantic schemas for chat ✅
│   ├── models/ingest.py                    ← Schemas for ingest/summarize/translate + bulk + job status ✅
│   ├── api/chat.py                         ← Chat session + streaming SSE messages + page-number short-circuit ✅
│   └── api/reports.py                      ← Single + bulk endpoints + job status polling ✅
└── Tqreerk-backend/
    ├── API/Controllers/                ← Auth, Rbac, Users, Reports, PublicReports, Organizations, Reference, Invitations ✅
    ├── API/Middleware/                  ← ExceptionHandlingMiddleware ✅
    ├── Application/DTOs/               ← Auth, Dashboard, Organizations, Rbac, Reports, Users ✅
    ├── Application/Interfaces/         ← IAuthService, ITokenService, IUserService, IRbacService, IReportService, IReportAiService, IOrganizationService, IPublicReportService, IDashboardService, IAiServiceClient, IFileStorage, IEmailSender ✅
    ├── Application/Services/           ← Auth, Token, User, Rbac, Report, ReportAi, Organization, PublicReport, Dashboard, LogEmailSender, SmtpEmailSender ✅
    ├── Application/Settings/           ← JwtSettings ✅
    ├── Domain/Entities/                ← 30 entities (27 + ChatSession, ChatMessage, ReportPage) ✅
    ├── Domain/Enums/                   ← All enums done ✅
    ├── Domain/Common/                  ← BaseEntity, AuditableEntity, SoftDeletableEntity ✅
    ├── Infrastructure/AI/              ← AiServiceClient (HTTP→Python), ReportProcessingWorker (background ingest+summarize) ✅
    ├── Infrastructure/Storage/         ← GcsFileStorage (production), LocalFileStorage (dev) ✅
    ├── Infrastructure/Data/            ← TaqreerkDbContext, configurations, 10 migrations ✅
    ├── Infrastructure/Data/Seed/       ← RbacSeedData, ReferenceSeedData (countries + sectors) ✅
    └── Extensions/                     ← ServiceExtensions ✅
```

**Key files:**
- [Program.cs](Tqreerk-backend/Program.cs) — auto-migrates DB on startup ✅
- [ServiceExtensions.cs](Tqreerk-backend/Extensions/ServiceExtensions.cs)
- [AuthController.cs](Tqreerk-backend/API/Controllers/AuthController.cs)
- [AuthService.cs](Tqreerk-backend/Application/Services/AuthService.cs)
- [TokenService.cs](Tqreerk-backend/Application/Services/TokenService.cs)
- [TaqreerkDbContext.cs](Tqreerk-backend/Infrastructure/Data/TaqreerkDbContext.cs)

---

## Phase 1 — Foundation (Days 1–7)

### Infrastructure & Setup
- [x] Repository created (Taqreerk.back)
- [x] Clean architecture folder structure
- [x] PostgreSQL connection via Npgsql EF Core 8
- [x] appsettings.json + appsettings.Development.json
- [x] Swagger/OpenAPI with JWT bearer security
- [x] CORS (localhost dev + taqreerk.com prod)
- [x] Global exception handling middleware
- [x] Soft-delete pattern (SoftDeletableEntity base class)
- [x] Audit timestamps (BaseEntity, AuditableEntity)
- [x] GitHub Actions CI/CD pipeline — ci.yml + deploy-staging.yml + deploy-production.yml
- [x] Staging environment verified on Google Cloud Run (`taqreerk-backend-staging`) — `/healthz/` confirmed ✅
- [x] Production Cloud Run service deployed (`taqreerk-backend-prod`)
- [x] Dockerfile + .dockerignore for containerized deploys
- [x] Auto-migrate EF Core on container startup (`db.Database.Migrate()` in Program.cs)
- [x] `/healthz` endpoint — DB status + pending migrations check
- [x] Swagger UI enabled in Development + Staging (disabled in Production)

### Database Schema
- [x] All 27 entities designed and configured
- [x] Initial migration created (`20260421224853_InitialCreate`)
- [x] PostgreSQL full-text search tsvector + GIN index on reports
- [x] JSONB columns for flexible data (permissions, AI output, chart data, etc.)
- [x] UUID primary keys with `gen_random_uuid()`
- [x] Soft delete query filters applied globally
- [x] Migration applied to `taqreerk_staging` database — auto-applied on container startup ✅ confirmed in logs
- [x] `ChatSession`, `ChatMessage`, `ReportPage` entities + EF configs added ✅
- [x] Migration `20260424000000_AddAiServiceTables` — pgvector extension + 3 new tables ✅
- [x] Migration `20260425073458_Feature2_LockoutAndAiTables` — auth lockout + extended AI tracking ✅
- [x] Migration `20260425083823_Feature3_SeedCountriesAndSectors` — Arab countries + sector seed via migration ✅
- [x] Migration `20260425101038_Feature5_OrganizationInvitations` — org invitation flow ✅
- [x] Migration `20260426000000_AddReportPagesFullTextSearch` — bilingual tsvector + GIN index + trigger on `report_pages.Content` (for hybrid chat retrieval) ✅
- [x] Migration `20260427072658_Feature6_FixSearchVectorTrigger` — trigger fix for tsvector edge cases ✅
- [x] Migration `20260427072700_Feature6_AddTranslatedFileUrl` — `TranslatedFileUrl` column on `ReportTranslation` ✅
- [x] pgvector `vector(768)` embedding column on `report_pages` (no global HNSW — filter by ReportId first) ✅
- [x] Staging DB: pgvector enabled + embedding column + search_vector + GIN index applied as `postgres` ✅
- [ ] Production DB: same steps documented in [production-db-setup.md](production-db-setup.md) — apply before first prod deploy
- [x] Seed: RBAC roles + permissions via `RbacSeedData` ✅
- [x] Seed: countries (Arab + global) + sectors via `ReferenceSeedData` ✅

### Authentication (JWT + Refresh Tokens)
- [x] `POST /api/auth/register/individual` — individual registration
- [x] `POST /api/auth/register/organization` — organization registration with admin user
- [x] `POST /api/auth/login` — email/password login, returns JWT + HttpOnly refresh cookie
- [x] `POST /api/auth/refresh` — token rotation (revoke old, issue new pair)
- [x] `POST /api/auth/logout` — revoke refresh token, clear cookie
- [x] BCrypt password hashing
- [x] JWT 15-min expiry (60-min in dev), Refresh 7-day expiry
- [x] Token hashed (SHA256) before database storage
- [x] IP address + device info tracking on tokens
- [x] `POST /api/auth/verify-email/send` + `verify-email/confirm` — email verification flow ✅
- [x] `POST /api/auth/otp/email/send` + `resend` + `verify` — email OTP flow (alt to verification link) ✅
- [x] `POST /api/auth/forgot-password` — initiate password reset ✅
- [x] `POST /api/auth/reset-password` — complete password reset ✅
- [x] Email sending: `SmtpEmailSender` (production) + `LogEmailSender` (dev) behind `IEmailSender` ✅
- [x] Account lockout (failed-login throttling) ✅
- [x] Session management: `GET /api/auth/sessions`, `DELETE sessions/{id}`, `POST logout-all` ✅
- [x] `GET /api/auth/me/permissions` — current user effective permissions ✅
- [ ] Unifonic OTP/SMS login (mobile authentication) — email OTP works; SMS still pending

### User Profile
- [x] `GET /api/users/me` — get current user profile ✅
- [x] `PUT /api/users/me` — update profile (name, job title, interest field, country, language) ✅
- [x] `POST /api/users/me/interests` — set user interests ✅
- [x] `GET /api/users/me/interests` — get user interests ✅

---

## Phase 2 — Payments (Days 8–15)

### Subscription Plans
- [ ] `GET /api/plans` — list all active plans with pricing
- [ ] `GET /api/plans/{id}` — get plan details

### Subscriptions & Miser (ميسر) Integration
- [ ] `IPaymentService` interface + `MiserPaymentService` implementation
- [ ] `POST /api/subscriptions/checkout` — initiate subscription purchase via Miser
- [ ] `POST /api/subscriptions/webhook` — handle all Miser webhook events (payment.success, payment.failed, subscription.cancelled, subscription.renewed)
- [ ] `GET /api/subscriptions/current` — get current user/org subscription status
- [ ] `POST /api/subscriptions/cancel` — cancel subscription
- [ ] `POST /api/subscriptions/upgrade` — upgrade/downgrade plan
- [ ] Subscription lifecycle state machine (active → grace → expired)
- [ ] Usage tracking per subscription (AI calls used, downloads used, featured slots used)
- [ ] Plan limit enforcement middleware/service (block AI calls when limit reached)

### Invoices & Billing
- [ ] `IInvoiceService` + PDF invoice generation (QuestPDF or similar)
- [ ] Automatic invoice creation on successful payment
- [ ] Email delivery of invoice PDF
- [ ] `GET /api/invoices` — list user/org invoices
- [ ] `GET /api/invoices/{id}/download` — download invoice PDF

### Admin Dashboard — User & Revenue Management
- [ ] `GET /api/admin/users` — list all users with filters (role, status, plan)
- [ ] `PUT /api/admin/users/{id}/status` — activate/suspend user
- [ ] `GET /api/admin/subscriptions` — all subscriptions with revenue stats
- [ ] `GET /api/admin/revenue` — revenue analytics (MRR, churn, plan breakdown)
- [ ] `GET /api/admin/audit-logs` — audit log viewer

---

## Phase 3 — Core Features (Days 16–22)

### Reference Data APIs
- [x] `GET /api/reference/sectors` — list all sectors (ar/en names) ✅
- [x] `GET /api/reference/countries` — list all countries (ar/en names) ✅

### Reports — Upload & Management
- [x] `IReportService` + `ReportService` implementation ✅
- [x] `IReportAiService` + `ReportAiService` — orchestrates GCS upload + Python ingest/summarize calls ✅
- [x] `POST /api/reports` — upload report (PDF + metadata), restricted to org admins (org context inferred from JWT) ✅
- [x] `GET /api/reports` — paginated org-scoped list ✅
- [x] `GET /api/reports/{id:guid}` — single report with metadata + AI status ✅
- [x] `DELETE /api/reports/{id}` — soft delete ✅
- [x] `GET /api/reports/{id}/ai-status` — poll ingest/summarize/translate progress (reads `ai_jobs`) ✅
- [x] `POST /api/reports/{id}/regenerate-ai` — re-run ingest+summarize (manual retry path) ✅
- [x] File upload to GCP Cloud Storage via `GcsFileStorage : IFileStorage` ✅
- [x] `ReportProcessingWorker` background worker — kicks off Python ingest after upload, polls until done ✅
- [ ] `PUT /api/reports/{id}` — update report metadata
- [ ] `GET /api/reports/search` — public full-text search using PostgreSQL tsvector
- [ ] View tracking: `POST /api/reports/{id}/view`
- [ ] Download tracking + permission check: `GET /api/reports/{id}/download`

### Public Reports (anonymous browse)
- [x] `GET /api/public/reports` — paginated public listing (filters: sector, country, org, year, language) ✅
- [x] `GET /api/public/reports/featured` ✅
- [x] `GET /api/public/reports/trending` ✅
- [x] `GET /api/public/reports/recent` ✅
- [x] `GET /api/public/reports/{slug}` — public report detail page ✅

### Report Interaction
- [ ] `POST /api/reports/{id}/rate` — submit rating (1–5) with optional review
- [ ] `GET /api/reports/{id}/ratings` — get report ratings
- [ ] `POST /api/reports/{id}/recommend` — recommend/share report
- [ ] `POST /api/reports/{id}/save` — save report to personal library
- [ ] `DELETE /api/reports/{id}/save` — unsave report
- [ ] `GET /api/users/me/saved-reports` — get user's saved reports

### Organizations — Public & Partner Portal
- [x] `GET /api/organizations/me` — get current user's org ✅
- [x] `PATCH /api/organizations/me/basics` — update name/description/logo ✅
- [x] `PATCH /api/organizations/me/scope` — sector scope ✅
- [x] `PATCH /api/organizations/me/reports` — report-publishing settings ✅
- [x] `PATCH /api/organizations/me/contact` — phone/website/address ✅
- [x] `POST /api/organizations/me/files` — org-level file uploads (logos etc.) ✅
- [x] `GET /api/organizations/me/stats` — org analytics ✅
- [x] `GET /api/organizations/me/recent-activity` — activity feed ✅
- [x] `GET /api/organizations/me/members` — member list ✅
- [x] `DELETE /api/organizations/me/members/{userId}` — remove member ✅
- [x] `GET /api/organizations/me/invitations` — pending invitations ✅
- [x] `POST /api/organizations/me/invitations` — invite member ✅
- [x] `DELETE /api/organizations/me/invitations/{id}` — revoke invitation ✅
- [x] `GET /api/invitations/preview` + `POST /api/invitations/accept` — token-based accept flow ✅
- [ ] `GET /api/organizations/{slug}` — public organization page with report list
- [ ] `GET /api/organizations` — list partner organizations
- [ ] `POST /api/organizations/me/promote-report` — request featured slot for a report

### AI Pipeline — Python ai-service (Gemini + pgvector RAG)

**Auth & infrastructure**
- [x] Unified `google-genai` SDK with dual auth: AI Studio (`GEMINI_API_KEY`) → Vertex AI (ADC) fallback ✅
- [x] Vertex AI default region `me-central1` (same datacenter as Cloud Run + Cloud SQL) ✅
- [x] Per-feature configurable models (`gemini_vision_model`, `gemini_chat_model`, `gemini_summary_model`, `gemini_embed_model`) ✅
- [x] All Gemini calls run with `temperature=0.2` for deterministic factual output ✅
- [x] Centralized prompt library at `core/prompts.py` (constants + JSON-schema for structured output) ✅
- [x] DB connection-string normalizer — accepts .NET Npgsql format AND libpq URIs ✅
- [x] PDF download supports both `gs://` URIs (via GCS client + ADC) and `https://` URLs ✅

**Single-report endpoints**
- [x] PDF ingestion pipeline: PyMuPDF → 150 DPI PNG per page → Gemini Vision → embedding → pgvector ✅
- [x] `POST /api/ai/reports/ingest` — trigger PDF ingestion ✅
- [x] `POST /api/ai/reports/summarize` — executive summary + key findings + topics (structured output) ✅
- [x] `POST /api/ai/reports/translate` — Google Translate v3 Document Translation with `enable_rotation_correction` + `enable_shadow_removal_native_pdf` ✅
- [x] Translation Gemini fallback — when Google's output ≈ input (path-rendered PDFs), Gemini reads PDF + renders new translated PDF (`.gemini.pdf` suffix) ✅
- [x] Translation auto-detect source language → flip target (ar↔en); .NET sends `output_prefix` exactly where it wants the file saved ✅

**Chat (streaming RAG)**
- [x] `POST /api/ai/chat/sessions` — create chat session per user per report ✅
- [x] `POST /api/ai/chat/sessions/{id}/messages` — **streaming SSE** (sources event → token stream → done) ✅
- [x] `GET /api/ai/chat/sessions/{id}` — full session history ✅
- [x] `GET /api/ai/chat/reports/{id}/sessions` — list all sessions for user+report ✅
- [x] Hybrid retrieval: dense (`embedding <=>`) + sparse (`tsvector @@`) with weighted-sum reranking ✅
- [x] Page-number short-circuit — explicit "page 2" / "صفحة ٢" requests bypass RAG and fetch directly ✅
- [x] Chat memory: last 10 messages sent to Gemini; full history stored in DB ✅
- [x] Producer/consumer thread bridge so streaming actually flushes token-by-token (not block-buffered) ✅
- [x] Per-step latency logging (`embed_text`, `hybrid_sql`, `gemini_first_token`, `stream_total`) ✅

**Bulk async endpoints (50+ items)**
- [x] `POST /api/ai/reports/bulk/ingest-summarize` — async fire-and-forget, returns 202 + job IDs ✅
- [x] `POST /api/ai/reports/bulk/translate` — same pattern ✅
- [x] `GET /api/ai/reports/jobs/{job_id}` — poll job status (Pending/Processing/Completed/Failed) ✅
- [x] Tracked via existing `ai_jobs` table (string-converted enums for `JobType` + `Status`) ✅
- [x] Bounded concurrency (semaphore=3) so 50-item batches don't slam Gemini quota ✅

**Deployment**
- [x] GitHub Actions `deploy-ai-service-staging.yml` — auto-deploys on push to `staging` ✅
- [x] GitHub Actions `deploy-ai-service-production.yml` — auto-deploys on push to `main` ✅
- [x] Both workflows pass `GEMINI_API_KEY` (optional), `DATABASE_URL_*`, `GCS_BUCKET`, `GCP_PROJECT_ID` ✅

**.NET integration**
- [x] `IAiServiceClient` + `AiServiceClient` (Infrastructure/AI) — HTTP client to Python service ✅
- [x] `IFileStorage` + `GcsFileStorage` + `LocalFileStorage` — upload PDFs, return `gs://` URI ✅
- [x] `POST /api/reports` — calls GCS upload + triggers Python ingest via `ReportAiService` ✅
- [x] `ReportProcessingWorker` (Infrastructure/AI) — background worker polling `ai_jobs` for state transitions ✅
- [x] `GET /api/reports/{id}/ai-status` — reads `ai_jobs` directly via EF Core (no Python proxy needed) ✅
- [x] `POST /api/reports/{id}/regenerate-ai` — manual retry trigger ✅
- [x] `TranslatedFileUrl` column on `ReportTranslation` (migration `Feature6_AddTranslatedFileUrl`) ✅
- [ ] Chat proxy: `POST /api/chat/*` in .NET — validates JWT then forwards SSE stream to Python (still TBD)
- [ ] Stale-job cleanup cron — mark jobs `Processing > 30min` as Failed (defensive, for instance recycle)

**Future AI features**
- [x] `POST /api/ai/reports/insights` — extract structured indicators (name/value/unit/time/context) and trends (topic/direction/magnitude) via Gemini structured output ✅
- [x] `POST /api/ai/reports/compare` — pairwise cosine similarity (mean page embeddings) + Gemini-driven qualitative comparison (common topics, key differences, shared indicators) ✅

### Infographics
- [ ] `POST /api/reports/{id}/infographics` — generate infographic (bar/pie/line/radar) from AI extracted data
- [ ] `GET /api/reports/{id}/infographics` — list report infographics
- [ ] `GET /api/infographics/{id}/export` — export as PNG/SVG/PDF

### User Dashboard
- [x] `IDashboardService` + `DashboardService` + DashboardDtos — service layer wired up ✅
- [ ] Dashboard controller endpoints — service exists, HTTP routes still TBD
- [ ] `GET /api/users/me/comparisons` — history of AI comparisons

### Notifications
- [ ] `GET /api/notifications` — get user notifications (paginated)
- [ ] `POST /api/notifications/{id}/read` — mark notification as read
- [ ] `POST /api/notifications/read-all` — mark all as read
- [ ] Notification dispatch service (triggered by: new report in followed sector/org, featured reports)

### Admin Dashboard — Content & Operations
- [ ] `GET /api/admin/reports` — all reports with approval queue
- [ ] `PUT /api/admin/reports/{id}/approve` — approve partner-uploaded report for publishing
- [ ] `PUT /api/admin/reports/{id}/reject` — reject report with reason
- [ ] `GET /api/admin/organizations` — all orgs with partner status management
- [ ] `PUT /api/admin/organizations/{id}/verify` — verify/unverify organization
- [ ] `GET /api/admin/stats` — platform KPIs (reports count, active users, searches, downloads by sector)
- [ ] `GET /api/admin/marketers` — manage marketer accounts
- [ ] `PUT /api/admin/featured-content` — curate featured reports on homepage

### Error Monitoring
- [x] Sentry SDK integrated in .NET (`Sentry.AspNetCore`, reads `Sentry__Dsn` env var) ✅
- [x] Sentry integrated in Python ai-service (`sentry-sdk[fastapi]`, auto-instruments FastAPI/Starlette, reads `SENTRY_DSN` env var, `ENVIRONMENT` tag) ✅
- [x] Global exception handler in Python ai-service — returns clean JSON with `request_id`, `error`, `type`, `detail`; hides internals in production; auto-captures to Sentry ✅
- [x] `X-Request-ID` middleware — every response carries a UUID for support traceability ✅
- [x] Ingest pipeline skips empty pages (no Gemini Vision content → no crash on `embed_text`) ✅
- [ ] Structured logging with Sentry breadcrumbs (optional polish — basic auto-breadcrumbs already capture logs/HTTP/SQL)

### Testing
- [ ] Unit tests for AuthService, TokenService (xUnit + Moq)
- [ ] Integration tests for auth endpoints (WebApplicationFactory + real PostgreSQL)
- [ ] Unit tests for ReportService, AiPipelineService

---

## Phase 4 — Hardening (Days 23–27)

### Security
- [ ] CSP headers middleware
- [ ] Rate limiting on auth endpoints (prevent brute force)
- [ ] Input validation attributes on all DTOs (FluentValidation or DataAnnotations)
- [ ] SQL injection audit (EF Core parameterized — verify no raw SQL)
- [ ] CSRF protection review
- [ ] Secret rotation procedure documented

### Performance
- [ ] Database indexes audit (verify EF configs produce correct indexes)
- [ ] Pagination on all list endpoints (cursor or offset)
- [ ] N+1 query audit with `.Include()` chains
- [ ] Response caching for reference data (sectors, countries, plans)

### Load Testing
- [ ] k6 or NBomber load test on report search endpoint
- [ ] k6 load test on auth endpoints

---

## Phase 5 — Delivery (Days 28–30)

- [ ] Production deployment on Google Cloud Run (`sadeed-production` project)
- [ ] Environment variables set in GCP Secret Manager
- [ ] Swagger UI live at production URL
- [ ] Postman Collection exported
- [ ] README.md with setup, env vars, local dev instructions
- [ ] Architecture diagram
- [ ] Post-handover document (env vars, backup/restore, deployment guide, monthly cost)
- [ ] Full account + code ownership transfer to client

---

## Environment Variables Reference

**GitHub Actions Secrets (already set in repo):**

| Secret | Purpose |
|---|---|
| `GCP_PROJECT_ID` | GCP project ID (`taqrrerk`) |
| `GCP_REGION` | GCP region (`me-central1`) |
| `GCP_SA_KEY` | GCP service account JSON key for auth |
| `ARTIFACT_REGISTRY_REPO` | Artifact Registry repo name |
| `GCP_CLOUDSQL_CONNECTION_NAME` | `taqrrerk:me-central1:taqrrerkdb` |
| `DATABASE_URL_STAGING` | Npgsql connection string → `taqreerk_staging` DB |
| `DATABASE_URL_PRODUCTION` | Npgsql connection string → `taqreerk_production` DB |
| `JWT_SECRET` | JWT signing key (min 32 chars) |

**Set in workflows (already wired):**

| Variable | Service | Where Used |
|---|---|---|
| `CLOUD_STORAGE_BUCKET` | Python ai-service | GCS bucket name (`taqreerk-uploads`, me-central1) ✅ |
| `GEMINI_API_KEY` | Python (optional) | If set → AI Studio. If empty → falls back to Vertex AI via ADC ✅ |
| `GCP_SERVICE_ACCOUNT` | Both | Cloud Run runtime service account |

**Still needed (set when features are built):**

| Variable | Service | Where Used |
|---|---|---|
| `REFRESH_TOKEN_SECRET` | .NET | Refresh token signing |
| `MISER_SECRET_KEY` | .NET | Miser payment API key |
| `MISER_WEBHOOK_SECRET` | .NET | Miser webhook HMAC verification |
| `UNIFONIC_API_KEY` | .NET | OTP/SMS login |
| `UNIFONIC_SENDER_ID` | .NET | SMS sender ID |
| `SENTRY_DSN` | Both | Error monitoring |
| `AI_SERVICE_URL_STAGING` | .NET | Python Cloud Run URL (staging) |
| `AI_SERVICE_URL_PRODUCTION` | .NET | Python Cloud Run URL (production) |

**GCP-side setup required (not GitHub secrets):**

- Vertex AI API enabled
- Cloud Translation API enabled
- Cloud SQL Admin API enabled
- Service account roles: `Vertex AI User`, `Cloud Translation API User`, `Storage Object Viewer` on `taqreerk-uploads`, `Cloud SQL Client`

---

## Conventions

- **Any work done must be reflected in this plan** — when you finish a task, mark its checkbox `[x]` and add a brief note. When you add new work that isn't tracked, append it to the relevant phase. This file is the source of truth for project status.
- All new controllers follow pattern in `AuthController.cs`
- All new services register in `ServiceExtensions.cs`
- All new DTOs go in `Application/DTOs/{Feature}/`
- All new entity configurations go in `Infrastructure/Data/Configurations/`
- Soft-delete entities extend `SoftDeletableEntity`, others extend `BaseEntity`
- Slugs generated in service layer, not controller
- Never expose `PasswordHash` or `TokenHash` in responses
- All list endpoints must be paginated
- Arabic (`Ar`) and English (`En`) fields on all public-facing entities
- Python ai-service: prompts live in `core/prompts.py`, model names in `core/config.py` — no hardcoded prompts or models inside services
- Python ai-service: all new Gemini calls run with `temperature=0.2` for deterministic factual output
