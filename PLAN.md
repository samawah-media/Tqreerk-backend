# Taqreerk Backend — Master Plan & Requirements Tracker

## Context

Taqreerk (تقريرك) is an Arabic-first SaaS platform for aggregating, searching, and AI-analyzing Arabic reports. The client is شركة سماوة. The backend is `Taqreerk.back` — ASP.NET Core 8, PostgreSQL (Cloud SQL on GCP), Clean Architecture, deployed on Google Cloud Run.

**Current state (as of 2026-05-04):** Phase 1 done. Phase 3 (core features + admin moderation + freemium gating) substantially done; Phase 2 (payments) still untouched. Three Python services in production: `ai-service` (LangGraph chat agent + RAG), `doc-processor` (layout-aware ingest with OCR + table/figure/formula extraction + chunking + Vertex embeddings), and the .NET API.

What's live in .NET: full auth (incl. 2FA for admins, lockout, sessions, OTP, password reset, email verification, RBAC), individual + organization onboarding, organization portal (members, invitations, scope, contact, files, analytics), reports upload pipeline with GCS + AI orchestration, public reports + facets + featured/trending/recent + slug detail + related, public stats overview, admin moderation suite (reviews queue, claim/release, approve/reject/return-for-edit, AI status/regenerate, ban/unban, organization verification/suspension, featured-slot management, sectors+countries CRUD, system settings, staff with 2FA reset, action logs, dashboard quick-stats), per-saved-report annotations + personal notepad, report comments, rate/save/recommend/view interactions, points (balance + history), freemium usage counters with advisory-locked consume path, chat SSE proxy.

What's live in Python: ai-service exposes ingest/summarize/translate/insights/compare + bulk variants + jobs; chat session/messages/history/list with LangGraph agent (groundedness check, query rewriter, reranker, HyQE, observability, chat_cache, chunk_embedding_cache). doc-processor provides extract endpoints with arabic_normalize, OCR, layout, tables, figures, formulas, embeddings (BGE-M3 / Vertex), and a reranker.

Remaining big chunks: Phase 2 (Miser payments, invoices, plan management endpoints), notifications, infographics, Unifonic SMS, frontend testing.

**Purpose of this file:** Track every deliverable from the PRD and project brief. Mark `[x]` when done so any agent picking up the project knows exactly what's left.

---

## Architecture Overview

```
Tqreerk-backend/
├── .github/workflows/
│   ├── ci.yml                                  ← Build & test on push/PR ✅
│   ├── deploy-staging.yml                      ← .NET → Cloud Run staging ✅
│   ├── deploy-production.yml                   ← .NET → Cloud Run production ✅
│   ├── deploy-ai-service-staging.yml           ← ai-service → Cloud Run staging ✅
│   ├── deploy-ai-service-production.yml        ← ai-service → Cloud Run production ✅
│   ├── deploy-doc-processor-staging.yml        ← doc-processor → Cloud Run staging ✅
│   └── deploy-doc-processor-production.yml     ← doc-processor → Cloud Run production ✅
├── Dockerfile                                  ← .NET multi-stage build ✅
├── .dockerignore                               ✅
├── production-db-setup.md                      ← runbook for prod DB (pgvector, GIN, HNSW, EF history) ✅
├── test-chat-stream.html                       ← local test page for SSE chat streaming ✅
├── ai-service/                                 ← FastAPI: chat agent + RAG + jobs ✅
│   ├── main.py
│   ├── Dockerfile                              ← python:3.12-slim + libmupdf
│   ├── requirements.txt
│   ├── core/{config,db,prompts,chunking}.py
│   ├── services/
│   │   ├── gemini.py                           ← google-genai SDK
│   │   ├── translate.py                        ← Translate v3 + Gemini fallback
│   │   ├── access.py                           ← per-user authz checks
│   │   ├── chat_cache.py                       ← turn-level cache
│   │   ├── doc_extractor.py
│   │   ├── embed.py / embedding_cache.py       ← cached chunk embeddings
│   │   ├── eval_models.py / groundedness.py    ← answer faithfulness scoring
│   │   ├── hyqe.py                             ← hypothetical-question expansion
│   │   ├── observability.py                    ← per-step latency + tracing
│   │   ├── query_rewriter.py
│   │   ├── quota.py                            ← AI-side enforcement
│   │   ├── reranker.py
│   │   └── tools.py                            ← LangGraph tools
│   ├── pipelines/{ingest,jobs,agent,eval}.py
│   ├── models/{chat,ingest}.py
│   └── api/{chat,reports}.py
├── doc-processor/                              ← FastAPI: layout-aware ingest + OCR ✅
│   ├── main.py
│   ├── Dockerfile.gpu
│   ├── cloudbuild.yaml
│   ├── core/{config,db}.py
│   ├── models/schema.py
│   ├── api/extract.py
│   └── pipeline/
│       ├── arabic_normalize.py
│       ├── chunker.py
│       ├── embeddings.py / vertex_embedder.py
│       ├── figures.py / formulas.py / tables.py
│       ├── ingest.py / orchestrator.py
│       ├── layout.py / ocr.py
│       └── reranker.py
├── scripts/check_chunker_sync.py               ← guards .NET ↔ python chunker drift ✅
└── Tqreerk-backend/
    ├── API/Controllers/
    │   ├── Auth, Users, Rbac, Reference, Reports, PublicReports,
    │   │ Organizations, Invitations, Chat, Usage, Points, Me,
    │   │ ReportInteractions, ReportComments, Annotations, PublicStats
    │   └── Admin{Auth, Dashboard, Users, Staff, Reviews, Organizations,
    │      Featured, Categories, Settings, Stats}
    ├── API/Authorization/                      ← RequirePlatformStaff, etc.
    ├── API/Middleware/                         ← ExceptionHandlingMiddleware ✅
    ├── Application/DTOs/                       ← Auth, Admin, Annotations, Analytics,
    │                                              Dashboard, Me, Organizations, Points,
    │                                              Rbac, Reports, Usage, Users
    ├── Application/Interfaces/                 ← 30+ interfaces
    ├── Application/Services/                   ← Auth, Token, User, Rbac, Report,
    │                                              ReportAi, Organization, OrganizationAnalytics,
    │                                              PublicReport, Dashboard, Review, Quota, Usage,
    │                                              Points, Annotations, ReportInteractions,
    │                                              ReportComments, Me, Staff, TwoFactor,
    │                                              AdminAuth, AdminUsers, AdminOrganizations,
    │                                              AdminCategories, AdminFeatured, AdminStats,
    │                                              AdminSettings, AdminDashboard, AdminActionLogger,
    │                                              DataProtectorEncryption, Smtp/LogEmailSender
    ├── Application/Settings/                   ← JwtSettings, AiServiceSettings ✅
    ├── Domain/Entities/                        ← 47 entities (see list below)
    ├── Domain/Enums/                           ← all enums ✅
    ├── Domain/Common/                          ← BaseEntity, AuditableEntity, SoftDeletableEntity ✅
    ├── Infrastructure/AI/                      ← AiServiceClient, ReportProcessingWorker ✅
    ├── Infrastructure/Storage/                 ← GcsFileStorage, LocalFileStorage ✅
    ├── Infrastructure/Data/                    ← TaqreerkDbContext, configurations, 33 migrations
    ├── Infrastructure/Data/Seed/               ← RbacSeedData, ReferenceSeedData ✅
    └── Extensions/                             ← ServiceExtensions ✅
```

