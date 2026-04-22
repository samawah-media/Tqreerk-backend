# Taqreerk Backend — Requirements & Progress Tracker

> **Platform:** Taqreerk (تقريرك) — Arabic SaaS report aggregation platform  
> **Owner:** شركة سماوة | **Builder:** ProCode Solutions  
> **Stack:** ASP.NET Core 8 · PostgreSQL · Google Cloud Run · Clean Architecture  
> **Last updated:** 2026-04-22 (session 2)

---

## How to use this file

- `[x]` = Done and merged
- `[ ]` = Not started or in progress
- Each item maps to a deliverable from the PRD or project brief
- Any agent starting work should read this file first, then mark items `[x]` as they complete them

---

## Phase 1 — Foundation (~80% done)

### Infrastructure

- [x] Clean architecture: `Domain / Application / Infrastructure / API` layers
- [x] PostgreSQL via Npgsql EF Core 8 (`TaqreerkDbContext`)
- [x] `appsettings.json` + `appsettings.Development.json`
- [x] Swagger/OpenAPI with JWT Bearer security scheme
- [x] CORS policy (localhost:5173/3000 dev, taqreerk.com prod)
- [x] Global exception handling middleware (`ExceptionHandlingMiddleware`)
- [x] Soft-delete base class (`SoftDeletableEntity`) with global query filters
- [x] Audit timestamps base class (`BaseEntity`, `AuditableEntity`)
- [x] GitHub Actions CI/CD — lint, build, test, deploy to Cloud Run (ci.yml + deploy-staging.yml + deploy-production.yml)
- [x] Dockerfile + .dockerignore for containerized Cloud Run deploys
- [x] Auto-migrate EF Core on container startup (`db.Database.Migrate()` in Program.cs)
- [x] Staging Cloud Run service verified and accessible (`taqreerk-backend-staging`)
- [x] Production Cloud Run service deployed (`taqreerk-backend-prod`)

### Database

- [x] All 27 entities modeled and EF configured
- [x] Initial migration created (`20260421224853_InitialCreate`)
- [x] Full-text search: `tsvector` GIN index on `reports.search_vector`
- [x] JSONB columns: permissions, AI output, chart data, metadata
- [x] UUID PKs, `now()` defaults, soft-delete filters
- [x] Migration applied to `taqreerk_staging` (Cloud SQL) — confirmed working in Cloud Run logs
- [ ] Migration applied to `taqreerk_production` (Cloud SQL) — needs production deploy
- [ ] Seed: roles (`admin`, `editor`, `partner`, `researcher`, `subscriber`)
- [ ] Seed: sectors (economy, education, technology, investment, health, energy, environment)
- [ ] Seed: Arab countries with ISO codes

### Authentication — `AuthController` `/api/auth`

- [x] `POST /register/individual` — full name, email/phone, password + profile fields
- [x] `POST /register/organization` — org name, email, password, sector, country
- [x] `POST /login` — email + password → JWT access token (15 min) + HttpOnly refresh cookie (7 day)
- [x] `POST /refresh` — token rotation, revokes old refresh token
- [x] `POST /logout` — revoke refresh token, clear cookie
- [x] BCrypt password hashing
- [x] SHA-256 refresh token hashing before DB storage
- [x] IP address + device info logged per token
- [ ] `POST /forgot-password` — send reset link via email
- [ ] `POST /reset-password` — verify token, set new password
- [ ] `POST /verify-email` — verify email address via token
- [ ] Unifonic OTP: `POST /request-otp` — send SMS OTP
- [ ] Unifonic OTP: `POST /verify-otp` — verify OTP, return tokens

### User Profile — `UsersController` `/api/users`

- [ ] `GET /me` — current user profile
- [ ] `PUT /me` — update name, job title, interest field, country, language preference
- [ ] `POST /me/interests` — set followed sectors / organizations / countries
- [ ] `GET /me/interests` — get current interests
- [ ] `GET /me/saved-reports` — personal saved reports library
- [ ] `GET /me/comparisons` — AI comparison history
- [ ] `GET /me/dashboard` — stats: saved count, recent views, AI usage, subscription summary

---

## Phase 2 — Payments

### Plans — `PlansController` `/api/plans`

- [ ] `GET /` — list active plans (individual + organizational)
- [ ] `GET /{id}` — single plan detail

### Subscriptions & Miser — `SubscriptionsController` `/api/subscriptions`

