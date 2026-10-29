-- Copilot seat roles and permissions. Runs after 200-seed-reference.sql, which deletes and reinserts
-- sec.RolePermission: the rows below are added back on every deployment. Safe to rerun.
--   LOB_SEAT_ADMIN  tracks one LOB's reservation against actual seats and names other seat admins for
--                   that LOB. No approvals (CanDecide = 0) and no RACI column: it is a reading and
--                   housekeeping role, so the workflow engine never routes a decision or a task to it.
--   seats.view          reservation versus actual pages for the LOBs where the person holds the permission
--   seats.admin.assign  add or end LOB_SEAT_ADMIN assignments for the same LOB (business owner, Platform Owner, seat admins)
SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

MERGE sec.RoleDefinition AS t
USING (VALUES
 ('LOB_SEAT_ADMIN', N'LOB seat admin', N'Tracks the LOB''s Copilot seat reservation against seats actually assigned; names or ends other LOB seat admins for the same LOB. No approval rights.', NULL, 'FUNDING_OWNER', 0, 1, 0, 17)
) AS s (RoleCode, Name, Description, FunctionCode, ScopeType, IsCentral, IsLob, CanDecide, SortOrder)
ON t.RoleCode = s.RoleCode
WHEN NOT MATCHED THEN INSERT (RoleCode, Name, Description, FunctionCode, ScopeType, IsCentral, IsLob, CanDecide, SortOrder)
     VALUES (s.RoleCode, s.Name, s.Description, s.FunctionCode, s.ScopeType, s.IsCentral, s.IsLob, s.CanDecide, s.SortOrder)
WHEN MATCHED THEN UPDATE SET Name = s.Name, Description = s.Description, FunctionCode = s.FunctionCode, ScopeType = s.ScopeType,
     IsCentral = s.IsCentral, IsLob = s.IsLob, CanDecide = s.CanDecide, SortOrder = s.SortOrder;

MERGE sec.RolePermission AS t
USING (VALUES
 ('LOB_SEAT_ADMIN','seats.view'), ('LOB_SEAT_ADMIN','seats.admin.assign'), ('LOB_SEAT_ADMIN','fundingowner.view'),
 ('LOB_BUSINESS_OWNER','seats.view'), ('LOB_BUSINESS_OWNER','seats.admin.assign'),
 ('SHARED_SERVICE_OWNER','seats.view'), ('SHARED_SERVICE_OWNER','seats.admin.assign'),
 ('DELEGATED_APPROVER','seats.view'),
 ('LOB_FINANCE_PARTNER','seats.view'),
 ('PLATFORM_OWNER','seats.view'), ('PLATFORM_OWNER','seats.admin.assign'),
 ('FINOPS_LEAD','seats.view'),
 ('FINOPS_ANALYST','seats.view'),
 ('AUDITOR','seats.view')
) AS s (RoleCode, Permission)
ON t.RoleCode = s.RoleCode AND t.Permission = s.Permission
WHEN NOT MATCHED THEN INSERT (RoleCode, Permission) VALUES (s.RoleCode, s.Permission);

COMMIT TRANSACTION;
GO
