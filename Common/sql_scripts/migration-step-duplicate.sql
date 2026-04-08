
-- 1. Diagnose — Run this SQL against the CMS database:
SELECT String01, COUNT(*) AS cnt
FROM tblBigTable
WHERE StoreName = 'EPiServer.Commerce.Internal.Migration.MigrationStepInfo'
GROUP BY String01
HAVING COUNT(*) > 1;

-- 2. Identify duplicates 

-- 3. Remove duplicates — Delete the extra rows, keeping newest one per step type:
;WITH cte AS ( 
  SELECT pkId, 
         ROW_NUMBER() OVER ( 
           PARTITION BY StoreName, String01  -- step store + step type 
           ORDER BY pkId DESC                -- keep the newest 
         ) AS rn 
  FROM dbo.tblBigTable 
  WHERE StoreName = 'EPiServer.Commerce.Internal.Migration.MigrationStepInfo' 
) 
DELETE FROM cte WHERE rn > 1;

-- 4. Verify — Reload the /EPiServer/Commerce/Migrate/Index page.
