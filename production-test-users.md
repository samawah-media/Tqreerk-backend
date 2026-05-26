# Production Test Users

Bootstrap test accounts for the production environment. All three test users share the same password (the same BCrypt hash as the seeded SuperAdmin from migration `20260428073639_Admin_ReviewWorkflow`).

## Credentials

| Role | Email | Password | UUID |
| --- | --- | --- | --- |
| Normal user | `user@taqreerk.test` | `Taqreerk!Admin#2026` | `50000000-0000-0000-0000-000000000001` |
| Platform admin | `admin@taqreerk.test` | `Taqreerk!Admin#2026` | `50000000-0000-0000-0000-000000000002` |
| Organization admin (جهة) | `org@taqreerk.test` | `Taqreerk!Admin#2026` | `50000000-0000-0000-0000-000000000003` |
| SuperAdmin (migration-seeded) | `admin@taqreerk.local` | `Taqreerk!Admin#2026` | `40000000-0000-0000-0000-000000000001` |

The `admin@taqreerk.local` SuperAdmin is created automatically by EF migration `20260428073639_Admin_ReviewWorkflow` — it lands on every fresh DB after `dotnet ef database update` and does NOT need the SQL below.

The other three (`*.test` emails) plus the supporting organization are created by the SQL block in this file.

## Organization (associated with `org@taqreerk.test`)

| Field | Value |
| --- | --- |
| Id | `60000000-0000-0000-0000-000000000001` |
| NameAr | جهة الاختبار |
| NameEn | Test Entity |
| Slug | `test-entity` |
| Type | `Governmental` |
| Status | `Active` |

## How to run

GCP Console → **SQL** → production instance → **Cloud SQL Studio** → sign in as **`postgres`** → database **`taqreerk_production`** → paste the block below.

Idempotent — `ON CONFLICT DO NOTHING` on every insert, so re-running is safe.

```sql
BEGIN;

-- ── 1. Organization (جهة) ────────────────────────────────────────────────
INSERT INTO organizations (
    "Id", "NameAr", "NameEn", "Slug", "Type", "Status",
    "IsPartner", "IsVerified", "TranslationEnabled", "CreatedAt"
) VALUES (
    '60000000-0000-0000-0000-000000000001',
    'جهة الاختبار',
    'Test Entity',
    'test-entity',
    'Governmental',                  -- Governmental | Private | Nonprofit | Educational
    'Active',                        -- skip review while testing
    FALSE, TRUE, FALSE,
    NOW()
)
ON CONFLICT ("Id") DO NOTHING;

-- ── 2. Three users ───────────────────────────────────────────────────────
INSERT INTO users (
    "Id", "Email", "PasswordHash", "FullName", "UserType",
    "PreferredLanguage", "Status", "EmailVerified", "PhoneVerified",
    "IsPlatformStaff", "FailedLoginAttempts", "CreatedAt"
) VALUES
-- (a) Normal user — regular individual, no roles
(
    '50000000-0000-0000-0000-000000000001',
    'user@taqreerk.test',
    '$2a$11$V50AMBZQ4MYf2vRNJoDzOONskEtLek9Wwm5h.2Hr2Oxg7YADtciN2',
    'مستخدم عادي',
    'individual', 'ar', 'Active', TRUE, FALSE, FALSE, 0, NOW()
),
-- (b) Platform admin — IsPlatformStaff, granted Admin platform role
(
    '50000000-0000-0000-0000-000000000002',
    'admin@taqreerk.test',
    '$2a$11$V50AMBZQ4MYf2vRNJoDzOONskEtLek9Wwm5h.2Hr2Oxg7YADtciN2',
    'مدير المنصة',
    'staff', 'ar', 'Active', TRUE, FALSE, TRUE, 0, NOW()
),
-- (c) Organization admin — joined to the org above as OrgAdminLegacy
(
    '50000000-0000-0000-0000-000000000003',
    'org@taqreerk.test',
    '$2a$11$V50AMBZQ4MYf2vRNJoDzOONskEtLek9Wwm5h.2Hr2Oxg7YADtciN2',
    'مسؤول الجهة',
    'organization_admin', 'ar', 'Active', TRUE, FALSE, FALSE, 0, NOW()
)
ON CONFLICT ("Id") DO NOTHING;

-- ── 3. Platform role grant (admin user → Admin role) ─────────────────────
-- Admin role UUID 30000000-...-02 is seeded by the RBAC migration.
-- Swap to 30000000-...-01 for SuperAdmin if you want full RBAC.
INSERT INTO user_roles ("UserId", "RoleId", "CreatedAt")
VALUES (
    '50000000-0000-0000-0000-000000000002',
    '30000000-0000-0000-0000-000000000002',
    NOW()
)
ON CONFLICT DO NOTHING;

-- ── 4. Organization membership (org user → OrgAdminLegacy in test entity) ─
-- OrgAdminLegacy role UUID 00000000-...-01.
INSERT INTO organization_members (
    "Id", "OrganizationId", "UserId", "RoleId", "IsActive", "JoinedAt", "CreatedAt"
) VALUES (
    gen_random_uuid(),
    '60000000-0000-0000-0000-000000000001',
    '50000000-0000-0000-0000-000000000003',
    '00000000-0000-0000-0000-000000000001',
    TRUE, NOW(), NOW()
)
ON CONFLICT DO NOTHING;

-- Mark the org user as the organization's creator (so they "own" it).
UPDATE organizations
SET    "CreatedByUserId" = '50000000-0000-0000-0000-000000000003'
WHERE  "Id" = '60000000-0000-0000-0000-000000000001'
   AND "CreatedByUserId" IS NULL;

-- ── 5. Free-plan subscriptions ───────────────────────────────────────────
-- Manual SQL inserts bypass the registration flow, so the
-- Feature5_UsageTracking backfill migration never ran for these users.
-- Without an Active subscription row, /api/me/plan-features throws
-- InvalidOperationException → HTTP 409. Same values the migration uses:
-- EndDate 100 years out (free plan has no real billing cycle).
-- NOT EXISTS guard makes this idempotent.
INSERT INTO subscriptions
    ("Id", "UserId", "OrganizationId", "PlanId",
     "Status", "PaymentStatus", "StartDate", "EndDate",
     "CreatedAt")
SELECT
    gen_random_uuid(),
    u."Id",
    NULL,
    '70000000-0000-0000-0000-000000000001',  -- IndividualFree
    'Active',
    'Paid',
    NOW(),
    NOW() + interval '100 years',
    NOW()
FROM users u
WHERE u."Id" IN (
    '50000000-0000-0000-0000-000000000001',  -- user@taqreerk.test
    '50000000-0000-0000-0000-000000000002',  -- admin@taqreerk.test
    '50000000-0000-0000-0000-000000000003'   -- org@taqreerk.test
)
AND NOT EXISTS (
    SELECT 1 FROM subscriptions s
    WHERE s."UserId" = u."Id"
      AND s."Status" = 'Active'
);

COMMIT;
```