**Domain entities (47 total):** User, Role, Permission, RolePermission, UserRole, RefreshToken, EmailVerificationToken, PasswordResetToken, Admin2faSecret, AdminActionLog, Organization, OrganizationProfile, OrganizationMember, OrganizationInvitation, OrganizationFile, Country, Sector, Plan, Subscription, UsageTracking, UserPoints, PointTransaction, Report, ReportAiContent, ReportTranslation, ReportChunk, ReportPage (legacy), ReportRating, ReportReview, ReportRecommendation, ReportComment, ReportPersonalNote, ReportAnnotation, SavedReport, ReportComparison, ReportKeyword, ReportView, FeaturedReport, ChatSession, ChatMessage, AiJob, Notification, Infographic, Invoice, Payment, AuditLog, SystemSetting, UserInterest, Page.

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
- [x] 47 entities designed and configured (initial 27 + 20 added across feature waves)
- [x] Initial migration (`20260421224853_InitialCreate`)
- [x] PostgreSQL full-text search tsvector + GIN indexes (reports + report_chunks)
- [x] JSONB columns for flexible data (permissions, AI output, chart data, chunk metadata)
- [x] UUID primary keys with `gen_random_uuid()`
- [x] Soft delete query filters applied globally
- [x] Migration applied to `taqreerk_staging` — auto-applied on container startup ✅
- [x] `ChatSession`, `ChatMessage`, `ReportPage` entities + EF configs ✅
- [x] Migration `20260424000000_AddAiServiceTables` — pgvector extension + 3 new tables ✅
- [x] Migration `20260425073458_Feature2_LockoutAndAiTables` ✅
- [x] Migration `20260425083823_Feature3_SeedCountriesAndSectors` ✅
- [x] Migration `20260425101038_Feature5_OrganizationInvitations` ✅
- [x] Migration `20260426000000_AddReportPagesFullTextSearch` ✅
- [x] Migration `20260427072658_Feature6_FixSearchVectorTrigger` ✅
- [x] Migration `20260427072700_Feature6_AddTranslatedFileUrl` ✅
- [x] Migration `20260428073639_Admin_ReviewWorkflow` — review states + claim columns ✅
- [x] Migration `20260428103039_Admin_ReportReviews` — `report_reviews` table ✅
- [x] Migration `20260428121546_Admin_ActionLogs` — `admin_action_logs` table ✅
- [x] Migration `20260428172909_Admin_TwoFactor` — `admin_2fa_secrets` table ✅
- [x] Migration `20260429000000_ReplaceReportPagesWithReportChunks` — chunk-based RAG ✅
- [x] Migration `20260429000100_AddChatCache` — turn-level chat cache table ✅
- [x] Migration `20260429120000_BgeM3VectorDimensions` — switch chunk embedding to BGE-M3 ✅
- [x] Migration `20260430165907_Feature6_Categories` — admin-managed sector/country metadata ✅
- [x] Migration `20260430210000_VertexEmbeddingDimensions` — back to Vertex AI dims ✅
- [x] Migration `20260501065432_Feature7_FeaturedReports` — homepage curation ✅
- [x] Migration `20260501084559_Feature10_SystemSettings` — `system_settings` key/value ✅
- [x] Migration `20260501100000_Feature8_OrgTranslationFlag` ✅
- [x] Migration `20260501180912_Feature_ReportComments` ✅
- [x] Migration `20260502000000_AddReportChunksMetadataGinIndex` — GIN on chunk metadata ✅
- [x] Migration `20260502010000_AddReportAiContentTopicsColumn` ✅
- [x] Migration `20260502120207_Feature5_UsageTracking` — `usage_tracking` table ✅
- [x] Migration `20260502132624_Feature5b_PointsAndMe` — `user_points` + `point_transactions` ✅
- [x] Migration `20260502162548_Feature_ReportAnnotations` ✅
- [x] Migration `20260503094219_Feature_AnnotationType` ✅
- [x] Migration `20260504100000_AddChunkEmbeddingCache` — embedding cache table ✅
- [x] Migration `20260504100002_AddReportChunksHyqeColumns` — HyQE columns ✅
- [x] Migration `20260504100003_AddHnswVectorIndexes` — HNSW indexes on chunks ✅
- [x] pgvector embedding column on `report_chunks` (HNSW indexes for fast ANN; per-report filter still applied) ✅
- [x] Staging DB: pgvector + GIN + HNSW applied ✅
- [ ] Production DB: same steps documented in [production-db-setup.md](production-db-setup.md) — apply before first prod deploy
- [x] Seed: RBAC roles + permissions via `RbacSeedData` ✅
- [x] Seed: countries (Arab + global) + sectors via `ReferenceSeedData` ✅