- [ ] `IPaymentService` interface + `MiserPaymentService` implementation
- [ ] `POST /checkout` — create Miser checkout session, return payment URL
- [ ] `POST /webhook` — handle Miser events: `payment.success`, `payment.failed`, `subscription.cancelled`, `subscription.renewed`
- [ ] HMAC signature verification on webhook endpoint
- [ ] `GET /current` — active subscription with usage stats
- [ ] `POST /cancel` — cancel at period end
- [ ] `POST /upgrade` — switch plan (prorate calculation)
- [ ] Subscription lifecycle: `active → grace_period → expired`
- [ ] Plan limit enforcement: AI calls, downloads, featured report slots
- [ ] `IUsageService` — track and check usage against plan limits

### Invoices — `InvoicesController` `/api/invoices`

- [ ] `IInvoiceService` + PDF generation (QuestPDF recommended)
- [ ] Auto-generate invoice on successful payment webhook
- [ ] Email invoice PDF to user on generation
- [ ] `GET /` — paginated invoice history
- [ ] `GET /{id}/download` — stream PDF file

### Admin — Revenue

- [ ] `GET /api/admin/subscriptions` — all subscriptions, filter by plan/status
- [ ] `GET /api/admin/revenue` — MRR, churn rate, revenue by plan
- [ ] `GET /api/admin/users` — all users, filter by role/status/plan
- [ ] `PUT /api/admin/users/{id}/status` — activate / suspend user
- [ ] `GET /api/admin/audit-logs` — searchable audit log

---

## Phase 3 — Core Features

### Reference Data

- [ ] `GET /api/sectors` — all sectors with `nameAr` / `nameEn`
- [ ] `GET /api/countries` — all countries with `nameAr` / `nameEn` / `isoCode`

### Reports — `ReportsController` `/api/reports`

- [ ] `IReportService` interface + `ReportService` implementation
- [ ] `IGcsStorageService` — Google Cloud Storage upload/download
- [ ] `POST /` — upload PDF + metadata (org admins only), stores file in GCS
- [ ] `GET /` — paginated library: filter by sector, country, org, year, language, source type
- [ ] `GET /search` — PostgreSQL full-text search with tsvector + ranked results
- [ ] `GET /{slug}` — public report detail (title, org, sector, country, summary, keywords, related)
- [ ] `PUT /{id}` — update metadata (org admin or platform admin)
- [ ] `DELETE /{id}` — soft delete
- [ ] `POST /{id}/view` — record view (anonymous or authenticated)
- [ ] `GET /{id}/download` — stream file, check subscription download limit

### Report Interaction

- [ ] `POST /{id}/rate` — submit rating 1–5 with optional review text
- [ ] `GET /{id}/ratings` — paginated ratings
- [ ] `POST /{id}/recommend` — mark as recommended, record share channel
- [ ] `POST /{id}/save` — save to personal library
- [ ] `DELETE /{id}/save` — unsave

### Organizations — `OrganizationsController` `/api/organizations`

- [ ] `GET /` — list partner organizations with report count
- [ ] `GET /{slug}` — public org page: profile + paginated reports
- [ ] `GET /me` — current user's org (requires org admin role)
- [ ] `PUT /me` — update org profile (name, description, logo, website)
- [ ] `POST /me/members` — invite member by email
- [ ] `DELETE /me/members/{userId}` — remove member
- [ ] `GET /me/reports` — org's reports with analytics (views, downloads per report)
- [ ] `POST /me/promote/{reportId}` — request featured slot (deducts from monthly quota)
- [ ] `GET /me/analytics` — org-level analytics summary

### AI Pipeline — `AiController` `/api/ai`

- [ ] `IGeminiService` — wraps Gemini 3 Flash HTTP calls
- [ ] `IAiPipelineService` — orchestrates job creation, queuing, and result storage
- [ ] Background job runner (`IHostedService` or Hangfire) for async processing
- [ ] `POST /reports/{id}/summarize` — create AI job: summary + key findings + recommendations
- [ ] `POST /reports/{id}/translate` — create AI job: translate summary/title AR↔EN
- [ ] `POST /reports/{id}/insights` — create AI job: extract indicators, trends, sector signals
- [ ] `POST /reports/{id}/keywords` — create AI job: extract keywords
- [ ] `POST /compare` — create AI comparison job for 2–5 reports with similarity scoring
- [ ] `GET /jobs/{jobId}` — poll job status (`queued / processing / done / failed`)
- [ ] Token usage tracking per `AiJob` record

### Infographics — `InfographicsController` `/api/infographics`

