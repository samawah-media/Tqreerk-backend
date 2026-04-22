# Taqreerk Backend — Master Plan & Requirements Tracker

## Context

Taqreerk (تقريرك) is an Arabic-first SaaS platform for aggregating, searching, and AI-analyzing Arabic reports. The client is شركة سماوة. The backend is `Taqreerk.back` — ASP.NET Core 8, PostgreSQL (Cloud SQL on GCP), Clean Architecture, deployed on Google Cloud Run.

**Current state (as of 2026-04-22):** Phase 1 is ~80% done. The domain model (all 27 entities), auth system, database schema, clean architecture skeleton, Dockerfile, and full CI/CD pipeline (3 GitHub Actions workflows) are in place. Both staging and production Cloud Run services are live. Auto-migration runs on every deploy. Remaining Phase 1 work: email/OTP auth flows, user profile endpoints, seed data.

**Purpose of this file:** Track every deliverable from the PRD and project brief. Mark `[x]` when done so any agent picking up the project knows exactly what's left.

---

## Architecture Overview

```
Tqreerk-backend/
├── .github/workflows/
│   ├── ci.yml                  ← Build & test on push/PR to main/staging/production ✅
│   ├── deploy-staging.yml      ← Build → push image → deploy to Cloud Run staging ✅
│   └── deploy-production.yml   ← Build → push image → deploy to Cloud Run production ✅
├── Dockerfile                  ← Multi-stage build (SDK 8 → ASP.NET runtime 8) ✅
├── .dockerignore               ✅
└── Tqreerk-backend/
    ├── API/Controllers/        ← HTTP layer (only AuthController exists so far)
    ├── API/Middleware/         ← ExceptionHandlingMiddleware ✅
    ├── Application/DTOs/       ← Request/Response objects
    ├── Application/Interfaces/ ← Service contracts
    ├── Application/Services/   ← Business logic (AuthService, TokenService done)
    ├── Application/Settings/   ← JwtSettings ✅
    ├── Domain/Entities/        ← 27 entities all done ✅
    ├── Domain/Enums/           ← All enums done ✅
    ├── Domain/Common/          ← BaseEntity, AuditableEntity, SoftDeletableEntity ✅
    ├── Infrastructure/Data/    ← TaqreerkDbContext, 26 configurations, 1 migration ✅
    └── Extensions/             ← ServiceExtensions ✅
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
- [ ] Migration applied to `taqreerk_production` database — needs production deploy
- [ ] Seed data: roles (admin, editor, partner, researcher, subscriber)
- [ ] Seed data: common sectors (economy, education, technology, investment, etc.)
- [ ] Seed data: Arab countries

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
- [ ] Email verification flow (send verification email, confirm endpoint)
- [ ] `POST /api/auth/forgot-password` — initiate password reset
- [ ] `POST /api/auth/reset-password` — complete password reset
- [ ] Unifonic OTP/SMS login (mobile authentication)

### User Profile
- [ ] `GET /api/users/me` — get current user profile
- [ ] `PUT /api/users/me` — update profile (name, job title, interest field, country, language)
- [ ] `POST /api/users/me/interests` — set user interests (sectors, organizations, countries)
- [ ] `GET /api/users/me/interests` — get user interests

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
- [ ] `GET /api/sectors` — list all sectors (ar/en names)
- [ ] `GET /api/countries` — list all countries (ar/en names)

### Reports — Upload & Management
- [ ] `IReportService` + `ReportService` implementation
- [ ] `POST /api/reports` — upload report (PDF + metadata), restricted to org admins
- [ ] `GET /api/reports` — public report library (paginated, filterable by sector/country/org/year/language)
- [ ] `GET /api/reports/{slug}` — public report detail page
- [ ] `PUT /api/reports/{id}` — update report metadata (org admin or platform admin)
- [ ] `DELETE /api/reports/{id}` — soft delete report
- [ ] `GET /api/reports/search` — full-text search using PostgreSQL tsvector
- [ ] File upload to GCP Cloud Storage (GCS client integration)
- [ ] View tracking: `POST /api/reports/{id}/view`
- [ ] Download tracking + permission check: `GET /api/reports/{id}/download`

### Report Interaction
- [ ] `POST /api/reports/{id}/rate` — submit rating (1–5) with optional review
- [ ] `GET /api/reports/{id}/ratings` — get report ratings
- [ ] `POST /api/reports/{id}/recommend` — recommend/share report
- [ ] `POST /api/reports/{id}/save` — save report to personal library
- [ ] `DELETE /api/reports/{id}/save` — unsave report
- [ ] `GET /api/users/me/saved-reports` — get user's saved reports

### Organizations — Public & Partner Portal
- [ ] `GET /api/organizations/{slug}` — public organization page with report list
- [ ] `GET /api/organizations` — list partner organizations
- [ ] `GET /api/organization/me` — get current user's org (for org admins)
- [ ] `PUT /api/organization/me` — update org profile
- [ ] `POST /api/organization/me/members` — invite member to org
- [ ] `DELETE /api/organization/me/members/{userId}` — remove member
- [ ] `GET /api/organization/me/reports` — org's report list with analytics
- [ ] `POST /api/organization/me/promote-report` — request featured slot for a report

### AI Pipeline (Google Gemini 3 Flash)
- [ ] `IGeminiService` + `GeminiService` — wrapper for Gemini 3 Flash API calls
- [ ] `IAiPipelineService` — orchestrates AI jobs
- [ ] `POST /api/ai/reports/{id}/summarize` — trigger summary + key findings generation
- [ ] `POST /api/ai/reports/{id}/translate` — trigger AI translation (AR↔EN)
- [ ] `POST /api/ai/reports/{id}/insights` — extract indicators and trends
- [ ] `POST /api/ai/compare` — compare 2+ reports with similarity scoring
- [ ] `GET /api/ai/jobs/{id}` — poll AI job status (queued/processing/done/failed)
- [ ] Background job queue (Hangfire or IHostedService) for async AI processing
- [ ] Token usage tracking per job

### Infographics
- [ ] `POST /api/reports/{id}/infographics` — generate infographic (bar/pie/line/radar) from AI extracted data
- [ ] `GET /api/reports/{id}/infographics` — list report infographics
- [ ] `GET /api/infographics/{id}/export` — export as PNG/SVG/PDF

### User Dashboard
- [ ] `GET /api/users/me/dashboard` — summary: saved reports, recent views, active subscription, AI usage
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
- [ ] Sentry SDK integration (backend — `SENTRY_DSN` env var)
- [ ] Structured logging with Sentry breadcrumbs

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

**Still needed (set when features are built):**

| Variable | Where Used |
|---|---|
| `REFRESH_TOKEN_SECRET` | Refresh token signing |
| `MISER_SECRET_KEY` | Miser payment API key |
| `MISER_WEBHOOK_SECRET` | Miser webhook HMAC verification |
| `UNIFONIC_API_KEY` | OTP/SMS login |
| `UNIFONIC_SENDER_ID` | SMS sender ID |
| `GEMINI_API_KEY` | Google Gemini 3 Flash |
| `SENTRY_DSN` | Error monitoring |
| `CLOUD_STORAGE_BUCKET` | Report file uploads (GCS) |

---

## Conventions

- All new controllers follow pattern in `AuthController.cs`
- All new services register in `ServiceExtensions.cs`
- All new DTOs go in `Application/DTOs/{Feature}/`
- All new entity configurations go in `Infrastructure/Data/Configurations/`
- Soft-delete entities extend `SoftDeletableEntity`, others extend `BaseEntity`
- Slugs generated in service layer, not controller
- Never expose `PasswordHash` or `TokenHash` in responses
- All list endpoints must be paginated
- Arabic (`Ar`) and English (`En`) fields on all public-facing entities