### Authentication (JWT + Refresh Tokens)
- [x] `POST /api/auth/register/individual`
- [x] `POST /api/auth/register/organization` (with admin user)
- [x] `POST /api/auth/login` — returns JWT + HttpOnly refresh cookie
- [x] `POST /api/auth/refresh` — token rotation (revoke old, issue new pair)
- [x] `POST /api/auth/logout`
- [x] BCrypt password hashing
- [x] JWT 15-min expiry (60-min in dev), Refresh 7-day expiry
- [x] Token hashed (SHA256) before database storage
- [x] IP address + device info tracking on tokens
- [x] `POST /api/auth/verify-email/send` + `verify-email/confirm` ✅
- [x] `POST /api/auth/otp/email/send` + `resend` + `verify` ✅
- [x] `POST /api/auth/forgot-password` ✅
- [x] `POST /api/auth/reset-password` ✅
- [x] `SmtpEmailSender` (production) + `LogEmailSender` (dev) behind `IEmailSender` ✅
- [x] Account lockout (failed-login throttling) ✅
- [x] Session management: `GET /api/auth/sessions`, `DELETE sessions/{id}`, `POST logout-all` ✅
- [x] `GET /api/auth/me/permissions` — current user effective permissions ✅
- [ ] Unifonic OTP/SMS login (mobile authentication) — email OTP works; SMS still pending

### User Profile
- [x] `GET /api/users/me` ✅
- [x] `PUT /api/users/me` (name, job title, interest field, country, language) ✅
- [x] `POST /api/users/me/interests` ✅
- [x] `GET /api/users/me/interests` ✅
- [x] `POST /api/users/me/change-password` ✅
- [x] `GET /api/me/saved-reports` ✅
- [x] `GET /api/me/activity` ✅
- [x] `GET /api/me/recommendations` ✅

---

## Phase 2 — Payments (Days 8–15)

### Subscription Plans
- [ ] `GET /api/plans` — list all active plans with pricing
- [ ] `GET /api/plans/{id}` — get plan details

### Subscriptions & Miser (ميسر) Integration
- [ ] `IPaymentService` interface + `MiserPaymentService` implementation
- [ ] `POST /api/subscriptions/checkout` — initiate subscription purchase via Miser
- [ ] `POST /api/subscriptions/webhook` — handle all Miser webhook events
- [ ] `GET /api/subscriptions/current` — get current user/org subscription status
- [ ] `POST /api/subscriptions/cancel`
- [ ] `POST /api/subscriptions/upgrade`
- [ ] Subscription lifecycle state machine (active → grace → expired)
- [x] Usage tracking per subscription — `usage_tracking` table + `IUsageService` with advisory-locked count-then-insert ✅
- [x] Plan limit enforcement — `EnsureWithinLimitAndConsumeAsync` wraps gated actions; `QuotaExceededException` → 429 ✅
- [x] Read endpoints: `GET /api/usage/me`, `GET /api/usage/me/history` ✅
- [x] Free-tier auto-link on registration (subscription row created at signup) — wired but recent reports of users missing this row → see Open Issues ⚠
- [x] Points: `IPointsService`, `point_transactions` ledger, `GET /api/me/points`, `GET /api/me/points/history` ✅

### Invoices & Billing
- [ ] `IInvoiceService` + PDF invoice generation (QuestPDF or similar)
- [ ] Automatic invoice creation on successful payment
- [ ] Email delivery of invoice PDF
- [ ] `GET /api/invoices`
- [ ] `GET /api/invoices/{id}/download`