## Verify (run after the block above)

```sql
SELECT u."Email", u."UserType", u."IsPlatformStaff", u."Status",
       array_agg(DISTINCT r."Name")  FILTER (WHERE r."Name"  IS NOT NULL) AS platform_roles,
       array_agg(DISTINCT o."NameEn") FILTER (WHERE o."NameEn" IS NOT NULL) AS organizations,
       array_agg(DISTINCT p."NameEn") FILTER (WHERE p."NameEn" IS NOT NULL) AS active_plans
FROM users u
LEFT JOIN user_roles           ur ON ur."UserId"        = u."Id"
LEFT JOIN roles                r  ON r."Id"             = ur."RoleId"
LEFT JOIN organization_members om ON om."UserId"        = u."Id" AND om."IsActive" = TRUE
LEFT JOIN organizations        o  ON o."Id"             = om."OrganizationId"
LEFT JOIN subscriptions        s  ON s."UserId"         = u."Id" AND s."Status" = 'Active'
LEFT JOIN plans                p  ON p."Id"             = s."PlanId"
WHERE u."Email" IN (
    'admin@taqreerk.local',
    'user@taqreerk.test',
    'admin@taqreerk.test',
    'org@taqreerk.test'
)
GROUP BY u."Id", u."Email", u."UserType", u."IsPlatformStaff", u."Status"
ORDER BY u."Email";
```

Expected rows:

- `admin@taqreerk.local` — `staff`, IsPlatformStaff=`t`, platform_roles=`{SuperAdmin}`, active_plans=`{Free}`
- `admin@taqreerk.test`  — `staff`, IsPlatformStaff=`t`, platform_roles=`{Admin}`, active_plans=`{Free}`
- `org@taqreerk.test`    — `organization_admin`, IsPlatformStaff=`f`, organizations=`{Test Entity}`, active_plans=`{Free}`
- `user@taqreerk.test`   — `individual`, IsPlatformStaff=`f`, no roles, no orgs, active_plans=`{Free}`

## Notes

- The shared BCrypt hash `$2a$11$V50AMBZQ4MYf2vRNJoDzOONskEtLek9Wwm5h.2Hr2Oxg7YADtciN2` corresponds to literal string `Taqreerk!Admin#2026`. It comes from the same seed as the migration-bootstrapped SuperAdmin, so we're not introducing a new secret with this file.
- All four accounts have `EmailVerified=TRUE` so they can log in immediately without the verification flow.
- After first login on real accounts (not these test ones), rotate the password via `POST /api/admin/auth/me/password` (admin app) or the regular user password-change endpoint.
- Test users live in production and will count against any per-org / per-user quotas configured via `Quota__*` env vars on the backend Cloud Run service. Remove them via the standard delete-user flow once real users are onboarded.
