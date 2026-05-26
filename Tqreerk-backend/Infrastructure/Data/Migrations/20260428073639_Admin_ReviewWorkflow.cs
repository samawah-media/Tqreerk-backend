using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taqreerk.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// First admin-module migration. Adds the moderation workflow plumbing
    /// the rest of the admin features build on:
    ///
    /// 1. New columns on `reports`: SubmittedForReviewAt, PublishedAt,
    ///    ClaimedByReviewerId (FK), ClaimedAt — needed by the review queue
    ///    + the auto-release background job.
    /// 2. New column on `users`: IsPlatformStaff — fast gate for /api/admin/*.
    /// 3. New role row: ContentReviewer (platform-scoped) + reports-page
    ///    permissions (view, edit) for it.
    /// 4. Backfill: reports currently in 'Draft' get flipped to
    ///    'PendingReview' so the new moderation workflow picks them up.
    ///    SubmittedForReviewAt is set to CreatedAt as a sensible default.
    /// 5. Bootstrap SuperAdmin user (admin@taqreerk.local) +
    ///    user_role grant to the SuperAdmin role. Idempotent so re-running
    ///    the migration on a partially-seeded DB is safe.
    /// </summary>
    public partial class Admin_ReviewWorkflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── 1. reports: workflow columns ───────────────────────────────
            migrationBuilder.AddColumn<DateTime>(
                name: "ClaimedAt",
                table: "reports",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ClaimedByReviewerId",
                table: "reports",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PublishedAt",
                table: "reports",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SubmittedForReviewAt",
                table: "reports",
                type: "timestamp with time zone",
                nullable: true);

            // Partial index for the auto-release sweep + reviewer-mine queries.
            migrationBuilder.CreateIndex(
                name: "IX_reports_ClaimedByReviewerId",
                table: "reports",
                column: "ClaimedByReviewerId",
                filter: "\"ClaimedByReviewerId\" IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_reports_users_ClaimedByReviewerId",
                table: "reports",
                column: "ClaimedByReviewerId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            // ── 2. users: platform-staff flag ──────────────────────────────
            migrationBuilder.AddColumn<bool>(
                name: "IsPlatformStaff",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_users_IsPlatformStaff",
                table: "users",
                column: "IsPlatformStaff",
                filter: "\"IsPlatformStaff\" = TRUE");

            // ── 3. New role: ContentReviewer + permissions ─────────────────
            migrationBuilder.InsertData(
                table: "roles",
                columns: new[] { "Id", "CreatedAt", "Description", "IsSystem", "Name", "Scope" },
                values: new object[]
                {
                    new Guid("30000000-0000-0000-0000-000000000003"),
                    new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc),
                    "Reviews submitted reports",
                    true,
                    "ContentReviewer",
                    0
                });

            migrationBuilder.InsertData(
                table: "role_permissions",
                columns: new[] { "RoleId", "PermissionId", "CreatedAt" },
                values: new object[,]
                {
                    {
                        new Guid("30000000-0000-0000-0000-000000000003"),
                        new Guid("20000000-0000-0000-0000-000000000201"),
                        new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc)
                    },
                    {
                        new Guid("30000000-0000-0000-0000-000000000003"),
                        new Guid("20000000-0000-0000-0000-000000000203"),
                        new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc)
                    }
                });

            // ── 4. Backfill existing Draft reports → PendingReview ─────────
            // Plain SQL because EF can't express "set Status only when it
            // matches X". The generated SubmittedForReviewAt mirrors CreatedAt
            // so the queue's FIFO ordering matches the upload order.
            migrationBuilder.Sql("""
                UPDATE reports
                SET    "Status" = 'PendingReview',
                       "SubmittedForReviewAt" = "CreatedAt"
                WHERE  "Status" = 'Draft'
                   AND "DeletedAt" IS NULL;
            """);

            // Reports already at 'Published' in the old enum keep that label
            // — set PublishedAt = CreatedAt as a best-effort backfill so the
            // public timeline still has a value to sort by.
            migrationBuilder.Sql("""
                UPDATE reports
                SET    "PublishedAt" = "CreatedAt"
                WHERE  "Status" = 'Published'
                   AND "PublishedAt" IS NULL;
            """);

            // ── 5. Bootstrap SuperAdmin user + role grant ──────────────────
            // Password = `Taqreerk!Admin#2026` (BCrypt cost 11). Email/phone
            // verified so the user can log in immediately. Idempotent inserts
            // — safe to re-run if the migration partially completed before.
            migrationBuilder.Sql("""
                INSERT INTO users (
                    "Id", "Email", "PasswordHash", "FullName", "UserType",
                    "PreferredLanguage", "Status", "EmailVerified",
                    "PhoneVerified", "IsPlatformStaff", "FailedLoginAttempts",
                    "CreatedAt"
                )
                VALUES (
                    '40000000-0000-0000-0000-000000000001',
                    'admin@taqreerk.local',
                    '$2a$11$V50AMBZQ4MYf2vRNJoDzOONskEtLek9Wwm5h.2Hr2Oxg7YADtciN2',
                    'Taqreerk SuperAdmin',
                    'staff',
                    'ar',
                    'Active',
                    TRUE,
                    FALSE,
                    TRUE,
                    0,
                    NOW()
                )
                ON CONFLICT ("Id") DO NOTHING;

                INSERT INTO user_roles ("UserId", "RoleId", "CreatedAt")
                VALUES (
                    '40000000-0000-0000-0000-000000000001',
                    '30000000-0000-0000-0000-000000000001',
                    NOW()
                )
                ON CONFLICT DO NOTHING;
            """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Roll back the seeded SuperAdmin first (FK to roles).
            migrationBuilder.Sql("""
                DELETE FROM user_roles
                WHERE  "UserId" = '40000000-0000-0000-0000-000000000001';

                DELETE FROM users
                WHERE  "Id" = '40000000-0000-0000-0000-000000000001';
            """);

            migrationBuilder.DeleteData(
                table: "role_permissions",
                keyColumns: new[] { "RoleId", "PermissionId" },
                keyValues: new object[]
                {
                    new Guid("30000000-0000-0000-0000-000000000003"),
                    new Guid("20000000-0000-0000-0000-000000000201")
                });

            migrationBuilder.DeleteData(
                table: "role_permissions",
                keyColumns: new[] { "RoleId", "PermissionId" },
                keyValues: new object[]
                {
                    new Guid("30000000-0000-0000-0000-000000000003"),
                    new Guid("20000000-0000-0000-0000-000000000203")
                });

            migrationBuilder.DeleteData(
                table: "roles",
                keyColumn: "Id",
                keyValue: new Guid("30000000-0000-0000-0000-000000000003"));

            // Walk the status backfill back. Any reports that were Draft
            // before Up() became PendingReview during the migration; we
            // cannot tell the original Drafts apart from genuine submissions
            // without a snapshot, so the sensible inverse is to restore all
            // PendingReview rows whose SubmittedForReviewAt == CreatedAt
            // (the marker the migration set).
            migrationBuilder.Sql("""
                UPDATE reports
                SET    "Status" = 'Draft',
                       "SubmittedForReviewAt" = NULL
                WHERE  "Status" = 'PendingReview'
                   AND "SubmittedForReviewAt" = "CreatedAt";
            """);

            migrationBuilder.DropIndex(
                name: "IX_users_IsPlatformStaff",
                table: "users");

            migrationBuilder.DropColumn(
                name: "IsPlatformStaff",
                table: "users");

            migrationBuilder.DropForeignKey(
                name: "FK_reports_users_ClaimedByReviewerId",
                table: "reports");

            migrationBuilder.DropIndex(
                name: "IX_reports_ClaimedByReviewerId",
                table: "reports");

            migrationBuilder.DropColumn(
                name: "SubmittedForReviewAt",
                table: "reports");

            migrationBuilder.DropColumn(
                name: "PublishedAt",
                table: "reports");

            migrationBuilder.DropColumn(
                name: "ClaimedByReviewerId",
                table: "reports");

            migrationBuilder.DropColumn(
                name: "ClaimedAt",
                table: "reports");
        }
    }
}