### Admin Dashboard — User & Revenue Management
- [x] `GET /api/admin/users` — list with filters (role, status, plan) ✅
- [x] `GET /api/admin/users/{id}` ✅
- [x] `POST /api/admin/users/{id}/ban` + `/unban` (replaces "PUT status") ✅
- [x] `DELETE /api/admin/users/{id}` ✅
- [x] `GET /api/admin/users/{id}/reports` ✅
- [ ] `GET /api/admin/subscriptions` — all subscriptions with revenue stats (depends on Phase 2)
- [ ] `GET /api/admin/revenue` — MRR, churn, plan breakdown (depends on Phase 2)
- [x] Audit logs — `admin_action_logs` table + `IAdminActionLogger` writes from every admin mutation ✅
- [ ] `GET /api/admin/audit-logs` — viewer endpoint (table + writer exist; reader endpoint TBD)

---

## Phase 3 — Core Features (Days 16–22)

### Reference Data APIs
- [x] `GET /api/sectors` (ar/en names) ✅
- [x] `GET /api/countries` (ar/en names) ✅

### Reports — Upload & Management
- [x] `IReportService` + `ReportService` ✅
- [x] `IReportAiService` + `ReportAiService` — orchestrates GCS upload + ai-service ingest/summarize calls ✅
- [x] `POST /api/reports` — upload (PDF + metadata), restricted to org admins ✅
- [x] `GET /api/reports` — paginated org-scoped list ✅
- [x] `GET /api/reports/{id:guid}` ✅
- [x] `PATCH /api/reports/{id}` — update report metadata ✅
- [x] `DELETE /api/reports/{id}` — soft delete ✅
- [x] `POST /api/reports/{id}/resubmit` — re-submit after Returned-for-edit ✅
- [x] `GET /api/reports/{id}/analytics` — per-report analytics ✅
- [x] `GET /api/reports/{id}/ai-status` ✅
- [x] `POST /api/reports/{id}/regenerate-ai` ✅
- [x] `POST /api/reports/{id}/ai/translate` ✅
- [x] File upload to GCP Cloud Storage via `GcsFileStorage : IFileStorage` ✅
- [x] `ReportProcessingWorker` background worker ✅
- [x] Public full-text search on `/api/public/reports?q=` (PostgreSQL tsvector) ✅
- [x] View tracking: `POST /api/reports/{id}/view` ✅
- [ ] Download tracking + permission check: `GET /api/reports/{id}/download` (view + interactions exist; download endpoint TBD)

### Public Reports (anonymous browse)
- [x] `GET /api/public/reports` (filters: sector, country, organization, year, page count, language, q, sort) ✅
- [x] `GET /api/public/reports/facets` ✅
- [x] `GET /api/public/reports/featured` ✅
- [x] `GET /api/public/reports/trending` ✅
- [x] `GET /api/public/reports/recent` ✅
- [x] `GET /api/public/reports/{slug}` ✅
- [x] `GET /api/public/reports/{slug}/related` ✅
- [x] `GET /api/public/stats/overview` — public KPI strip ✅

### Report Interaction
- [x] `PUT /api/reports/{id}/rating` (1–5 + optional review) ✅
- [x] `DELETE /api/reports/{id}/rating` ✅
- [x] `POST /api/reports/{id}/save` ✅
- [x] `DELETE /api/reports/{id}/save` ✅
- [x] `POST /api/reports/{id}/recommend` ✅
- [x] `DELETE /api/reports/{id}/recommend` ✅
- [x] `POST /api/reports/{id}/view` ✅
- [x] `GET /api/reports/{id}/me` — my interaction state for this report ✅
- [x] `GET /api/me/saved-reports` ✅
- [x] `GET /api/reports/{id}/comments` + `POST` + `DELETE /comments/{commentId}` ✅
- [x] Annotations + personal notepad on saved reports:
  - [x] `GET /api/me/reports/{id}/editor` — bootstrap (report + saved annotations + note) ✅
  - [x] `GET /api/me/reports/{id}/annotations` ✅
  - [x] `POST /api/me/reports/{id}/annotations` ✅
  - [x] `PATCH /api/me/reports/{id}/annotations/{annotationId}` ✅
  - [x] `DELETE /api/me/reports/{id}/annotations/{annotationId}` ✅
  - [x] `GET /api/me/reports/{id}/note` + `PUT /note` ✅

### Organizations — Public & Partner Portal
- [x] `GET /api/organizations/me` ✅
- [x] `PATCH /api/organizations/me/basics` ✅
- [x] `PATCH /api/organizations/me/scope` ✅
- [x] `PATCH /api/organizations/me/reports` ✅
- [x] `PATCH /api/organizations/me/contact` ✅
- [x] `POST /api/organizations/me/files` ✅
- [x] `GET /api/organizations/me/stats` ✅
- [x] `GET /api/organizations/me/recent-activity` ✅
- [x] `GET /api/organizations/me/analytics` (`OrganizationAnalyticsService`) ✅
- [x] `GET /api/organizations/me/members` ✅
- [x] `DELETE /api/organizations/me/members/{userId}` ✅
- [x] `PATCH /api/organizations/me/members/{userId}/role` ✅
- [x] `GET /api/organizations/me/invitations` ✅
- [x] `POST /api/organizations/me/invitations` ✅
- [x] `DELETE /api/organizations/me/invitations/{id}` ✅
- [x] `GET /api/invitations/preview` + `POST /api/invitations/accept` ✅
- [ ] `GET /api/organizations/{slug}` — public organization page with report list
- [ ] `GET /api/organizations` — list partner organizations
- [ ] `POST /api/organizations/me/promote-report` — request featured slot (admin-side curation exists; org-side request flow TBD)