- [ ] `POST /reports/{id}/infographics` — generate chart (bar/pie/line/radar) from AI extracted indicators
- [ ] `GET /reports/{id}/infographics` — list report's infographics
- [ ] `GET /{id}/export?format=png|svg|pdf` — export infographic file

### Notifications — `NotificationsController` `/api/notifications`

- [ ] `INotificationService` — dispatch notifications on events
- [ ] `GET /` — paginated notification list for current user
- [ ] `POST /{id}/read` — mark single notification as read
- [ ] `POST /read-all` — mark all as read
- [ ] Dispatch: new report in followed sector/org/country
- [ ] Dispatch: featured report published
- [ ] Dispatch: AI job completed

### Admin — Content & Operations

- [ ] `GET /api/admin/reports` — reports with pending approval queue
- [ ] `PUT /api/admin/reports/{id}/approve` — approve partner-uploaded report
- [ ] `PUT /api/admin/reports/{id}/reject` — reject with reason (sends notification)
- [ ] `GET /api/admin/organizations` — list all orgs with verification status
- [ ] `PUT /api/admin/organizations/{id}/verify` — verify/unverify org
- [ ] `GET /api/admin/stats` — KPIs: total reports, registered orgs, active users, searches/day, downloads/sector
- [ ] `PUT /api/admin/featured-content` — set featured reports on homepage

### Monitoring

- [ ] Sentry SDK installed (`Sentry.AspNetCore` NuGet package)
- [ ] `SENTRY_DSN` environment variable wired up
- [ ] Structured error context (user id, request id) sent to Sentry

### Testing

- [ ] `Taqreerk.Tests` xUnit test project added to solution
- [ ] `AuthService` unit tests (register, login, token rotation)
- [ ] `TokenService` unit tests (generate, validate, expired token)
- [ ] `ReportService` unit tests
- [ ] `AiPipelineService` unit tests
- [ ] Integration test: register + login + refresh + logout flow (WebApplicationFactory)
- [ ] Integration test: upload report + search + download

---

## Phase 4 — Hardening

### Security

- [ ] CSP / security headers middleware (X-Content-Type-Options, X-Frame-Options, etc.)
- [ ] Rate limiting on `/api/auth/*` (ASP.NET Core built-in rate limiting)
- [ ] FluentValidation on all DTOs (no unvalidated user input reaches service layer)
- [ ] Verify no raw SQL — all queries through EF Core parameterized
- [ ] Webhook endpoints verify HMAC signatures before processing

### Performance

- [ ] All list endpoints paginated (page + pageSize or cursor)
- [ ] N+1 query audit — verify `.Include()` chains are correct
- [ ] Response caching headers on sectors, countries, plans (rarely change)
- [ ] Database indexes audit — run `EXPLAIN ANALYZE` on search and library queries

### Load Testing

- [ ] k6 script: report search under 100 VUs
- [ ] k6 script: auth flow (register → login → refresh)

---

## Phase 5 — Delivery

- [ ] All env vars set in GCP Secret Manager (`sadeed-production` project)
- [ ] Production Cloud Run service deployed and healthy
- [ ] `taqreerk_production` migration applied
- [ ] Swagger UI accessible at production URL
- [ ] Postman collection exported and shared
- [ ] README.md complete (local setup, env vars, migration commands)
- [ ] Architecture diagram
- [ ] Post-handover document: env vars, backup/restore, deployment commands, monthly GCP cost estimate
- [ ] Full account ownership transfer to client

---

## Conventions for Agents

| Rule | Detail |
|---|---|
| New controllers | Follow `AuthController.cs` pattern — inject service interface, return consistent JSON |
| New services | Add interface to `Application/Interfaces/`, implementation to `Application/Services/`, register in `ServiceExtensions.cs` |
| New DTOs | Place in `Application/DTOs/{FeatureName}/` folder |
| New EF configs | Place in `Infrastructure/Data/Configurations/`, auto-discovered by `ApplyConfigurationsFromAssembly()` |
| Pagination | Every list endpoint takes `int page = 1, int pageSize = 20` and returns `{ data, totalCount, page, pageSize }` |
| Soft delete entities | Use `SoftDeletableEntity` base; plain entities use `BaseEntity` |
| Arabic + English | All user-visible entity fields have `NameAr`/`NameEn` variants |
| Never expose | `PasswordHash`, `TokenHash` — never include in response DTOs |
| Slugs | Generate in service layer from `NameAr` or `NameEn`, ensure uniqueness |
| Error responses | All errors flow through `ExceptionHandlingMiddleware`; throw typed exceptions in services |
| AI jobs | Always create an `AiJob` record first, process async, update status on completion |