### Admin Moderation Suite
- [x] `IReviewService` + `ReviewService` (claim/release with optimistic concurrency → 409) ✅
- [x] `GET /api/admin/reviews/queue` (filters: sectorId, organizationId, assignedToMe, status, sort, paged) ✅
- [x] `POST /api/admin/reviews/{id}/claim` ✅
- [x] `POST /api/admin/reviews/{id}/release` ✅
- [x] `GET /api/admin/reviews/{id}` — full reviewable payload ✅
- [x] `POST /api/admin/reviews/{id}/approve` ✅
- [x] `POST /api/admin/reviews/{id}/reject` ✅
- [x] `POST /api/admin/reviews/{id}/return-for-edit` ✅
- [x] `GET /api/admin/reviews/{id}/ai-status` ✅
- [x] `POST /api/admin/reviews/{id}/regenerate-ai` ✅
- [x] `RequirePlatformStaff` policy gating all admin routes ✅
- [x] Admin 2FA: `POST /api/admin/auth/2fa/setup`, `/activate`, `/verify`, `/regenerate-backup-codes`, `GET /2fa/status` ✅
- [x] `GET /api/admin/auth/me` ✅

### Admin Dashboard — Content & Operations
- [x] Dashboard quick-stats: `GET /api/admin/dashboard/quick-stats` ✅
- [x] Platform stats overview: `GET /api/admin/stats/overview` ✅
- [x] Featured-content curation:
  - [x] `GET /api/admin/featured` ✅
  - [x] `POST /api/admin/featured` ✅
  - [x] `PATCH /api/admin/featured/{id}` ✅
  - [x] `DELETE /api/admin/featured/{id}` ✅
  - [x] `POST /api/admin/featured/sections/{section}/reorder` ✅
- [x] Sector/country admin (Arabic-first CRUD with reorder):
  - [x] `GET /api/admin/sectors` + `POST` + `PATCH /{id}` + `DELETE /{id}` + `POST /sectors/reorder` ✅
  - [x] `GET /api/admin/countries` + `POST` + `PATCH /{id}` + `DELETE /{id}` + `POST /countries/reorder` ✅
- [x] Organization admin:
  - [x] `GET /api/admin/organizations` ✅
  - [x] `GET /api/admin/organizations/{id}` ✅
  - [x] `PATCH /api/admin/organizations/{id}` ✅
  - [x] `POST /api/admin/organizations/{id}/verify` + `/unverify` ✅
  - [x] `POST /api/admin/organizations/{id}/suspend` + `/reactivate` ✅
  - [x] `DELETE /api/admin/organizations/{id}` ✅
  - [x] `GET /api/admin/organizations/{id}/reports` ✅
  - [x] `GET /api/admin/organizations/{id}/members` ✅
- [x] Staff admin:
  - [x] `GET /api/admin/staff` ✅
  - [x] `POST /api/admin/staff` ✅
  - [x] `PATCH /api/admin/staff/{id}/role` ✅
  - [x] `DELETE /api/admin/staff/{id}` ✅
  - [x] `POST /api/admin/staff/{id}/reset-2fa` ✅
- [x] System settings + maintenance:
  - [x] `GET /api/admin/settings` ✅
  - [x] `PATCH /api/admin/settings/{key}` ✅
  - [x] `POST /api/admin/maintenance/enable` + `/disable` ✅
  - [x] `GET /api/admin/health` ✅
- [ ] `GET /api/admin/audit-logs` viewer (writes go to `admin_action_logs`; reader endpoint TBD)
- [ ] Marketer accounts (`GET /api/admin/marketers`)

### AI Pipeline — ai-service (Gemini + chunk-based RAG)

**Auth & infrastructure**
- [x] Unified `google-genai` SDK with dual auth: AI Studio (`GEMINI_API_KEY`) → Vertex AI (ADC) fallback ✅
- [x] Vertex AI default region `me-central1` ✅
- [x] Per-feature configurable models (`gemini_vision_model`, `gemini_chat_model`, `gemini_summary_model`, `gemini_embed_model`) ✅
- [x] All Gemini calls run with `temperature=0.2` ✅
- [x] Centralized prompt library at `core/prompts.py` ✅
- [x] DB connection-string normalizer — Npgsql + libpq URIs ✅
- [x] PDF download supports `gs://` and `https://` ✅
- [x] Sentry integration + global JSON exception handler + `X-Request-ID` middleware ✅
- [x] Per-step latency logging (`embed_text`, `hybrid_sql`, `gemini_first_token`, `stream_total`) + observability service ✅

**Single-report endpoints**
- [x] PDF ingestion pipeline: PyMuPDF → 150 DPI PNG per page → Gemini Vision → embedding → pgvector ✅
- [x] Parallel ingest with `Semaphore(5)` thread pool — empty pages skipped, per-page failures fail loudly ✅
- [x] Migration to chunk-based RAG: page extraction now produces `report_chunks` with metadata + embeddings ✅
- [x] `POST /api/ai/reports/ingest` — async fire-and-forget; returns 202 + `job_id` ✅
- [x] `POST /api/ai/reports/summarize` ✅
- [x] `GET /api/ai/reports/{id}/pages` ✅
- [x] `POST /api/ai/reports/translate` (Translate v3 + Gemini fallback for path-rendered PDFs) ✅
- [x] `POST /api/ai/reports/insights` ✅
- [x] `POST /api/ai/reports/compare` ✅
- [x] Auto-ingest fallback on summarize/translate/insights/compare/pages when no chunk content exists ✅

**Chat (LangGraph agent + streaming RAG)**
- [x] LangGraph agent with tool-calling at `pipelines/agent.py` ✅
- [x] Hybrid retrieval: dense (`embedding <=>`) + sparse (`tsvector @@`) with reranker ✅
- [x] HyQE (hypothetical-question expansion) at `services/hyqe.py` ✅
- [x] Query rewriter at `services/query_rewriter.py` ✅
- [x] Reranker at `services/reranker.py` ✅
- [x] Groundedness check at `services/groundedness.py` ✅
- [x] Chat cache (turn-level) — `chat_cache` table + `services/chat_cache.py` ✅
- [x] Embedding cache — `chunk_embedding_cache` table + `services/embedding_cache.py` ✅
- [x] Page-number short-circuit ("page 2" / "صفحة ٢" bypass RAG) ✅
- [x] `POST /api/ai/chat/sessions` — create chat session per user per report ✅
- [x] `POST /api/ai/chat/sessions/{id}/messages` — streaming SSE (sources event → token stream → done) ✅
- [x] `GET /api/ai/chat/sessions/{id}` — full session history ✅
- [x] `GET /api/ai/chat/reports/{id}/sessions` ✅
- [x] Producer/consumer thread bridge for token-level flushing ✅

**Bulk async endpoints**
- [x] `POST /api/ai/reports/bulk/ingest-summarize` (async, returns 202 + job IDs) ✅
- [x] `POST /api/ai/reports/bulk/translate` ✅
- [x] `GET /api/ai/reports/jobs/{job_id}` ✅
- [x] Bounded concurrency (semaphore=3) ✅
- [x] Python `job_type` strings aligned with .NET `AiJobType` ✅

**.NET integration**
- [x] `IAiServiceClient` + `AiServiceClient` ✅
- [x] `IFileStorage` + `GcsFileStorage` + `LocalFileStorage` ✅
- [x] `POST /api/reports` → GCS upload + ai-service ingest via `ReportAiService` ✅
- [x] `ReportProcessingWorker` polling `ai_jobs` ✅
- [x] `GET /api/reports/{id}/ai-status` reads `ai_jobs` directly via EF ✅
- [x] `POST /api/reports/{id}/regenerate-ai` ✅
- [x] `POST /api/reports/{id}/ai/translate` ✅
- [x] `TranslatedFileUrl` column on `ReportTranslation` ✅
- [x] Chat proxy in .NET (`/api/chat/sessions/*`) — JWT validated, user_id forwarded, SSE pumped through ✅
- [ ] Stale-job cleanup cron — mark jobs `Processing > 30min` as Failed (defensive, for instance recycle)

**Deployment**
- [x] GitHub Actions `deploy-ai-service-staging.yml` + `deploy-ai-service-production.yml` ✅
- [x] Both pass `GEMINI_API_KEY` (optional), `DATABASE_URL_*`, `GCS_BUCKET`, `GCP_PROJECT_ID` ✅

### AI Pipeline — doc-processor (layout-aware ingestion)
- [x] Separate FastAPI service deployed to Cloud Run via `deploy-doc-processor-staging.yml` + `production.yml` ✅
- [x] Endpoints in `api/extract.py` ✅
- [x] Pipeline modules: layout, OCR, tables, figures, formulas, arabic_normalize, chunker, embeddings (BGE-M3 + Vertex), reranker, orchestrator, ingest ✅
- [x] GPU Dockerfile (`Dockerfile.gpu`) + `cloudbuild.yaml` for GPU pool ✅
- [x] `scripts/check_chunker_sync.py` — drift guard against the .NET-side chunker ✅

### Infographics
- [ ] `POST /api/reports/{id}/infographics`
- [ ] `GET /api/reports/{id}/infographics`
- [ ] `GET /api/infographics/{id}/export` (PNG/SVG/PDF)

### User Dashboard
- [x] `IDashboardService` + `DashboardService` + DashboardDtos ✅
- [x] `GET /api/me/saved-reports`, `GET /api/me/activity`, `GET /api/me/recommendations` ✅
- [ ] `GET /api/me/comparisons` — history of AI comparisons
- [ ] Standalone dashboard controller surface (current "/me" surface covers most)

### Notifications
- [ ] `GET /api/notifications` (paginated)
- [ ] `POST /api/notifications/{id}/read`
- [ ] `POST /api/notifications/read-all`
- [ ] Notification dispatch service (new report in followed sector/org, featured reports)

### Error Monitoring
- [x] Sentry SDK integrated in .NET (`Sentry.AspNetCore`, reads `Sentry__Dsn`) ✅
- [x] Sentry integrated in ai-service (`sentry-sdk[fastapi]`, `ENVIRONMENT` tag) ✅
- [x] Global exception handler in ai-service — JSON with `request_id`, `error`, `type`, `detail` ✅
- [x] `X-Request-ID` middleware ✅
- [x] Ingest pipeline skips empty pages ✅
- [ ] Structured logging with Sentry breadcrumbs (optional polish)

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
- [x] HNSW vector indexes on `report_chunks` (migration `20260504100003_AddHnswVectorIndexes`) ✅
- [x] GIN index on `report_chunks.metadata` ✅
- [x] GIN tsvector index on `report_chunks.content` ✅
- [x] Embedding cache + chat cache reducing repeated Gemini calls ✅
- [ ] Database indexes audit (verify EF configs produce correct indexes)
- [x] Pagination on all list endpoints (admin queue, public reports, comments, history, etc.) ✅
- [ ] N+1 query audit with `.Include()` chains
- [x] Response caching for `/api/public/reports` (60s) ✅
- [ ] Response caching for reference data (sectors, countries, plans)

### Load Testing
- [ ] k6 or NBomber load test on report search endpoint
- [ ] k6 load test on auth endpoints

---

## Phase 5 — Delivery (Days 28–30)

- [ ] Production deployment on Google Cloud Run (`sadeed-production` project) — staging is green; prod cutover is pending
- [ ] Environment variables set in GCP Secret Manager
- [ ] Swagger UI live at production URL
- [ ] Postman Collection exported
- [ ] README.md with setup, env vars, local dev instructions
- [ ] Architecture diagram
- [ ] Post-handover document (env vars, backup/restore, deployment guide, monthly cost)
- [ ] Full account + code ownership transfer to client

---

## Phase 6 — Frontend Performance Enablement (Lighthouse ≥ 90, LCP ≤ 2.5s on 4G)

These are browser-rendered targets. The backend cannot produce them, but it
owns TTFB, payload size, cache headers, and image delivery — roughly the
first 800–1000 ms of any LCP. Frontend owns the rest (bundle, fonts,
render-blocking JS, layout). Backend contract: **p75 TTFB ≤ 600 ms** on every
endpoint the SPA hits during first paint, **≤ 20 KB compressed** for
`/public/reports` page-1 default, and stable cacheable URLs for the hero
image.

### P0 — Backend-side LCP enablers ✅ (2026-05-13)

- [x] Response compression (Brotli + Gzip, `CompressionLevel.Fastest`,
      `EnableForHttps=true`) — wired in `ServiceExtensions.AddPerformance()`
      and `app.UseResponseCompression()` early in the pipeline. Expected
      ~80% shrink on `/public/reports` JSON, ~150–300 ms LCP win on 4G.
- [x] `AddOutputCache()` + `app.UseOutputCache()` with named policies
      (`PublicList`, `PublicFacets`, `PublicFeatured`, `PublicTrending`,
      `PublicRecent`, `PublicRelated`, `PublicStats`, `Reference`).
      `[OutputCache(PolicyName=...)]` added alongside `[ResponseCache]` on
      `PublicReportsController` (5 of 6 endpoints — slug detail excluded
      to keep cache memory bounded), `ReferenceController` (countries +
      sectors), and `PublicStatsController`. ResponseCache headers kept so
      CDN/browser caching still works.
- [x] CORS preflight cache — `.SetPreflightMaxAge(TimeSpan.FromHours(2))`
      on the `DefaultCors` policy. Saves one ~150 ms RTT per cold cross-
      origin call.
- [x] Cloud Run startup CPU boost — `--cpu-boost` added to
      `deploy-production.yml` and `deploy-staging.yml`. Doubles CPU during
      container startup, cutting cold-start ~30–50%.
      *(Min-instances bump deferred per direction; staying at 1/0
      prod/staging.)*
- [x] ReadyToRun — `<PublishReadyToRun Condition="...Release...">true`
      in `Tqreerk-backend.csproj`. Native pre-JIT cuts cold-start
      ~30–50% on top of `--cpu-boost`.
- [x] Npgsql pool tuning — `MinPoolSize=5`, `MaxPoolSize=20`,
      `ConnectionIdleLifetime=60` injected via
      `NpgsqlConnectionStringBuilder` in `AddDatabase()`. Keeps 5 warm
      connections per instance and caps blast radius at 20 (vs default
      100) so 20 instances × 20 ≤ Cloud SQL's connection ceiling.
      Operator can still override via env-var connection string.
- [x] Dropped `app.UseHttpsRedirection()` — Cloud Run terminates TLS at
      the LB and only exposes `:443` externally; the redirect was a no-op
      for the happy path and would 307-loop any forwarded HTTP-equivalent
      probe. Kept a comment in `Program.cs` explaining when to re-enable.

### P1 — Image / hero LCP path (TBD)

The LCP element on a public report page is almost certainly the cover image
served from GCS. Today's signed URLs are per-request and uncacheable.

- [ ] Decide hosting model for public report covers (public bucket with
      long max-age vs cached signed URL string).
- [ ] Pre-generate cover variants at upload (thumb 320w / medium 768w /
      full 1280w, WebP + JPEG) and return a `coverUrls` map for `srcset`.

### P2 — CDN + edge (TBD)

- [ ] Cloud CDN in front of Cloud Run (Cloud LB + serverless NEG).
      Anonymous endpoints already emit correct `Cache-Control` from
      `[ResponseCache]`; CDN can honour them as-is.
- [ ] HTTP/3 / QUIC on the LB.
- [ ] `Server-Timing` middleware so frontend RUM can attribute backend
      slice of LCP.

### P3 — Payload hygiene (TBD)

- [ ] Audit `/public/reports` default response size (target ≤ 1.5 KB per
      item compressed; ≤ 20 KB for page=1 pageSize=20).
- [ ] `?fields=` projection or `*-mini` variant for index pages.
- [ ] N+1 audit on public report endpoints (move from Phase 4).

### Acceptance criteria

| Metric | Target | How measured |
|---|---|---|
| Backend p75 TTFB on `/public/reports?page=1` (warm) | ≤ 250 ms | Synthetic 4G probe from Doha |
| Backend p75 TTFB on `/public/reports?page=1` (cold) | ≤ 600 ms | post-deploy curl |
| Backend p75 TTFB on `/public/reports/{slug}` | ≤ 300 ms | same |
| `/public/reports` compressed payload | ≤ 20 KB at page=1, pageSize=20 | `curl -H 'Accept-Encoding: br' \| wc -c` |
| Cover image `medium` variant size | ≤ 80 KB WebP | object size in GCS |
| Cover image cache-control | `public, max-age=31536000, immutable` (or 1h-pinned signed URL) | response headers |
| CDN hit rate on public read endpoints | ≥ 70% after warm-up | Cloud CDN metrics |

### Out of scope (frontend owns)

JS bundle / code splitting, font loading strategy, critical CSS inlining,
image lazy-loading + `fetchpriority="high"` on hero, service worker, React
hydration cost. Frontend will not hit ≥ 90 Lighthouse without doing those,
regardless of what the backend ships.

### Verification checklist (post-deploy)

- [ ] `curl -sI -H 'Accept-Encoding: br' https://<host>/api/public/reports`
      shows `content-encoding: br` and `cache-control: public, max-age=60`.
- [ ] Second call to same URL within 60 s shows TTFB ≪ first call (output
      cache hit).
- [ ] `curl -sI -X OPTIONS -H 'Origin: https://taqreerk.com' -H 'Access-Control-Request-Method: GET' https://<host>/api/public/reports`
      shows `access-control-max-age: 7200`.
- [ ] Cloud Run revision in `gcloud run services describe taqreerk-backend-prod`
      shows `startupCpuBoost: true`.
- [ ] Cold-deploy `/healthz` first-200 time ≤ previous baseline (R2R win).

---

## Open Issues / Watchlist (2026-05-04)

- [ ] **Free-tier subscription auto-link gap** — `/api/usage/me` and other gated paths throw `InvalidOperationException("User has no active subscription")` (mapped to 409 by middleware) for at least one user. Registration is supposed to insert a row in `Subscriptions` with `Status = Active`; either the path is broken for some flow (org members? invited users?) or the backfill never ran in staging. Check [UsageService.cs:222-240](Tqreerk-backend/Application/Services/UsageService.cs#L222-L240) and registration logic.
- [ ] **`InvalidOperationException` → 409 default mapping** in [ExceptionHandlingMiddleware.cs:50](Tqreerk-backend/API/Middleware/ExceptionHandlingMiddleware.cs#L50) is overbroad. Missing-config / missing-data cases are getting reported as "Conflict", which is misleading. Narrow the mapping or migrate call sites to a typed exception.
- [ ] **Production DB setup not yet applied** — pgvector + GIN + HNSW + EF history bootstrap is in [production-db-setup.md](production-db-setup.md). Must run before first prod deploy.

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
| `CLOUD_STORAGE_BUCKET` | ai-service / doc-processor | GCS bucket name (`taqreerk-uploads`, me-central1) ✅ |
| `GEMINI_API_KEY` | ai-service (optional) | If set → AI Studio. If empty → Vertex AI via ADC ✅ |
| `GCP_SERVICE_ACCOUNT` | All | Cloud Run runtime service account |
| `SENTRY_DSN` / `Sentry__Dsn` | All | Error monitoring ✅ |

**Still needed (set when features are built):**

| Variable | Service | Where Used |
|---|---|---|
| `REFRESH_TOKEN_SECRET` | .NET | Refresh token signing |
| `MISER_SECRET_KEY` | .NET | Miser payment API key |
| `MISER_WEBHOOK_SECRET` | .NET | Miser webhook HMAC verification |
| `UNIFONIC_API_KEY` | .NET | OTP/SMS login |
| `UNIFONIC_SENDER_ID` | .NET | SMS sender ID |
| `AI_SERVICE_URL_STAGING` | .NET | ai-service Cloud Run URL (staging) |
| `AI_SERVICE_URL_PRODUCTION` | .NET | ai-service Cloud Run URL (production) |
| `DOC_PROCESSOR_URL_STAGING` | .NET / ai-service | doc-processor Cloud Run URL (staging) |
| `DOC_PROCESSOR_URL_PRODUCTION` | .NET / ai-service | doc-processor Cloud Run URL (production) |

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
- Admin mutations must call `IAdminActionLogger` and run under `[RequirePlatformStaff]`
- Gated user actions must wrap through `IUsageService.EnsureWithinLimitAndConsumeAsync`
- ai-service: prompts live in `core/prompts.py`, model names in `core/config.py` — no hardcoded prompts or models inside services
- ai-service: all new Gemini calls run with `temperature=0.2` for deterministic factual output
- Chunker logic must stay in sync with the .NET-side chunker — `scripts/check_chunker_sync.py` runs in CI
