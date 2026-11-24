/*
    dash.usp_RefreshDashboard
    ------------------------------------------------------------------------
    Rebuilds what the Copilot Insights dashboard reads besides the bi schema:

      1. dash.Person and dash.OrgClosure, whole, from audit.UserDirectory
         (on-premises AD) - so the security model follows today's
         organization. Small: one row per person, and one per (manager,
         person under them) pair.
      2. dash.UserDay from the bi facts, and dash.ResourceDay (labels and
         sites) from bi.FactAccessedResource, for the days from
         @SinceUtc's day on (every day when NULL), replaced in one transaction.
         Every day, too, when a full bi refresh has run since the last
         rollup, and from the first day of an app whose category a
         bi refresh changed.
      3. dash.AgentUse, whole: when each agent was first and last used.
      4. The totals the pages read: dash.UserItemDay (per person per app and
         agent) from @SinceUtc's day, as dash.UserDay; dash.UserWeek and
         dash.UserItemWeek from the week it falls in, dash.AdoptionDay from
         its day; dash.UserActivity, dash.UserPeriod, dash.UserItemPeriod and
         dash.SitePerson whole; each whole the first time.
      5. The columnstores over what it wrote, compressed, when the caller may
         alter the tables.

    Invoke-CopilotAuditBackfill.ps1 -Incremental and the CSV importer call it
    last, after bi.usp_RefreshBi, with the same @SinceUtc.

    While a full bi rebuild has not finished (bi.RebuildInProgress has a row:
    it is running, or was stopped part way), steps 2 and 3 are skipped and
    the rollups left as they are: the facts are half written. The
    first rollup after the rebuild has finished builds every day.

    The closure follows ManagerUPN up from each person. A manager missing from
    the directory (another employee type, say) still becomes an ancestor, so
    they can see their people when they sign in. A loop in the manager
    attributes stops at 40 levels and each pair keeps its shortest distance.

    The dashboard's app-pool account may run it (Admin > Health > Refresh)
    without ALTER: it truncates a table only when the caller may, and deletes
    its rows otherwise, which ownership chaining allows. That account needs
    EXECUTE on it, SELECT on bi (its first check looks for bi.FactInteraction)
    and SELECT on audit.UserDirectory, which it reads through dynamic SQL
    because the table is optional. Install-CopilotInsights.ps1 grants all three.

    Requires Create-CopilotDashboardSchema.sql and the bi schema.
*/
SET NOCOUNT ON;
GO

/*
    dash.usp_RefreshResourceTotals
    ------------------------------------------------------------------------
    The totals of resources read that Sensitive content, Access to labeled content, Oversharing hotspots, Labeled
    content by unit and Usage by license read (see the schema's notes on dash.ReadMix): called by
    dash.usp_RefreshDashboard after its own totals, with the first day that run rewrote (@FromKey, NULL for every day),
    the last day with data and the day before it (@MaxKey, @PrevKey: the days the period totals end on) and the first
    day with data (@MinKey). The period totals are rebuilt whole, from one pass each over the resources read,
    dash.ResourceDay and the conversations; the readers of each site and the copy of the conversations from @FromKey on.
    The setting DashResourceTotalsEnds records the two days once all of it is built, and is cleared while it is.
*/
CREATE OR ALTER PROCEDURE dash.usp_RefreshResourceTotals
    @FromKey INT = NULL,
    @MaxKey  INT = NULL,
    @PrevKey INT = NULL,
    @MinKey  INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    DECLARE @started DATETIME2(3) = SYSUTCDATETIME(), @msg NVARCHAR(400), @step DATETIME2(3) = SYSUTCDATETIME(), @times NVARCHAR(200) = N'';
    SET @MaxKey = ISNULL(@MaxKey, (SELECT MAX(DateKey) FROM dash.UserDay));
    SET @MinKey = ISNULL(@MinKey, (SELECT MIN(DateKey) FROM dash.UserDay));
    SET @PrevKey = ISNULL(@PrevKey, (SELECT MAX(DateKey) FROM dash.UserDay WHERE DateKey < @MaxKey));
    IF @MaxKey IS NULL RETURN;   -- no data yet

    /* Cleared first: a run stopped part way leaves the pages counting the old way. */
    UPDATE dash.Setting SET Value = NULL, UpdatedBy = N'dash.usp_RefreshResourceTotals', UpdatedUtc = SYSUTCDATETIME() WHERE Name = 'DashResourceTotalsEnds';
    DECLARE @mayTruncate BIT = CASE WHEN HAS_PERMS_BY_NAME(N'dash.ReadMixPeriod', N'OBJECT', N'ALTER') = 1 THEN 1 ELSE 0 END;

    /* The first day of each period a page offers, ending on either day (All data: from the first day with data). */
    DECLARE @maxDate DATE = CONVERT(DATE, CONVERT(CHAR(8), @MaxKey)), @prevDate DATE = CONVERT(DATE, CONVERT(CHAR(8), ISNULL(@PrevKey, @MaxKey)));
    DECLARE @m7 INT = CONVERT(INT, CONVERT(CHAR(8), DATEADD(DAY, -6, @maxDate), 112)), @m14 INT = CONVERT(INT, CONVERT(CHAR(8), DATEADD(DAY, -13, @maxDate), 112)),
            @m30 INT = CONVERT(INT, CONVERT(CHAR(8), DATEADD(DAY, -29, @maxDate), 112)), @m90 INT = CONVERT(INT, CONVERT(CHAR(8), DATEADD(DAY, -89, @maxDate), 112)),
            @m180 INT = CONVERT(INT, CONVERT(CHAR(8), DATEADD(DAY, -179, @maxDate), 112)), @m365 INT = CONVERT(INT, CONVERT(CHAR(8), DATEADD(DAY, -364, @maxDate), 112)),
            @m730 INT = CONVERT(INT, CONVERT(CHAR(8), DATEADD(DAY, -729, @maxDate), 112));
    DECLARE @p7 INT = CONVERT(INT, CONVERT(CHAR(8), DATEADD(DAY, -6, @prevDate), 112)), @p14 INT = CONVERT(INT, CONVERT(CHAR(8), DATEADD(DAY, -13, @prevDate), 112)),
            @p30 INT = CONVERT(INT, CONVERT(CHAR(8), DATEADD(DAY, -29, @prevDate), 112)), @p90 INT = CONVERT(INT, CONVERT(CHAR(8), DATEADD(DAY, -89, @prevDate), 112)),
            @p180 INT = CONVERT(INT, CONVERT(CHAR(8), DATEADD(DAY, -179, @prevDate), 112)), @p365 INT = CONVERT(INT, CONVERT(CHAR(8), DATEADD(DAY, -364, @prevDate), 112)),
            @p730 INT = CONVERT(INT, CONVERT(CHAR(8), DATEADD(DAY, -729, @prevDate), 112));
    /* Without a day before the last (one day of data), only the last day's totals are written. */
    DECLARE @prev INT = ISNULL(@PrevKey, -1);

    /* ---- Resources read, per person and combination ------------------------ */

    /* Every combination the dimensions allow, numbered once: a few hundred rows, and a reader of them needs no scan to
       find the ones in use. */
    INSERT INTO dash.ReadMix (MixKey, ResourceTypeKey, SensitivityLabelKey, FileTypeKey, IsBlocked)
    SELECT ISNULL((SELECT MAX(MixKey) FROM dash.ReadMix), 0) + ROW_NUMBER() OVER (ORDER BY t.ResourceTypeKey, l.SensitivityLabelKey, ft.FileTypeKey, b.IsBlocked),
           t.ResourceTypeKey, l.SensitivityLabelKey, ft.FileTypeKey, b.IsBlocked
    FROM bi.DimResourceType AS t
    CROSS JOIN bi.DimSensitivityLabel AS l
    CROSS JOIN bi.DimFileType AS ft
    CROSS JOIN (VALUES (CAST(0 AS BIT)), (CAST(1 AS BIT))) AS b (IsBlocked)
    WHERE NOT EXISTS (SELECT 1 FROM dash.ReadMix AS m WHERE m.ResourceTypeKey = t.ResourceTypeKey AND m.SensitivityLabelKey = l.SensitivityLabelKey
                                                         AND m.FileTypeKey = ft.FileTypeKey AND m.IsBlocked = b.IsBlocked);

    /* One pass over every resource read, both days' periods side by side. */
    DROP TABLE IF EXISTS #mix;
    SELECT f.UserKey, m.MixKey,
           SUM(CASE WHEN f.DateKey >= @m7 THEN 1 ELSE 0 END) AS M7, SUM(CASE WHEN f.DateKey >= @m14 THEN 1 ELSE 0 END) AS M14,
           SUM(CASE WHEN f.DateKey >= @m30 THEN 1 ELSE 0 END) AS M30, SUM(CASE WHEN f.DateKey >= @m90 THEN 1 ELSE 0 END) AS M90,
           SUM(CASE WHEN f.DateKey >= @m180 THEN 1 ELSE 0 END) AS M180, SUM(CASE WHEN f.DateKey >= @m365 THEN 1 ELSE 0 END) AS M365,
           SUM(CASE WHEN f.DateKey >= @m730 THEN 1 ELSE 0 END) AS M730, COUNT(*) AS MAll,
           SUM(CASE WHEN f.DateKey BETWEEN @p7 AND @prev THEN 1 ELSE 0 END) AS P7, SUM(CASE WHEN f.DateKey BETWEEN @p14 AND @prev THEN 1 ELSE 0 END) AS P14,
           SUM(CASE WHEN f.DateKey BETWEEN @p30 AND @prev THEN 1 ELSE 0 END) AS P30, SUM(CASE WHEN f.DateKey BETWEEN @p90 AND @prev THEN 1 ELSE 0 END) AS P90,
           SUM(CASE WHEN f.DateKey BETWEEN @p180 AND @prev THEN 1 ELSE 0 END) AS P180, SUM(CASE WHEN f.DateKey BETWEEN @p365 AND @prev THEN 1 ELSE 0 END) AS P365,
           SUM(CASE WHEN f.DateKey BETWEEN @p730 AND @prev THEN 1 ELSE 0 END) AS P730, SUM(CASE WHEN f.DateKey <= @prev THEN 1 ELSE 0 END) AS PAll
    INTO #mix
    FROM bi.FactAccessedResource AS f
    JOIN dash.ReadMix AS m ON m.ResourceTypeKey = f.ResourceTypeKey AND m.SensitivityLabelKey = f.SensitivityLabelKey
                          AND m.FileTypeKey = f.FileTypeKey AND m.IsBlocked = f.IsBlocked
    WHERE f.DateKey BETWEEN @MinKey AND @MaxKey
    GROUP BY f.UserKey, m.MixKey
    OPTION (RECOMPILE);

    BEGIN TRANSACTION;
    IF @mayTruncate = 1 TRUNCATE TABLE dash.ReadMixPeriod; ELSE DELETE FROM dash.ReadMixPeriod;
    INSERT INTO dash.ReadMixPeriod WITH (TABLOCK) (EndKey, UserKey, MixKey, Reads7, Reads14, Reads30, Reads90, Reads180, Reads365, Reads730, ReadsAll)
    SELECT @MaxKey, UserKey, MixKey, M7, M14, M30, M90, M180, M365, M730, MAll FROM #mix
    UNION ALL
    SELECT @PrevKey, UserKey, MixKey, P7, P14, P30, P90, P180, P365, P730, PAll FROM #mix WHERE PAll > 0 AND @PrevKey IS NOT NULL;
    COMMIT TRANSACTION;
    DROP TABLE #mix;
    SELECT @times = CONCAT(N'resources read ', DATEDIFF(SECOND, @step, SYSUTCDATETIME()), N' s'), @step = SYSUTCDATETIME();

    /* ---- dash.ResourceDay, per person ------------------------------------- */

    /* Summed per person and label or site first (by the text, as dash.ResourceDay keeps them), then each label id and
       site is numbered where it is new, from those rows, and the sums are kept by number. Read and numbered in one
       statement, its threads deadlocked on each other; read twice, the days were read twice. */
    DROP TABLE IF EXISTS #rps;
    SELECT rd.Kind, rd.Item, rd.UserKey,
               SUM(CASE WHEN rd.DateKey >= @m7 THEN rd.Interactions ELSE 0 END) AS M7, SUM(CASE WHEN rd.DateKey >= @m14 THEN rd.Interactions ELSE 0 END) AS M14,
               SUM(CASE WHEN rd.DateKey >= @m30 THEN rd.Interactions ELSE 0 END) AS M30, SUM(CASE WHEN rd.DateKey >= @m90 THEN rd.Interactions ELSE 0 END) AS M90,
               SUM(CASE WHEN rd.DateKey >= @m180 THEN rd.Interactions ELSE 0 END) AS M180, SUM(CASE WHEN rd.DateKey >= @m365 THEN rd.Interactions ELSE 0 END) AS M365,
               SUM(CASE WHEN rd.DateKey >= @m730 THEN rd.Interactions ELSE 0 END) AS M730, SUM(rd.Interactions) AS MAll,
               SUM(CASE WHEN rd.DateKey BETWEEN @p7 AND @prev THEN rd.Interactions ELSE 0 END) AS P7, SUM(CASE WHEN rd.DateKey BETWEEN @p14 AND @prev THEN rd.Interactions ELSE 0 END) AS P14,
               SUM(CASE WHEN rd.DateKey BETWEEN @p30 AND @prev THEN rd.Interactions ELSE 0 END) AS P30, SUM(CASE WHEN rd.DateKey BETWEEN @p90 AND @prev THEN rd.Interactions ELSE 0 END) AS P90,
               SUM(CASE WHEN rd.DateKey BETWEEN @p180 AND @prev THEN rd.Interactions ELSE 0 END) AS P180, SUM(CASE WHEN rd.DateKey BETWEEN @p365 AND @prev THEN rd.Interactions ELSE 0 END) AS P365,
               SUM(CASE WHEN rd.DateKey BETWEEN @p730 AND @prev THEN rd.Interactions ELSE 0 END) AS P730, SUM(CASE WHEN rd.DateKey <= @prev THEN rd.Interactions ELSE 0 END) AS PAll
    INTO #rps
    FROM dash.ResourceDay AS rd
    WHERE rd.DateKey BETWEEN @MinKey AND @MaxKey
    GROUP BY rd.Kind, rd.Item, rd.UserKey
    OPTION (RECOMPILE);
    INSERT INTO dash.ResourceItem (Kind, Item)
    SELECT i.Kind, i.Item FROM (SELECT DISTINCT s.Kind, s.Item FROM #rps AS s) AS i
    WHERE NOT EXISTS (SELECT 1 FROM dash.ResourceItem AS ri WHERE ri.Kind = i.Kind AND ri.Item = i.Item)
    OPTION (MAXDOP 1);

    DROP TABLE IF EXISTS #rp;
    SELECT CAST(CASE s.Kind WHEN 'Label' THEN 'L' WHEN 'Site' THEN 'S' ELSE 'B' END AS CHAR(1)) AS Kind, ri.ItemKey, s.UserKey,
           s.M7, s.M14, s.M30, s.M90, s.M180, s.M365, s.M730, s.MAll, s.P7, s.P14, s.P30, s.P90, s.P180, s.P365, s.P730, s.PAll
    INTO #rp
    FROM #rps AS s JOIN dash.ResourceItem AS ri ON ri.Kind = s.Kind AND ri.Item = s.Item
    OPTION (RECOMPILE);
    DROP TABLE #rps;

    /* All labels together, with the days that had any (a person's day counts once however many labels it read), and all
       blocked sites together. */
    DROP TABLE IF EXISTS #rpd;
    WITH d AS (SELECT rd.UserKey, rd.DateKey, SUM(rd.Interactions) AS N FROM dash.ResourceDay AS rd
               WHERE rd.Kind = 'Label' AND rd.DateKey BETWEEN @MinKey AND @MaxKey GROUP BY rd.UserKey, rd.DateKey)
    SELECT d.UserKey,
           SUM(CASE WHEN d.DateKey >= @m7 THEN d.N ELSE 0 END) AS M7, SUM(CASE WHEN d.DateKey >= @m14 THEN d.N ELSE 0 END) AS M14,
           SUM(CASE WHEN d.DateKey >= @m30 THEN d.N ELSE 0 END) AS M30, SUM(CASE WHEN d.DateKey >= @m90 THEN d.N ELSE 0 END) AS M90,
           SUM(CASE WHEN d.DateKey >= @m180 THEN d.N ELSE 0 END) AS M180, SUM(CASE WHEN d.DateKey >= @m365 THEN d.N ELSE 0 END) AS M365,
           SUM(CASE WHEN d.DateKey >= @m730 THEN d.N ELSE 0 END) AS M730, SUM(d.N) AS MAll,
           SUM(CASE WHEN d.DateKey BETWEEN @p7 AND @prev THEN d.N ELSE 0 END) AS P7, SUM(CASE WHEN d.DateKey BETWEEN @p14 AND @prev THEN d.N ELSE 0 END) AS P14,
           SUM(CASE WHEN d.DateKey BETWEEN @p30 AND @prev THEN d.N ELSE 0 END) AS P30, SUM(CASE WHEN d.DateKey BETWEEN @p90 AND @prev THEN d.N ELSE 0 END) AS P90,
           SUM(CASE WHEN d.DateKey BETWEEN @p180 AND @prev THEN d.N ELSE 0 END) AS P180, SUM(CASE WHEN d.DateKey BETWEEN @p365 AND @prev THEN d.N ELSE 0 END) AS P365,
           SUM(CASE WHEN d.DateKey BETWEEN @p730 AND @prev THEN d.N ELSE 0 END) AS P730, SUM(CASE WHEN d.DateKey <= @prev THEN d.N ELSE 0 END) AS PAll,
           COUNT(CASE WHEN d.DateKey >= @m7 THEN 1 END) AS DM7, COUNT(CASE WHEN d.DateKey >= @m14 THEN 1 END) AS DM14,
           COUNT(CASE WHEN d.DateKey >= @m30 THEN 1 END) AS DM30, COUNT(CASE WHEN d.DateKey >= @m90 THEN 1 END) AS DM90,
           COUNT(CASE WHEN d.DateKey >= @m180 THEN 1 END) AS DM180, COUNT(CASE WHEN d.DateKey >= @m365 THEN 1 END) AS DM365,
           COUNT(CASE WHEN d.DateKey >= @m730 THEN 1 END) AS DM730, COUNT(*) AS DMAll,
           COUNT(CASE WHEN d.DateKey BETWEEN @p7 AND @prev THEN 1 END) AS DP7, COUNT(CASE WHEN d.DateKey BETWEEN @p14 AND @prev THEN 1 END) AS DP14,
           COUNT(CASE WHEN d.DateKey BETWEEN @p30 AND @prev THEN 1 END) AS DP30, COUNT(CASE WHEN d.DateKey BETWEEN @p90 AND @prev THEN 1 END) AS DP90,
           COUNT(CASE WHEN d.DateKey BETWEEN @p180 AND @prev THEN 1 END) AS DP180, COUNT(CASE WHEN d.DateKey BETWEEN @p365 AND @prev THEN 1 END) AS DP365,
           COUNT(CASE WHEN d.DateKey BETWEEN @p730 AND @prev THEN 1 END) AS DP730, COUNT(CASE WHEN d.DateKey <= @prev THEN 1 END) AS DPAll
    INTO #rpd
    FROM d
    GROUP BY d.UserKey
    OPTION (RECOMPILE);

    BEGIN TRANSACTION;
    IF @mayTruncate = 1 TRUNCATE TABLE dash.ResourcePeriod; ELSE DELETE FROM dash.ResourcePeriod;
    INSERT INTO dash.ResourcePeriod WITH (TABLOCK) (EndKey, Kind, ItemKey, UserKey, N7, N14, N30, N90, N180, N365, N730, NAll, D7, D14, D30, D90, D180, D365, D730, DAll)
    SELECT @MaxKey, Kind, ItemKey, UserKey, M7, M14, M30, M90, M180, M365, M730, MAll, 0, 0, 0, 0, 0, 0, 0, 0 FROM #rp
    UNION ALL
    SELECT @PrevKey, Kind, ItemKey, UserKey, P7, P14, P30, P90, P180, P365, P730, PAll, 0, 0, 0, 0, 0, 0, 0, 0 FROM #rp WHERE PAll > 0 AND @PrevKey IS NOT NULL
    UNION ALL
    SELECT @MaxKey, 'X', 0, UserKey, SUM(M7), SUM(M14), SUM(M30), SUM(M90), SUM(M180), SUM(M365), SUM(M730), SUM(MAll), 0, 0, 0, 0, 0, 0, 0, 0
    FROM #rp WHERE Kind = 'B' GROUP BY UserKey
    UNION ALL
    SELECT @PrevKey, 'X', 0, UserKey, SUM(P7), SUM(P14), SUM(P30), SUM(P90), SUM(P180), SUM(P365), SUM(P730), SUM(PAll), 0, 0, 0, 0, 0, 0, 0, 0
    FROM #rp WHERE Kind = 'B' AND @PrevKey IS NOT NULL GROUP BY UserKey HAVING SUM(PAll) > 0
    UNION ALL
    SELECT @MaxKey, 'A', 0, UserKey, M7, M14, M30, M90, M180, M365, M730, MAll, DM7, DM14, DM30, DM90, DM180, DM365, DM730, DMAll FROM #rpd
    UNION ALL
    SELECT @PrevKey, 'A', 0, UserKey, P7, P14, P30, P90, P180, P365, P730, PAll, DP7, DP14, DP30, DP90, DP180, DP365, DP730, DPAll FROM #rpd WHERE DPAll > 0 AND @PrevKey IS NOT NULL;
    COMMIT TRANSACTION;
    DROP TABLE #rp;
    DROP TABLE #rpd;
    SELECT @times = CONCAT(@times, N', labels and sites ', DATEDIFF(SECOND, @step, SYSUTCDATETIME()), N' s'), @step = SYSUTCDATETIME();

    /* ---- The readers of each site, from @FromKey on ------------------------- */

    /* Each person's last two days at each site, from the days this run rewrote. A pair's two days are worked out from its
       new days and what the table knew of its days before @FromKey; where that is not enough (its days from @FromKey on
       are gone, or both its days were in them), its days before @FromKey are read again, for those pairs alone.
       PrevKey 0: not worked out, for a pair whose last day is more than a week before the data's last day when the
       table was built whole; it is worked out when the pair is next read. A page reads PrevKey only for a pair whose last
       day is after the period's, which is the data's last day, so it never reads a 0. */
    DECLARE @siteWhole BIT = CASE WHEN @FromKey IS NULL OR NOT EXISTS (SELECT 1 FROM dash.SiteReader) THEN 1 ELSE 0 END;
    IF @siteWhole = 1
    BEGIN
        DROP TABLE IF EXISTS #last;
        SELECT f.SiteKey, f.UserKey, MAX(f.DateKey) AS LastKey
        INTO #last
        FROM bi.FactAccessedResource AS f
        WHERE f.SiteKey > 1 AND f.DateKey <= @MaxKey
        GROUP BY f.SiteKey, f.UserKey
        OPTION (RECOMPILE);
        /* The day before the last, for the pairs read in the last week: the only ones a page or the next nights can need it for. */
        DECLARE @weekKey INT = @m7;
        DROP TABLE IF EXISTS #prev;
        SELECT f.SiteKey, f.UserKey, MAX(f.DateKey) AS PrevKey
        INTO #prev
        FROM bi.FactAccessedResource AS f
        JOIN #last AS l ON l.SiteKey = f.SiteKey AND l.UserKey = f.UserKey AND l.LastKey >= @weekKey
        WHERE f.SiteKey > 1 AND f.DateKey < l.LastKey
        GROUP BY f.SiteKey, f.UserKey
        OPTION (RECOMPILE);
        BEGIN TRANSACTION;
        IF @mayTruncate = 1 TRUNCATE TABLE dash.SiteReader; ELSE DELETE FROM dash.SiteReader;
        INSERT INTO dash.SiteReader WITH (TABLOCK) (SiteKey, UserKey, LastKey, PrevKey)
        SELECT l.SiteKey, l.UserKey, l.LastKey, CASE WHEN l.LastKey >= @weekKey THEN p.PrevKey ELSE 0 END
        FROM #last AS l LEFT JOIN #prev AS p ON p.SiteKey = l.SiteKey AND p.UserKey = l.UserKey;
        COMMIT TRANSACTION;
        DROP TABLE #last;
        DROP TABLE #prev;
    END;
    ELSE
    BEGIN
        DROP TABLE IF EXISTS #nd;
        SELECT f.SiteKey, f.UserKey, f.DateKey
        INTO #nd
        FROM bi.FactAccessedResource AS f
        WHERE f.DateKey BETWEEN @FromKey AND @MaxKey AND f.SiteKey > 1
        GROUP BY f.SiteKey, f.UserKey, f.DateKey
        OPTION (RECOMPILE);
        DROP TABLE IF EXISTS #nw;
        SELECT n.SiteKey, n.UserKey, MAX(n.DateKey) AS N1, COUNT(*) AS Cnt, CAST(NULL AS INT) AS N2
        INTO #nw
        FROM #nd AS n GROUP BY n.SiteKey, n.UserKey;
        UPDATE w SET N2 = x.N2
        FROM #nw AS w
        JOIN (SELECT n.SiteKey, n.UserKey, MAX(n.DateKey) AS N2 FROM #nd AS n JOIN #nw AS w2 ON w2.SiteKey = n.SiteKey AND w2.UserKey = n.UserKey AND n.DateKey < w2.N1
              GROUP BY n.SiteKey, n.UserKey) AS x ON x.SiteKey = w.SiteKey AND x.UserKey = w.UserKey;

        /* The pairs this run touches: read from @FromKey on, before or now. */
        DROP TABLE IF EXISTS #aff;
        SELECT COALESCE(w.SiteKey, o.SiteKey) AS SiteKey, COALESCE(w.UserKey, o.UserKey) AS UserKey,
               o.LastKey AS OL1, o.PrevKey AS OL0, CASE WHEN o.SiteKey IS NULL THEN 0 ELSE 1 END AS Old,
               w.N1, w.N2, ISNULL(w.Cnt, 0) AS Cnt, CAST(NULL AS INT) AS O1, CAST(NULL AS INT) AS O2, CAST(0 AS BIT) AS Fallback
        INTO #aff
        FROM #nw AS w
        FULL JOIN (SELECT r.SiteKey, r.UserKey, r.LastKey, r.PrevKey FROM dash.SiteReader AS r
                   WHERE r.LastKey >= @FromKey
                      OR EXISTS (SELECT 1 FROM #nw AS x WHERE x.SiteKey = r.SiteKey AND x.UserKey = r.UserKey)) AS o
            ON o.SiteKey = w.SiteKey AND o.UserKey = w.UserKey;
        /* What is known of each pair's days before @FromKey: its last two, its last, or nothing. */
        UPDATE #aff SET
            O1 = CASE WHEN OL1 < @FromKey THEN OL1 WHEN OL0 > 0 AND OL0 < @FromKey THEN OL0 END,
            O2 = CASE WHEN OL1 < @FromKey THEN OL0 END,
            Fallback = CASE WHEN Old = 0 OR OL1 < @FromKey THEN 0
                            WHEN OL0 IS NULL THEN 0                               -- no day before its last at all
                            WHEN OL0 > 0 AND OL0 < @FromKey THEN CASE WHEN Cnt = 0 THEN 1 ELSE 0 END
                            ELSE CASE WHEN Cnt < 2 THEN 1 ELSE 0 END END;
        IF EXISTS (SELECT 1 FROM #aff WHERE Fallback = 1)
        BEGIN
            DROP TABLE IF EXISTS #fb;
            SELECT f.SiteKey, f.UserKey, f.DateKey
            INTO #fb
            FROM bi.FactAccessedResource AS f
            JOIN #aff AS a ON a.SiteKey = f.SiteKey AND a.UserKey = f.UserKey AND a.Fallback = 1
            WHERE f.DateKey < @FromKey AND f.SiteKey > 1
            GROUP BY f.SiteKey, f.UserKey, f.DateKey
            OPTION (RECOMPILE);
            UPDATE a SET O1 = x.O1, O2 = (SELECT MAX(b.DateKey) FROM #fb AS b WHERE b.SiteKey = a.SiteKey AND b.UserKey = a.UserKey AND b.DateKey < x.O1)
            FROM #aff AS a
            LEFT JOIN (SELECT SiteKey, UserKey, MAX(DateKey) AS O1 FROM #fb GROUP BY SiteKey, UserKey) AS x ON x.SiteKey = a.SiteKey AND x.UserKey = a.UserKey
            WHERE a.Fallback = 1;
            DROP TABLE #fb;
        END;
        BEGIN TRANSACTION;
        DELETE r FROM dash.SiteReader AS r JOIN #aff AS a ON a.SiteKey = r.SiteKey AND a.UserKey = r.UserKey;
        INSERT INTO dash.SiteReader (SiteKey, UserKey, LastKey, PrevKey)
        SELECT SiteKey, UserKey, COALESCE(N1, O1),
               CASE WHEN N2 IS NOT NULL THEN N2 WHEN N1 IS NOT NULL THEN O1 ELSE O2 END
        FROM #aff WHERE COALESCE(N1, O1) IS NOT NULL;
        COMMIT TRANSACTION;
        DROP TABLE #nd;
        DROP TABLE #nw;
        DROP TABLE #aff;
    END;

    SELECT @times = CONCAT(@times, N', site readers ', DATEDIFF(SECOND, @step, SYSUTCDATETIME()), N' s'), @step = SYSUTCDATETIME();

    /* ---- The conversations, from @FromKey on --------------------------------- */

    /* Loaded in parallel, which a columnstore takes a large load fastest by. Should SQL Server pick the load as a
       deadlock victim among its own threads (it did, loading 30 million conversations), it is loaded again on one thread. */
    DECLARE @sessionWhole BIT = CASE WHEN @FromKey IS NULL OR NOT EXISTS (SELECT 1 FROM dash.SessionSpan) THEN 1 ELSE 0 END;
    DECLARE @try INT = 0;
    WHILE 1 = 1
    BEGIN
        BEGIN TRY
            BEGIN TRANSACTION;
            IF @sessionWhole = 1 AND @mayTruncate = 1 TRUNCATE TABLE dash.SessionSpan;
            ELSE IF @sessionWhole = 1 DELETE FROM dash.SessionSpan;
            ELSE DELETE FROM dash.SessionSpan WHERE DateKey >= @FromKey;
            IF @try = 0
                INSERT INTO dash.SessionSpan WITH (TABLOCK) (SessionKey, DateKey, UserKey, AppKey, SessionStart, SpanMinutes, PromptCount)
                SELECT fs.SessionKey, fs.DateKey, fs.UserKey, fs.AppKey, fs.SessionStart, fs.SpanMinutes, fs.PromptCount
                FROM bi.FactSession AS fs
                WHERE @sessionWhole = 1 OR fs.DateKey >= @FromKey
                OPTION (RECOMPILE);
            ELSE
                INSERT INTO dash.SessionSpan WITH (TABLOCK) (SessionKey, DateKey, UserKey, AppKey, SessionStart, SpanMinutes, PromptCount)
                SELECT fs.SessionKey, fs.DateKey, fs.UserKey, fs.AppKey, fs.SessionStart, fs.SpanMinutes, fs.PromptCount
                FROM bi.FactSession AS fs
                WHERE @sessionWhole = 1 OR fs.DateKey >= @FromKey
                OPTION (RECOMPILE, MAXDOP 1);
            COMMIT TRANSACTION;
            BREAK;
        END TRY
        BEGIN CATCH
            IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
            IF ERROR_NUMBER() <> 1205 OR @try > 0 THROW;
            SET @try = 1;
            RAISERROR('  The load of dash.SessionSpan was chosen as a deadlock victim: loading it again on one thread.', 0, 1) WITH NOWAIT;
        END CATCH;
    END;

    /* Rows changed in place wait in delta stores, read row by row, until compressed: compressed now, where the caller may. */
    IF @mayTruncate = 1
    BEGIN
        ALTER INDEX CCI_DashSiteReader ON dash.SiteReader REORGANIZE WITH (COMPRESS_ALL_ROW_GROUPS = ON);
        ALTER INDEX CCI_DashSessionSpan ON dash.SessionSpan REORGANIZE WITH (COMPRESS_ALL_ROW_GROUPS = ON);
    END;

    /* Each person's minutes and conversations in each app over each period, from the conversations as they now stand:
       Usage by license summed them from every conversation of the period, 3 to 8 seconds for two years. */
    DROP TABLE IF EXISTS #sp;
    SELECT s.UserKey, s.AppKey,
           SUM(CASE WHEN s.DateKey >= @m7 THEN s.SpanMinutes ELSE 0 END) AS MM7, COUNT(CASE WHEN s.DateKey >= @m7 AND s.SpanMinutes > 0 THEN 1 END) AS NM7, COUNT(CASE WHEN s.DateKey >= @m7 THEN 1 END) AS SM7,
           SUM(CASE WHEN s.DateKey >= @m14 THEN s.SpanMinutes ELSE 0 END) AS MM14, COUNT(CASE WHEN s.DateKey >= @m14 AND s.SpanMinutes > 0 THEN 1 END) AS NM14, COUNT(CASE WHEN s.DateKey >= @m14 THEN 1 END) AS SM14,
           SUM(CASE WHEN s.DateKey >= @m30 THEN s.SpanMinutes ELSE 0 END) AS MM30, COUNT(CASE WHEN s.DateKey >= @m30 AND s.SpanMinutes > 0 THEN 1 END) AS NM30, COUNT(CASE WHEN s.DateKey >= @m30 THEN 1 END) AS SM30,
           SUM(CASE WHEN s.DateKey >= @m90 THEN s.SpanMinutes ELSE 0 END) AS MM90, COUNT(CASE WHEN s.DateKey >= @m90 AND s.SpanMinutes > 0 THEN 1 END) AS NM90, COUNT(CASE WHEN s.DateKey >= @m90 THEN 1 END) AS SM90,
           SUM(CASE WHEN s.DateKey >= @m180 THEN s.SpanMinutes ELSE 0 END) AS MM180, COUNT(CASE WHEN s.DateKey >= @m180 AND s.SpanMinutes > 0 THEN 1 END) AS NM180, COUNT(CASE WHEN s.DateKey >= @m180 THEN 1 END) AS SM180,
           SUM(CASE WHEN s.DateKey >= @m365 THEN s.SpanMinutes ELSE 0 END) AS MM365, COUNT(CASE WHEN s.DateKey >= @m365 AND s.SpanMinutes > 0 THEN 1 END) AS NM365, COUNT(CASE WHEN s.DateKey >= @m365 THEN 1 END) AS SM365,
           SUM(CASE WHEN s.DateKey >= @m730 THEN s.SpanMinutes ELSE 0 END) AS MM730, COUNT(CASE WHEN s.DateKey >= @m730 AND s.SpanMinutes > 0 THEN 1 END) AS NM730, COUNT(CASE WHEN s.DateKey >= @m730 THEN 1 END) AS SM730,
           SUM(s.SpanMinutes) AS MMAll, COUNT(CASE WHEN s.SpanMinutes > 0 THEN 1 END) AS NMAll, COUNT(*) AS SMAll,
           SUM(CASE WHEN s.DateKey BETWEEN @p7 AND @prev THEN s.SpanMinutes ELSE 0 END) AS MP7, COUNT(CASE WHEN s.DateKey BETWEEN @p7 AND @prev AND s.SpanMinutes > 0 THEN 1 END) AS NP7, COUNT(CASE WHEN s.DateKey BETWEEN @p7 AND @prev THEN 1 END) AS SP7,
           SUM(CASE WHEN s.DateKey BETWEEN @p14 AND @prev THEN s.SpanMinutes ELSE 0 END) AS MP14, COUNT(CASE WHEN s.DateKey BETWEEN @p14 AND @prev AND s.SpanMinutes > 0 THEN 1 END) AS NP14, COUNT(CASE WHEN s.DateKey BETWEEN @p14 AND @prev THEN 1 END) AS SP14,
           SUM(CASE WHEN s.DateKey BETWEEN @p30 AND @prev THEN s.SpanMinutes ELSE 0 END) AS MP30, COUNT(CASE WHEN s.DateKey BETWEEN @p30 AND @prev AND s.SpanMinutes > 0 THEN 1 END) AS NP30, COUNT(CASE WHEN s.DateKey BETWEEN @p30 AND @prev THEN 1 END) AS SP30,
           SUM(CASE WHEN s.DateKey BETWEEN @p90 AND @prev THEN s.SpanMinutes ELSE 0 END) AS MP90, COUNT(CASE WHEN s.DateKey BETWEEN @p90 AND @prev AND s.SpanMinutes > 0 THEN 1 END) AS NP90, COUNT(CASE WHEN s.DateKey BETWEEN @p90 AND @prev THEN 1 END) AS SP90,
           SUM(CASE WHEN s.DateKey BETWEEN @p180 AND @prev THEN s.SpanMinutes ELSE 0 END) AS MP180, COUNT(CASE WHEN s.DateKey BETWEEN @p180 AND @prev AND s.SpanMinutes > 0 THEN 1 END) AS NP180, COUNT(CASE WHEN s.DateKey BETWEEN @p180 AND @prev THEN 1 END) AS SP180,
           SUM(CASE WHEN s.DateKey BETWEEN @p365 AND @prev THEN s.SpanMinutes ELSE 0 END) AS MP365, COUNT(CASE WHEN s.DateKey BETWEEN @p365 AND @prev AND s.SpanMinutes > 0 THEN 1 END) AS NP365, COUNT(CASE WHEN s.DateKey BETWEEN @p365 AND @prev THEN 1 END) AS SP365,
           SUM(CASE WHEN s.DateKey BETWEEN @p730 AND @prev THEN s.SpanMinutes ELSE 0 END) AS MP730, COUNT(CASE WHEN s.DateKey BETWEEN @p730 AND @prev AND s.SpanMinutes > 0 THEN 1 END) AS NP730, COUNT(CASE WHEN s.DateKey BETWEEN @p730 AND @prev THEN 1 END) AS SP730,
           SUM(CASE WHEN s.DateKey <= @prev THEN s.SpanMinutes ELSE 0 END) AS MPAll, COUNT(CASE WHEN s.DateKey <= @prev AND s.SpanMinutes > 0 THEN 1 END) AS NPAll,
           COUNT(CASE WHEN s.DateKey <= @prev THEN 1 END) AS SPAll
    INTO #sp
    FROM dash.SessionSpan AS s
    WHERE s.DateKey BETWEEN @MinKey AND @MaxKey
    GROUP BY s.UserKey, s.AppKey
    OPTION (RECOMPILE);
    BEGIN TRANSACTION;
    IF @mayTruncate = 1 TRUNCATE TABLE dash.SessionPeriod; ELSE DELETE FROM dash.SessionPeriod;
    INSERT INTO dash.SessionPeriod WITH (TABLOCK) (EndKey, UserKey, AppKey, Minutes7, Minutes14, Minutes30, Minutes90, Minutes180, Minutes365, Minutes730, MinutesAll, Spanned7, Spanned14, Spanned30, Spanned90, Spanned180, Spanned365, Spanned730, SpannedAll, Sessions7, Sessions14, Sessions30, Sessions90, Sessions180, Sessions365, Sessions730, SessionsAll)
    SELECT @MaxKey, UserKey, AppKey, MM7, MM14, MM30, MM90, MM180, MM365, MM730, MMAll, NM7, NM14, NM30, NM90, NM180, NM365, NM730, NMAll, SM7, SM14, SM30, SM90, SM180, SM365, SM730, SMAll FROM #sp
    UNION ALL
    SELECT @PrevKey, UserKey, AppKey, MP7, MP14, MP30, MP90, MP180, MP365, MP730, MPAll, NP7, NP14, NP30, NP90, NP180, NP365, NP730, NPAll, SP7, SP14, SP30, SP90, SP180, SP365, SP730, SPAll FROM #sp WHERE SPAll > 0 AND @PrevKey IS NOT NULL
    OPTION (MAXDOP 1);
    COMMIT TRANSACTION;
    DROP TABLE #sp;
    SELECT @times = CONCAT(@times, N', conversations ', DATEDIFF(SECOND, @step, SYSUTCDATETIME()), N' s');
    DECLARE @ends NVARCHAR(40) = CONCAT(@MaxKey, N',', @PrevKey);
    MERGE dash.Setting AS t
    USING (SELECT 'DashResourceTotalsEnds' AS Name, @ends AS Value) AS s ON t.Name = s.Name
    WHEN MATCHED THEN UPDATE SET Value = s.Value, UpdatedBy = N'dash.usp_RefreshResourceTotals', UpdatedUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED THEN INSERT (Name, Value, Description, UpdatedBy)
        VALUES (s.Name, s.Value, N'The last days the totals of resources read end on. Written by each refresh once they are built.', N'dash.usp_RefreshResourceTotals');

    SET @msg = CONCAT(N'  Totals of resources read', CASE WHEN @FromKey IS NULL THEN N' (every day)' ELSE CONCAT(N', readers and conversations from ', @FromKey) END,
                      N': ', FORMAT(DATEDIFF(MILLISECOND, @started, SYSUTCDATETIME()) / 1000.0, 'N1'), N' s (', @times, N').');
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
END;
GO

IF OBJECT_ID(N'dash.usp_RefreshDashboard', N'P') IS NULL
    EXEC (N'CREATE PROCEDURE dash.usp_RefreshDashboard AS BEGIN SET NOCOUNT ON; END;');
GO

ALTER PROCEDURE dash.usp_RefreshDashboard
    @SinceUtc DATETIME2(0) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @started DATETIME2(0) = SYSUTCDATETIME(), @msg NVARCHAR(600);
    IF OBJECT_ID(N'bi.FactInteraction', N'U') IS NULL
        THROW 50201, N'The bi schema is missing: run sql/Create-CopilotBiSchema.sql and bi.usp_RefreshBi first.', 1;

    /* ---- 1. People, levels and the org closure ----------------------------- */

    DROP TABLE IF EXISTS #dir;
    CREATE TABLE #dir (Upn NVARCHAR(256) COLLATE DATABASE_DEFAULT NOT NULL PRIMARY KEY,
                       ManagerUpn NVARCHAR(256) COLLATE DATABASE_DEFAULT NULL, OrgDepth INT NULL);
    IF OBJECT_ID(N'audit.UserDirectory', N'U') IS NOT NULL
        EXEC (N'INSERT INTO #dir (Upn, ManagerUpn, OrgDepth)
                SELECT UserPrincipalName, NULLIF(NULLIF(ManagerUPN, N''''), UserPrincipalName), OrgDepth FROM audit.UserDirectory;');

    DROP TABLE IF EXISTS #clo;
    WITH c AS
    (
        SELECT d.Upn AS AncestorUpn, d.Upn AS DescendantUpn, 0 AS Depth
        FROM #dir AS d
        UNION ALL
        /* One step up: the current ancestor's own manager. A manager missing
           from #dir becomes an ancestor here and the walk stops there. */
        SELECT a.ManagerUpn, c.DescendantUpn, c.Depth + 1
        FROM c
        JOIN #dir AS a ON a.Upn = c.AncestorUpn
        WHERE a.ManagerUpn IS NOT NULL AND c.Depth < 40
    )
    SELECT AncestorUpn, DescendantUpn, MIN(Depth) AS Depth
    INTO #clo
    FROM c
    GROUP BY AncestorUpn, DescendantUpn
    OPTION (MAXRECURSION 41);

    DECLARE @top NVARCHAR(256) = (SELECT NULLIF(LTRIM(RTRIM(Value)), N'') FROM dash.Setting WHERE Name = 'OrgTopUpn');

    /* No top set: take it from the org chart - the one person with no manager
       who has at least half the directory under them - and record it, so the
       dashboard works with nothing configured. OrgTopUpn, when set, wins. */
    DECLARE @detected NVARCHAR(256) =
    (
        SELECT TOP (1) c.AncestorUpn
        FROM #clo AS c
        JOIN #dir AS d ON d.Upn = c.AncestorUpn AND d.ManagerUpn IS NULL
        GROUP BY c.AncestorUpn
        HAVING COUNT(*) * 2 >= (SELECT COUNT(*) FROM #dir)
        ORDER BY COUNT(*) DESC
    );
    MERGE dash.Setting AS t
    USING (SELECT 'OrgTopUpnDetected' AS Name, @detected AS Value) AS s ON t.Name = s.Name
    WHEN MATCHED THEN UPDATE SET Value = s.Value, UpdatedBy = N'dash.usp_RefreshDashboard', UpdatedUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED THEN INSERT (Name, Value, Description, UpdatedBy)
        VALUES (s.Name, s.Value, N'The top of the organization found in the org chart, used while OrgTopUpn is empty. Written by each refresh.', N'dash.usp_RefreshDashboard');
    SET @top = COALESCE(@top, @detected);

    DROP TABLE IF EXISTS #person;
    CREATE TABLE #person (Upn NVARCHAR(256) COLLATE DATABASE_DEFAULT NOT NULL PRIMARY KEY, DisplayName NVARCHAR(256) NULL,
                          ManagerUpn NVARCHAR(256) NULL, Department NVARCHAR(200) NULL, AccountEnabled BIT NULL,
                          Sid VARCHAR(184) NULL);
    /* The SID column arrives with the 2.2.0 enrichment schema; a directory table from before it has none. */
    DECLARE @sidCol NVARCHAR(4) = CASE WHEN COL_LENGTH(N'audit.UserDirectory', N'Sid') IS NULL THEN N'NULL' ELSE N'Sid' END;
    IF OBJECT_ID(N'audit.UserDirectory', N'U') IS NOT NULL
        EXEC (N'INSERT INTO #person (Upn, DisplayName, ManagerUpn, Department, AccountEnabled, Sid)
                SELECT UserPrincipalName, FullName, NULLIF(NULLIF(ManagerUPN, N''''), UserPrincipalName), Department, AccountEnabled, ' + @sidCol + N'
                FROM audit.UserDirectory;');
    /* Managers outside the directory table still get a row, so they can sign in and see their people. */
    INSERT INTO #person (Upn)
    SELECT DISTINCT c.AncestorUpn FROM #clo AS c WHERE NOT EXISTS (SELECT 1 FROM #person AS p WHERE p.Upn = c.AncestorUpn);

    /* A level counts every manager between a person and the top, those without a UPN too. The closure runs
       on ManagerUPN, which passes over a manager without a UPN to the one above, so a person under such a manager
       right below the top came out at level 2, which sees the whole organization. The AD load's OrgDepth counts them
       all; the deeper of the two is taken, so a level is never nearer the top than the chart puts it. */
    DECLARE @topDepth INT = (SELECT OrgDepth FROM #dir WHERE Upn = @top);

    BEGIN TRANSACTION;
    /* Truncated where the caller may: deleting 1.5 million pairs through their indexes took half a minute. */
    IF HAS_PERMS_BY_NAME(N'dash.OrgClosure', N'OBJECT', N'ALTER') = 1 TRUNCATE TABLE dash.OrgClosure; ELSE DELETE FROM dash.OrgClosure;
    /* With each person's bi.DimUser key and whether their account is enabled, which the pages read with the pairs. */
    INSERT INTO dash.OrgClosure (AncestorUpn, DescendantUpn, Depth, DescendantKey, DescendantEnabled)
    SELECT c.AncestorUpn, c.DescendantUpn, c.Depth, u.UserKey, p.AccountEnabled
    FROM #clo AS c
    LEFT JOIN bi.DimUser AS u ON u.UserPrincipalName = c.DescendantUpn
    LEFT JOIN #person AS p ON p.Upn = c.DescendantUpn;

    DELETE FROM dash.Person;
    INSERT INTO dash.Person (Upn, DisplayName, ManagerUpn, Department, AccountEnabled, Sid, Level, DirectReports, PeopleBelow)
    SELECT p.Upn, p.DisplayName, p.ManagerUpn, p.Department, p.AccountEnabled, p.Sid,
           CASE WHEN @top IS NULL OR t.DescendantUpn IS NULL THEN NULL
                WHEN d.OrgDepth - @topDepth > t.Depth THEN d.OrgDepth - @topDepth + 1
                ELSE t.Depth + 1 END,
           (SELECT COUNT(*) FROM #clo AS c WHERE c.AncestorUpn = p.Upn AND c.Depth = 1),
           (SELECT COUNT(*) FROM #clo AS c WHERE c.AncestorUpn = p.Upn AND c.Depth >= 1)
    FROM #person AS p
    LEFT JOIN #clo AS t ON t.AncestorUpn = @top AND t.DescendantUpn = p.Upn
    LEFT JOIN #dir AS d ON d.Upn = p.Upn;
    /* Each person's Entra ID object ID, which a sign-in through Entra ID is matched by first. The column arrives with
       the dashboard schema, and its source with the enrichment schema: either may not be there yet. */
    IF COL_LENGTH(N'dash.Person', N'ObjectId') IS NOT NULL AND COL_LENGTH(N'audit.CopilotLicense', N'EntraObjectId') IS NOT NULL
        EXEC (N'UPDATE p SET ObjectId = l.EntraObjectId
                FROM dash.Person AS p JOIN audit.CopilotLicense AS l ON l.UserPrincipalName = p.Upn
                WHERE l.EntraObjectId IS NOT NULL;');
    COMMIT TRANSACTION;

    DECLARE @people INT = (SELECT COUNT(*) FROM dash.Person), @closure INT = (SELECT COUNT(*) FROM dash.OrgClosure);

    /* ---- 2. The daily rollup ------------------------------------------------- */

    /* Not while a full bi rebuild has not finished, running or stopped part way: the facts are half written.
       A rollup over them took every month the rebuild had not reached yet out of the rollups (the dashboard
       installer's refresh during an upgrade's rebuild, say), and the windowed rollups after it never put them back.
       The run that finishes the rebuild is a full one, so the first rollup after it builds every day (below). */
    DECLARE @rebuilding BIT = 0;
    IF OBJECT_ID(N'bi.RebuildInProgress', N'U') IS NOT NULL
        SET @rebuilding = CASE WHEN EXISTS (SELECT 1 FROM bi.RebuildInProgress) THEN 1 ELSE 0 END;
    IF @rebuilding = 1
    BEGIN
        SET @msg = CONCAT(N'Dashboard: org chart refreshed (', FORMAT(@people, 'N0'), N' people). The rollups are left as they are: a full ',
                          N'refresh of the bi schema has not finished (it is running, or was stopped part way). The next bi refresh finishes it, ',
                          N'and the first dashboard refresh after that rolls up every day.');
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
        RETURN;
    END;

    DECLARE @fromKey INT = NULL;
    IF @SinceUtc IS NOT NULL
    BEGIN
        /* The bi facts are dated in the reporting zone of the last bi refresh. */
        DECLARE @zone SYSNAME = (SELECT TOP (1) ReportingTimeZone FROM bi.RefreshRun ORDER BY RunId DESC);
        DECLARE @localDay DATE = CAST(CAST(@SinceUtc AS DATETIMEOFFSET) AT TIME ZONE ISNULL(@zone, N'UTC') AS DATE);
        SET @fromKey = YEAR(@localDay) * 10000 + MONTH(@localDay) * 100 + DAY(@localDay);
    END;

    /* The bi refreshes since the last rollup, read before the facts are: a full rebuild that starts while this reads
       them is one of the next rollup's, which then builds every day. */
    DECLARE @biRun INT = (SELECT MAX(RunId) FROM bi.RefreshRun);
    DECLARE @lastBiRun INT = ISNULL((SELECT TOP (1) r.BiRunId FROM dash.RefreshRun AS r WHERE r.BiRunId IS NOT NULL ORDER BY r.RunId DESC), 0);

    /* A full bi refresh since the last rollup rewrote every interaction (a new rule for Premium or licensed
       use, license history that arrived late, another time zone, a stopped rebuild the nightly run finished), so
       every day is rolled up again. Before, only the window asked for was, and every older day kept the figures of
       the facts before it, for good: the dashboard and Power BI disagreed, and an upgrade's new Premium rule never
       reached the days before it (Forecasting's revoke list among them). */
    IF @fromKey IS NOT NULL AND EXISTS (SELECT 1 FROM bi.RefreshRun WHERE RunId > @lastBiRun AND SinceUtc IS NULL)
    BEGIN
        RAISERROR('A full refresh of the bi schema ran since the last rollup: rolling up every day.', 0, 1) WITH NOWAIT;
        SET @fromKey = NULL;
    END;

    /* A bi refresh since the last rollup that changed an app's category moves the start back to the
       first day that app was used: Premium use and ChatInteractions depend on the category. */
    IF @fromKey IS NOT NULL
    BEGIN
        DECLARE @recatFrom INT = (SELECT MIN(b.RecategorizedFromDateKey) FROM bi.RefreshRun AS b WHERE b.RunId > @lastBiRun);
        IF @recatFrom < @fromKey SET @fromKey = @recatFrom;
    END;

    DECLARE @noAgent INT = (SELECT AgentKey FROM bi.DimAgent WHERE AgentId = N'(none)');

    DROP TABLE IF EXISTS #day;
    WITH i AS
    (
        SELECT f.DateKey, f.UserKey,
               COUNT_BIG(*)                                                          AS Interactions,
               SUM(f.PromptCount)                                                    AS Prompts,
               COUNT(DISTINCT f.AppKey)                                              AS AppsUsed,
               SUM(CASE WHEN f.AgentKey <> ISNULL(@noAgent, -1) THEN 1 ELSE 0 END)   AS AgentInteractions,
               SUM(CAST(f.UsedWebSearch AS INT))                                     AS WebGrounded,
               SUM(CASE WHEN a.AppCategory = N'Copilot Chat' THEN 1 ELSE 0 END)      AS ChatInteractions,
               SUM(CASE WHEN f.LicenseStatusKey = 1 THEN 1 ELSE 0 END)               AS LicensedUse,
               SUM(CAST(f.PremiumUse AS INT))                                        AS PremiumUse,   -- set by bi.usp_RefreshBi, the rule's one definition
               SUM(CAST(f.JailbreakDetected AS INT))                                 AS Jailbreak,
               SUM(CAST(f.PromptInjectionDetected AS INT))                           AS PromptInjection,
               SUM(CAST(f.HasBlockedResource AS INT))                                AS BlockedResource,
               SUM(CAST(f.HasSensitivityLabel AS INT))                               AS LabeledContent
        FROM bi.FactInteraction AS f
        JOIN bi.DimApp AS a ON a.AppKey = f.AppKey
        WHERE @fromKey IS NULL OR f.DateKey >= @fromKey
        GROUP BY f.DateKey, f.UserKey
    ),
    s AS
    (
        SELECT s.DateKey, s.UserKey, COUNT(*) AS Sessions, SUM(CAST(s.IsMultiTurn AS INT)) AS MultiTurnSessions
        FROM bi.FactSession AS s
        WHERE @fromKey IS NULL OR s.DateKey >= @fromKey
        GROUP BY s.DateKey, s.UserKey
    )
    SELECT COALESCE(i.DateKey, s.DateKey) AS DateKey, COALESCE(i.UserKey, s.UserKey) AS UserKey,
           CAST(ISNULL(i.Interactions, 0) AS INT) AS Interactions, ISNULL(i.Prompts, 0) AS Prompts,
           CAST(ISNULL(i.AppsUsed, 0) AS TINYINT) AS AppsUsed,
           ISNULL(i.AgentInteractions, 0) AS AgentInteractions, ISNULL(i.WebGrounded, 0) AS WebGrounded,
           ISNULL(i.ChatInteractions, 0) AS ChatInteractions, ISNULL(i.LicensedUse, 0) AS LicensedUse, ISNULL(i.PremiumUse, 0) AS PremiumUse,
           ISNULL(i.Jailbreak, 0) AS Jailbreak, ISNULL(i.PromptInjection, 0) AS PromptInjection,
           ISNULL(i.BlockedResource, 0) AS BlockedResource, ISNULL(i.LabeledContent, 0) AS LabeledContent,
           ISNULL(s.Sessions, 0) AS Sessions, ISNULL(s.MultiTurnSessions, 0) AS MultiTurnSessions
    INTO #day
    FROM i
    FULL JOIN s ON s.DateKey = i.DateKey AND s.UserKey = i.UserKey
    OPTION (RECOMPILE);

    /* Labels and sites, from bi.FactAccessedResource: the label on each resource
       read, the site of labeled content, and the site of content Copilot was
       blocked from. Sites are already cut to https://host/sites/name there.
       Interactions, not resources: the fact has a row per resource, and
       each was counted, so an answer drawn from six files with one label added
       six to that label and its site, where the page's own count of labeled
       interactions added one, and the label alert fired on a single interaction. */
    DROP TABLE IF EXISTS #res;
    WITH r AS
    (
        SELECT f.DateKey, f.UserKey, f.RecordId, f.IsBlocked, l.LabelId, s.Site
        FROM bi.FactAccessedResource AS f
        JOIN bi.DimSensitivityLabel AS l ON l.SensitivityLabelKey = f.SensitivityLabelKey
        JOIN bi.DimSite AS s ON s.SiteKey = f.SiteKey
        WHERE (@fromKey IS NULL OR f.DateKey >= @fromKey)
          AND (l.LabelId IS NOT NULL OR f.IsBlocked = 1)
    ),
    items AS
    (
        SELECT DateKey, UserKey, RecordId, 'Label' AS Kind, LOWER(CAST(LabelId AS NVARCHAR(36))) AS Item FROM r WHERE LabelId IS NOT NULL
        UNION ALL
        SELECT DateKey, UserKey, RecordId, 'Site', Site FROM r WHERE LabelId IS NOT NULL AND Site <> N'(none)'
        UNION ALL
        SELECT DateKey, UserKey, RecordId, 'Blocked', Site FROM r WHERE IsBlocked = 1 AND Site <> N'(none)'
    )
    SELECT DateKey, UserKey, Kind, CAST(LEFT(Item, 400) AS NVARCHAR(400)) AS Item, COUNT(DISTINCT RecordId) AS Interactions
    INTO #res
    FROM items
    GROUP BY DateKey, UserKey, Kind, LEFT(Item, 400)
    OPTION (RECOMPILE);

    BEGIN TRANSACTION;
    IF @fromKey IS NULL AND HAS_PERMS_BY_NAME(N'dash.UserDay', N'OBJECT', N'ALTER') = 1
        TRUNCATE TABLE dash.UserDay;
    ELSE IF @fromKey IS NULL
        DELETE FROM dash.UserDay;
    ELSE
        DELETE FROM dash.UserDay WHERE DateKey >= @fromKey;
    INSERT INTO dash.UserDay WITH (TABLOCK)
        (DateKey, UserKey, Interactions, Prompts, AppsUsed, AgentInteractions, WebGrounded, ChatInteractions,
         LicensedUse, PremiumUse, Jailbreak, PromptInjection, BlockedResource, LabeledContent, Sessions, MultiTurnSessions)
    SELECT DateKey, UserKey, Interactions, Prompts, AppsUsed, AgentInteractions, WebGrounded, ChatInteractions,
           LicensedUse, PremiumUse, Jailbreak, PromptInjection, BlockedResource, LabeledContent, Sessions, MultiTurnSessions
    FROM #day;
    DECLARE @dayRows INT = @@ROWCOUNT;
    IF @fromKey IS NULL AND HAS_PERMS_BY_NAME(N'dash.ResourceDay', N'OBJECT', N'ALTER') = 1
        TRUNCATE TABLE dash.ResourceDay;
    ELSE IF @fromKey IS NULL
        DELETE FROM dash.ResourceDay;
    ELSE
        DELETE FROM dash.ResourceDay WHERE DateKey >= @fromKey;
    INSERT INTO dash.ResourceDay WITH (TABLOCK) (DateKey, UserKey, Kind, Item, Interactions)
    SELECT DateKey, UserKey, Kind, Item, Interactions FROM #res;
    /* The flagged interactions, for Security > Flagged interactions. From @fromKey on, or all of them when
       the table is empty, as it is the first time after an upgrade (a refresh of today alone would leave history out). */
    IF OBJECT_ID(N'dash.FlaggedInteraction', N'U') IS NOT NULL
    BEGIN
        DECLARE @flagFrom INT = CASE WHEN EXISTS (SELECT 1 FROM dash.FlaggedInteraction) THEN @fromKey ELSE NULL END;
        DELETE FROM dash.FlaggedInteraction WHERE @flagFrom IS NULL OR DateKey >= @flagFrom;
        INSERT INTO dash.FlaggedInteraction (DateKey, UserKey, RecordId, ActivityHour, AppKey, AgentKey, JailbreakDetected, PromptInjectionDetected, HasBlockedResource)
        SELECT f.DateKey, f.UserKey, f.RecordId, f.ActivityHour, f.AppKey, f.AgentKey, f.JailbreakDetected, f.PromptInjectionDetected, f.HasBlockedResource
        FROM bi.FactInteraction AS f
        WHERE (@flagFrom IS NULL OR f.DateKey >= @flagFrom)
          AND (f.JailbreakDetected = 1 OR f.PromptInjectionDetected = 1 OR f.HasBlockedResource = 1);
    END;
    DELETE FROM dash.AgentUse;
    INSERT INTO dash.AgentUse (AgentKey, FirstDateKey, LastDateKey, Interactions)
    SELECT AgentKey, MIN(DateKey), MAX(DateKey), COUNT_BIG(*) FROM bi.FactInteraction GROUP BY AgentKey;
    COMMIT TRANSACTION;

    /* Each person's use of each app and each agent per day (dash.UserItemDay), from the same day as dash.UserDay, or
       every day the first time. Apps and agents apart: most interactions have no agent, and are not read twice. Also
       per use case (U), license type Copilot recorded (L), model (M), and the interactions with each of three flags
       (F). The kinds the table holds are recorded (setting DashItemKinds): a database whose table was built with
       fewer kinds is built whole once, so the new kinds cover every day. */
    DECLARE @kinds NVARCHAR(40) = N'A,G,U,L,M,F';
    DECLARE @itemFrom INT = CASE WHEN EXISTS (SELECT 1 FROM dash.UserItemDay)
                                  AND EXISTS (SELECT 1 FROM dash.Setting WHERE Name = 'DashItemKinds' AND Value = @kinds) THEN @fromKey END;
    DROP TABLE IF EXISTS #item;
    SELECT f.DateKey, f.UserKey, CAST('A' AS CHAR(1)) AS Kind, f.AppKey AS Item, CAST(COUNT_BIG(*) AS INT) AS Interactions,
           ISNULL(SUM(f.PromptCount), 0) AS Prompts, ISNULL(SUM(f.AccessedResourceCount), 0) AS Resources,
           ISNULL(SUM(CAST(f.UsedWebSearch AS INT)), 0) AS Web
    INTO #item
    FROM bi.FactInteraction AS f
    WHERE @itemFrom IS NULL OR f.DateKey >= @itemFrom
    GROUP BY f.DateKey, f.UserKey, f.AppKey
    OPTION (RECOMPILE);
    INSERT INTO #item WITH (TABLOCK) (DateKey, UserKey, Kind, Item, Interactions, Prompts, Resources, Web)
    SELECT f.DateKey, f.UserKey, 'G', f.AgentKey, CAST(COUNT_BIG(*) AS INT), ISNULL(SUM(f.PromptCount), 0),
           ISNULL(SUM(f.AccessedResourceCount), 0), ISNULL(SUM(CAST(f.UsedWebSearch AS INT)), 0)
    FROM bi.FactInteraction AS f
    WHERE (@itemFrom IS NULL OR f.DateKey >= @itemFrom) AND f.AgentKey <> ISNULL(@noAgent, -1)
    GROUP BY f.DateKey, f.UserKey, f.AgentKey
    OPTION (RECOMPILE);
    INSERT INTO #item WITH (TABLOCK) (DateKey, UserKey, Kind, Item, Interactions, Prompts, Resources, Web)
    SELECT f.DateKey, f.UserKey, k.Kind, k.Item, CAST(COUNT_BIG(*) AS INT), ISNULL(SUM(f.PromptCount), 0),
           ISNULL(SUM(f.AccessedResourceCount), 0), ISNULL(SUM(CAST(f.UsedWebSearch AS INT)), 0)
    FROM bi.FactInteraction AS f
    CROSS APPLY (VALUES ('U', f.UseCaseKey), ('L', f.LicenseTypeKey), ('M', f.ModelKey),
                        ('F', CASE WHEN f.MemoryUpdated = 1 THEN 1 END), ('F', CASE WHEN f.CalledAnotherAgent = 1 THEN 2 END),
                        ('F', CASE WHEN f.DlpEvaluationDeferred = 1 THEN 3 END)) AS k (Kind, Item)
    WHERE (@itemFrom IS NULL OR f.DateKey >= @itemFrom) AND k.Item IS NOT NULL
    GROUP BY f.DateKey, f.UserKey, k.Kind, k.Item
    OPTION (RECOMPILE);
    /* Written whole, the table's columnstore is set aside and built again after, in one pass: kept up row by row, a
       first build of tens of millions of rows takes minutes in its delta stores. */
    DECLARE @itemWhole BIT = CASE WHEN @itemFrom IS NULL AND HAS_PERMS_BY_NAME(N'dash.UserItemDay', N'OBJECT', N'ALTER') = 1 THEN 1 ELSE 0 END;
    BEGIN TRANSACTION;
    IF @itemWhole = 1
    BEGIN
        ALTER INDEX NCCI_DashUserItemDay ON dash.UserItemDay DISABLE;
        TRUNCATE TABLE dash.UserItemDay;
    END;
    ELSE IF @itemFrom IS NULL
        DELETE FROM dash.UserItemDay;
    ELSE
        DELETE FROM dash.UserItemDay WHERE DateKey >= @itemFrom;
    INSERT INTO dash.UserItemDay WITH (TABLOCK) (DateKey, UserKey, Kind, Item, Interactions, Prompts, Resources, Web)
    SELECT DateKey, UserKey, Kind, Item, Interactions, Prompts, Resources, Web FROM #item;
    IF @itemWhole = 1
        ALTER INDEX NCCI_DashUserItemDay ON dash.UserItemDay REBUILD;
    MERGE dash.Setting AS t
    USING (SELECT 'DashItemKinds' AS Name, @kinds AS Value) AS s ON t.Name = s.Name
    WHEN MATCHED AND t.Value <> s.Value THEN UPDATE SET Value = s.Value, UpdatedBy = N'dash.usp_RefreshDashboard', UpdatedUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED THEN INSERT (Name, Value, Description, UpdatedBy)
        VALUES (s.Name, s.Value, N'The kinds dash.UserItemDay holds. Written by each refresh: one built with fewer is built whole once.', N'dash.usp_RefreshDashboard');
    COMMIT TRANSACTION;
    DROP TABLE #item;

    /* What the Copilot Credit estimate counts, per person, agent and day (dash.CreditUseDay), from the same day as
       dash.UserItemDay, or every day the first time. Billed: an agent used by someone not licensed at the time
       (status 2; an unknown status is not taken for unlicensed), or in a Copilot Studio channel. */
    DECLARE @creditFrom INT = CASE WHEN EXISTS (SELECT 1 FROM dash.CreditUseDay) THEN @itemFrom END;
    DROP TABLE IF EXISTS #credit;
    SELECT fi.DateKey, fi.UserKey, fi.AgentKey,
           CAST(COUNT_BIG(*) AS INT) AS I, SUM(x.IsAgent) AS A, SUM(x.Billed) AS B, SUM(x.Billed * x.Work) AS BW, SUM(x.Billed * x.Calls) AS BA,
           SUM(CAST(ISNULL(fi.AccessedResourceCount, 0) AS BIGINT)) AS R, SUM(CAST(ISNULL(fi.MessageCount, 1) AS BIGINT)) AS M,
           CAST(MAX(x.Lic) AS TINYINT) AS Lic, CAST(MAX(x.Billed) AS TINYINT) AS AnyBilled
    INTO #credit
    FROM bi.FactInteraction AS fi
    JOIN bi.DimAgent AS ag ON ag.AgentKey = fi.AgentKey
    JOIN bi.DimApp AS ap ON ap.AppKey = fi.AppKey
    CROSS APPLY (SELECT CASE WHEN ag.AgentSource = N'(no agent)' THEN 0 ELSE 1 END AS IsAgent,
                        CASE WHEN fi.LicenseStatusKey = 1 THEN 1 ELSE 0 END AS Lic,
                        CASE WHEN ag.AgentSource <> N'(no agent)' AND (fi.LicenseStatusKey = 2 OR ap.App = N'Copilot Studio') THEN 1 ELSE 0 END AS Billed,
                        CASE WHEN fi.AccessedResourceCount > 0 OR fi.UseKind IN (1, 3) THEN 1 ELSE 0 END AS Work,
                        CAST(ISNULL(fi.CalledAnotherAgent, 0) AS INT) AS Calls) AS x
    WHERE @creditFrom IS NULL OR fi.DateKey >= @creditFrom
    GROUP BY fi.DateKey, fi.UserKey, fi.AgentKey
    OPTION (RECOMPILE);
    DECLARE @creditWhole BIT = CASE WHEN @creditFrom IS NULL AND HAS_PERMS_BY_NAME(N'dash.CreditUseDay', N'OBJECT', N'ALTER') = 1 THEN 1 ELSE 0 END;
    BEGIN TRANSACTION;
    IF @creditWhole = 1
    BEGIN
        ALTER INDEX NCCI_DashCreditUseDay ON dash.CreditUseDay DISABLE;
        TRUNCATE TABLE dash.CreditUseDay;
    END;
    ELSE IF @creditFrom IS NULL
        DELETE FROM dash.CreditUseDay;
    ELSE
        DELETE FROM dash.CreditUseDay WHERE DateKey >= @creditFrom;
    INSERT INTO dash.CreditUseDay WITH (TABLOCK) (DateKey, UserKey, AgentKey, I, A, B, BW, BA, R, M, Lic, AnyBilled)
    SELECT DateKey, UserKey, AgentKey, I, A, B, BW, BA, R, M, Lic, AnyBilled FROM #credit;
    IF @creditWhole = 1
        ALTER INDEX NCCI_DashCreditUseDay ON dash.CreditUseDay REBUILD;
    COMMIT TRANSACTION;
    DROP TABLE #credit;

    /* ---- 3. Totals of dash.UserDay the pages read ---------------------------- */

    /* Each is worked out from dash.UserDay as it now stands, into a temporary table, then put in place in a short
       transaction of its own: a page reading one waits for the swap, not for the sums. A page that reads one while
       another is still being swapped is kept under the data stamp from before, which this run's dash.RefreshRun row
       ends. Where the table is empty (the first refresh after an upgrade), or the run rebuilds every day, each is
       built whole. */
    DECLARE @maxKey INT = (SELECT MAX(DateKey) FROM dash.UserDay);
    DECLARE @minKey INT = (SELECT MIN(DateKey) FROM dash.UserDay);
    /* The last days the period totals end on, recorded once they are all built (setting DashTotalsEnds): the pages read
       that, where looking for each period's rows in the two columnstores cost them a second a request. Cleared while
       they are rebuilt, so a refresh stopped part way leaves the pages summing the days. */
    UPDATE dash.Setting SET Value = NULL, UpdatedBy = N'dash.usp_RefreshDashboard', UpdatedUtc = SYSUTCDATETIME() WHERE Name = 'DashTotalsEnds';

    /* The week @fromKey falls in: the per app and agent weeks are rebuilt from it. */
    DECLARE @weekFrom DATE = NULL, @weekFromKey INT = NULL;
    IF @fromKey IS NOT NULL AND EXISTS (SELECT 1 FROM dash.UserWeek)
        SELECT @weekFrom = d.WeekEnding,
               @weekFromKey = (SELECT s.DateKey FROM bi.DimDate AS s WHERE s.[Date] = DATEADD(DAY, -6, d.WeekEnding))
        FROM bi.DimDate AS d WHERE d.DateKey = @fromKey;
    IF @weekFromKey IS NULL SET @weekFrom = NULL;
    /* By week, whole: with whether the person held a license on the week's last day, which bi.usp_RefreshBi can change
       for any day when it rebuilds the license days. */
    DROP TABLE IF EXISTS #week;
    WITH w AS (
    SELECT d.WeekEnding, ud.UserKey, CAST(COUNT(CASE WHEN ud.Interactions > 0 THEN 1 END) AS TINYINT) AS ActiveDays,
           SUM(ud.Interactions) AS Interactions, SUM(ud.Prompts) AS Prompts, SUM(ud.AgentInteractions) AS AgentInteractions,
           SUM(ud.WebGrounded) AS WebGrounded, SUM(ud.ChatInteractions) AS ChatInteractions, SUM(ud.LicensedUse) AS LicensedUse,
           SUM(ud.PremiumUse) AS PremiumUse, SUM(ud.Jailbreak) AS Jailbreak, SUM(ud.PromptInjection) AS PromptInjection,
           SUM(ud.BlockedResource) AS BlockedResource, SUM(ud.LabeledContent) AS LabeledContent, SUM(ud.Sessions) AS Sessions,
           SUM(ud.MultiTurnSessions) AS MultiTurnSessions
    FROM dash.UserDay AS ud
    JOIN bi.DimDate AS d ON d.DateKey = ud.DateKey
    GROUP BY d.WeekEnding, ud.UserKey)
    SELECT w.*, CAST(CASE WHEN ld.UserKey IS NULL THEN 0 ELSE 1 END AS BIT) AS HeldAtEnd
    INTO #week
    FROM w
    LEFT JOIN bi.FactLicensedDay AS ld ON ld.DateKey = CONVERT(INT, CONVERT(CHAR(8), w.WeekEnding, 112)) AND ld.UserKey = w.UserKey
    OPTION (RECOMPILE);

    /* The same per app and agent (dash.UserItemWeek), from the same week, or every week the first time and whenever
       dash.UserItemDay was just built whole (new kinds). */
    DECLARE @itemWeekFrom DATE = CASE WHEN @itemFrom IS NOT NULL AND EXISTS (SELECT 1 FROM dash.UserItemWeek) THEN @weekFrom END;
    DECLARE @itemWeekFromKey INT = CASE WHEN @itemWeekFrom IS NOT NULL THEN @weekFromKey END;
    DROP TABLE IF EXISTS #itemweek;
    SELECT d.WeekEnding, ui.UserKey, ui.Kind, ui.Item, CAST(COUNT(*) AS TINYINT) AS ActiveDays, SUM(ui.Interactions) AS Interactions,
           SUM(ui.Prompts) AS Prompts, SUM(ui.Resources) AS Resources, SUM(ui.Web) AS Web
    INTO #itemweek
    FROM dash.UserItemDay AS ui
    JOIN bi.DimDate AS d ON d.DateKey = ui.DateKey
    WHERE @itemWeekFromKey IS NULL OR ui.DateKey >= @itemWeekFromKey
    GROUP BY d.WeekEnding, ui.UserKey, ui.Kind, ui.Item
    OPTION (RECOMPILE);

    /* Adoption's 28 days: each day a person used Copilot with a license keeps them adopted until their next such
       day, or for 28 days, so each person-day is written once. From @fromKey on: a day looks back, never forward,
       so the days before it keep theirs; it reads the 27 days before @fromKey for them. */
    DECLARE @adoptFrom INT = CASE WHEN @fromKey IS NOT NULL AND EXISTS (SELECT 1 FROM dash.AdoptionDay) THEN @fromKey END;
    DECLARE @adoptFromN INT = (SELECT DATEDIFF(DAY, 0, [Date]) FROM bi.DimDate WHERE DateKey = @adoptFrom);
    IF @adoptFromN IS NULL SET @adoptFrom = NULL;
    DECLARE @useFromKey INT = (SELECT MIN(DateKey) FROM bi.DimDate WHERE DATEDIFF(DAY, 0, [Date]) >= @adoptFromN - 27);
    DROP TABLE IF EXISTS #adopt;
    WITH used AS (SELECT ud.UserKey, DATEDIFF(DAY, 0, d.[Date]) AS N
                  FROM dash.UserDay AS ud JOIN bi.DimDate AS d ON d.DateKey = ud.DateKey
                  WHERE ud.LicensedUse > 0 AND (@adoptFrom IS NULL OR ud.DateKey >= @useFromKey)),
    iv AS (SELECT u.UserKey, u.N, ISNULL(LEAD(u.N) OVER (PARTITION BY u.UserKey ORDER BY u.N) - u.N, 28) AS Gap FROM used AS u),
    k AS (SELECT n FROM (VALUES (0), (1), (2), (3), (4), (5), (6), (7), (8), (9), (10), (11), (12), (13), (14), (15), (16), (17), (18), (19),
                                (20), (21), (22), (23), (24), (25), (26), (27)) AS v (n)),
    cover AS (SELECT iv.UserKey, iv.N + k.n AS DayN FROM iv JOIN k ON k.n < iv.Gap)
    SELECT d.DateKey, c.UserKey
    INTO #adopt
    FROM cover AS c
    JOIN bi.DimDate AS d ON DATEDIFF(DAY, 0, d.[Date]) = c.DayN
    WHERE d.DateKey <= @maxKey AND (@adoptFrom IS NULL OR d.DateKey >= @adoptFrom)
    OPTION (RECOMPILE);

    /* Each person and each site they read labeled content from or were blocked at, with the last day they did and the
       last before @maxKey (dash.SitePerson), from dash.ResourceDay as it now stands: whole, as both move every day. */
    DROP TABLE IF EXISTS #siteperson;
    SELECT CAST(CASE r.Kind WHEN 'Site' THEN 'S' ELSE 'B' END AS CHAR(1)) AS Kind, s.SiteKey, r.UserKey,
           MAX(r.DateKey) AS LastKey, MAX(CASE WHEN r.DateKey < @maxKey THEN r.DateKey END) AS PrevKey
    INTO #siteperson
    FROM dash.ResourceDay AS r
    JOIN bi.DimSite AS s ON CAST(LEFT(s.Site, 400) AS NVARCHAR(400)) = r.Item
    WHERE r.Kind IN ('Site', 'Blocked')
    GROUP BY r.Kind, s.SiteKey, r.UserKey
    OPTION (RECOMPILE);

    /* Each person's first and last day of use, over every day. */
    DROP TABLE IF EXISTS #act;
    SELECT ud.UserKey, MIN(CASE WHEN ud.Interactions > 0 THEN ud.DateKey END) AS FirstActiveKey,
           MAX(CASE WHEN ud.Interactions > 0 THEN ud.DateKey END) AS LastActiveKey
    INTO #act
    FROM dash.UserDay AS ud
    GROUP BY ud.UserKey;

    /* Each person's sums over every period a page offers, ending on the last day with data or the day before it: a
       page's period ends on the last complete day, which is one of the two whether or not today has begun arriving.
       Days 0 is All data, from the first day with data. */
    DROP TABLE IF EXISTS #period;
    CREATE TABLE #period (EndKey INT NOT NULL, Days SMALLINT NOT NULL, UserKey INT NOT NULL, ActiveDays SMALLINT NOT NULL, PremiumDays SMALLINT NOT NULL,
                          LastActiveKey INT NULL, MostApps TINYINT NOT NULL, Interactions INT NOT NULL, Prompts INT NOT NULL, AgentInteractions INT NOT NULL, WebGrounded INT NOT NULL,
                          ChatInteractions INT NOT NULL, LicensedUse INT NOT NULL, PremiumUse INT NOT NULL, Jailbreak INT NOT NULL, PromptInjection INT NOT NULL,
                          BlockedResource INT NOT NULL, LabeledContent INT NOT NULL, Sessions INT NOT NULL, MultiTurnSessions INT NOT NULL, HeldUse INT NOT NULL);
    DROP TABLE IF EXISTS #itemperiod;
    CREATE TABLE #itemperiod (EndKey INT NOT NULL, Days SMALLINT NOT NULL, Kind CHAR(1) NOT NULL, Item INT NOT NULL, UserKey INT NOT NULL,
                              ActiveDays SMALLINT NOT NULL, LastActiveKey INT NOT NULL, Interactions INT NOT NULL, Prompts INT NOT NULL,
                              Resources INT NOT NULL, Web INT NOT NULL);
    DROP TABLE IF EXISTS #creditperiod;
    CREATE TABLE #creditperiod (EndKey INT NOT NULL, Days SMALLINT NOT NULL, UserKey INT NOT NULL, AgentKey INT NOT NULL, I BIGINT NOT NULL,
                                A BIGINT NOT NULL, B BIGINT NOT NULL, BW BIGINT NOT NULL, BA BIGINT NOT NULL, R BIGINT NOT NULL, M BIGINT NOT NULL,
                                Lic TINYINT NOT NULL, AnyBilled TINYINT NOT NULL);
    DECLARE @pEnd INT, @pDays SMALLINT, @pFrom INT;
    DECLARE periods CURSOR LOCAL FAST_FORWARD FOR
        SELECT e.EndKey, p.Days
        FROM (SELECT @maxKey AS EndKey UNION SELECT MAX(DateKey) FROM dash.UserDay WHERE DateKey < @maxKey) AS e
        CROSS JOIN (VALUES (7), (14), (30), (90), (180), (365), (730), (0)) AS p (Days)
        WHERE e.EndKey IS NOT NULL;
    OPEN periods;
    FETCH NEXT FROM periods INTO @pEnd, @pDays;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @pFrom = CASE WHEN @pDays = 0 THEN @minKey
                          ELSE CONVERT(INT, CONVERT(CHAR(8), DATEADD(DAY, 1 - @pDays, CONVERT(DATE, CONVERT(CHAR(8), @pEnd))), 112)) END;
        INSERT INTO #period WITH (TABLOCK)
        SELECT @pEnd, @pDays, ud.UserKey, COUNT(CASE WHEN ud.Interactions > 0 THEN 1 END), COUNT(CASE WHEN ud.PremiumUse > 0 THEN 1 END),
               MAX(CASE WHEN ud.Interactions > 0 THEN ud.DateKey END), MAX(ud.AppsUsed),
               SUM(ud.Interactions), SUM(ud.Prompts), SUM(ud.AgentInteractions), SUM(ud.WebGrounded), SUM(ud.ChatInteractions),
               SUM(ud.LicensedUse), SUM(ud.PremiumUse), SUM(ud.Jailbreak), SUM(ud.PromptInjection), SUM(ud.BlockedResource),
               SUM(ud.LabeledContent), SUM(ud.Sessions), SUM(ud.MultiTurnSessions),
               SUM(CASE WHEN ld.UserKey IS NOT NULL THEN ud.Interactions ELSE 0 END)   -- use on days holding a license
        FROM dash.UserDay AS ud
        LEFT JOIN bi.FactLicensedDay AS ld ON ld.DateKey = ud.DateKey AND ld.UserKey = ud.UserKey
        WHERE ud.DateKey BETWEEN @pFrom AND @pEnd
        GROUP BY ud.UserKey
        OPTION (RECOMPILE);
        INSERT INTO #itemperiod WITH (TABLOCK)
        SELECT @pEnd, @pDays, ui.Kind, ui.Item, ui.UserKey, COUNT(*), MAX(ui.DateKey), SUM(ui.Interactions), SUM(ui.Prompts), SUM(ui.Resources), SUM(ui.Web)
        FROM dash.UserItemDay AS ui
        WHERE ui.DateKey BETWEEN @pFrom AND @pEnd
        GROUP BY ui.Kind, ui.Item, ui.UserKey
        OPTION (RECOMPILE);
        INSERT INTO #creditperiod WITH (TABLOCK)
        SELECT @pEnd, @pDays, cu.UserKey, cu.AgentKey, SUM(CAST(cu.I AS BIGINT)), SUM(CAST(cu.A AS BIGINT)), SUM(CAST(cu.B AS BIGINT)),
               SUM(CAST(cu.BW AS BIGINT)), SUM(CAST(cu.BA AS BIGINT)), SUM(cu.R), SUM(cu.M), MAX(cu.Lic), MAX(cu.AnyBilled)
        FROM dash.CreditUseDay AS cu
        WHERE cu.DateKey BETWEEN @pFrom AND @pEnd
        GROUP BY cu.UserKey, cu.AgentKey
        OPTION (RECOMPILE);
        FETCH NEXT FROM periods INTO @pEnd, @pDays;
    END;
    CLOSE periods;
    DEALLOCATE periods;

    DECLARE @mayTruncate BIT = CASE WHEN HAS_PERMS_BY_NAME(N'dash.UserWeek', N'OBJECT', N'ALTER') = 1 THEN 1 ELSE 0 END;

    BEGIN TRANSACTION;
    /* Written whole, its columnstore is set aside and built again after, in one pass: kept up row by row, 12 million
       weeks took a minute and a half. */
    IF @mayTruncate = 1
    BEGIN
        ALTER INDEX NCCI_DashUserWeek ON dash.UserWeek DISABLE;
        TRUNCATE TABLE dash.UserWeek;
    END;
    ELSE DELETE FROM dash.UserWeek;
    INSERT INTO dash.UserWeek WITH (TABLOCK)
        (WeekEnding, UserKey, ActiveDays, Interactions, Prompts, AgentInteractions, WebGrounded, ChatInteractions, LicensedUse, PremiumUse,
         Jailbreak, PromptInjection, BlockedResource, LabeledContent, Sessions, MultiTurnSessions, HeldAtEnd)
    SELECT WeekEnding, UserKey, ActiveDays, Interactions, Prompts, AgentInteractions, WebGrounded, ChatInteractions, LicensedUse, PremiumUse,
           Jailbreak, PromptInjection, BlockedResource, LabeledContent, Sessions, MultiTurnSessions, HeldAtEnd
    FROM #week;
    IF @mayTruncate = 1
        ALTER INDEX NCCI_DashUserWeek ON dash.UserWeek REBUILD;
    COMMIT TRANSACTION;

    BEGIN TRANSACTION;
    IF @adoptFrom IS NULL AND @mayTruncate = 1 TRUNCATE TABLE dash.AdoptionDay;
    ELSE IF @adoptFrom IS NULL DELETE FROM dash.AdoptionDay;
    ELSE DELETE FROM dash.AdoptionDay WHERE DateKey >= @adoptFrom;
    INSERT INTO dash.AdoptionDay WITH (TABLOCK) (DateKey, UserKey) SELECT DateKey, UserKey FROM #adopt;
    COMMIT TRANSACTION;

    BEGIN TRANSACTION;
    IF @mayTruncate = 1 TRUNCATE TABLE dash.UserActivity; ELSE DELETE FROM dash.UserActivity;
    INSERT INTO dash.UserActivity WITH (TABLOCK) (UserKey, FirstActiveKey, LastActiveKey) SELECT UserKey, FirstActiveKey, LastActiveKey FROM #act;
    IF @mayTruncate = 1 TRUNCATE TABLE dash.UserPeriod; ELSE DELETE FROM dash.UserPeriod;
    INSERT INTO dash.UserPeriod WITH (TABLOCK)
        (EndKey, Days, UserKey, ActiveDays, PremiumDays, LastActiveKey, MostApps, Interactions, Prompts, AgentInteractions, WebGrounded, ChatInteractions,
         LicensedUse, PremiumUse, Jailbreak, PromptInjection, BlockedResource, LabeledContent, Sessions, MultiTurnSessions, HeldUse)
    SELECT EndKey, Days, UserKey, ActiveDays, PremiumDays, LastActiveKey, MostApps, Interactions, Prompts, AgentInteractions, WebGrounded, ChatInteractions,
           LicensedUse, PremiumUse, Jailbreak, PromptInjection, BlockedResource, LabeledContent, Sessions, MultiTurnSessions, HeldUse
    FROM #period;
    COMMIT TRANSACTION;

    /* The whole organization's license holders on each day, those enabled today, and those of them adopted that day:
       after dash.AdoptionDay, whole, as the license days and the enabled flags are. */
    DROP TABLE IF EXISTS #held;
    SELECT ld.DateKey, COUNT(*) AS Held, COUNT(en.UserKey) AS HeldEnabled,
           COUNT(CASE WHEN en.UserKey IS NOT NULL AND ad.UserKey IS NOT NULL THEN 1 END) AS AdoptedEnabled
    INTO #held
    FROM bi.FactLicensedDay AS ld
    LEFT JOIN bi.DimUser AS en ON en.UserKey = ld.UserKey AND en.AccountEnabled = 1
    LEFT JOIN dash.AdoptionDay AS ad ON ad.DateKey = ld.DateKey AND ad.UserKey = ld.UserKey
    GROUP BY ld.DateKey
    OPTION (RECOMPILE);
    BEGIN TRANSACTION;
    DELETE FROM dash.HeldDay;
    INSERT INTO dash.HeldDay (DateKey, Held, HeldEnabled, AdoptedEnabled) SELECT DateKey, Held, HeldEnabled, AdoptedEnabled FROM #held;
    COMMIT TRANSACTION;

    BEGIN TRANSACTION;
    IF @itemWeekFrom IS NULL AND @mayTruncate = 1
    BEGIN
        ALTER INDEX NCCI_DashUserItemWeek ON dash.UserItemWeek DISABLE;   -- as dash.UserItemDay's, written whole
        TRUNCATE TABLE dash.UserItemWeek;
    END;
    ELSE IF @itemWeekFrom IS NULL DELETE FROM dash.UserItemWeek;
    ELSE DELETE FROM dash.UserItemWeek WHERE WeekEnding >= @itemWeekFrom;
    INSERT INTO dash.UserItemWeek WITH (TABLOCK) (WeekEnding, UserKey, Kind, Item, ActiveDays, Interactions, Prompts, Resources, Web)
    SELECT WeekEnding, UserKey, Kind, Item, ActiveDays, Interactions, Prompts, Resources, Web FROM #itemweek;
    IF @itemWeekFrom IS NULL AND @mayTruncate = 1
        ALTER INDEX NCCI_DashUserItemWeek ON dash.UserItemWeek REBUILD;
    COMMIT TRANSACTION;

    BEGIN TRANSACTION;
    IF @mayTruncate = 1 TRUNCATE TABLE dash.SitePerson; ELSE DELETE FROM dash.SitePerson;
    INSERT INTO dash.SitePerson WITH (TABLOCK) (Kind, SiteKey, UserKey, LastKey, PrevKey, AsOfKey)
    SELECT Kind, SiteKey, UserKey, LastKey, PrevKey, @maxKey FROM #siteperson;
    COMMIT TRANSACTION;

    /* On one thread: loaded in parallel, the threads of this load deadlocked on each other every night at 160 million
       interactions, and the attempt cost five minutes before the load on one thread, which takes two. */
    BEGIN TRANSACTION;
    IF @mayTruncate = 1 TRUNCATE TABLE dash.UserItemPeriod; ELSE DELETE FROM dash.UserItemPeriod;
    INSERT INTO dash.UserItemPeriod WITH (TABLOCK) (EndKey, Days, Kind, Item, UserKey, ActiveDays, LastActiveKey, Interactions, Prompts, Resources, Web)
    SELECT EndKey, Days, Kind, Item, UserKey, ActiveDays, LastActiveKey, Interactions, Prompts, Resources, Web FROM #itemperiod
    OPTION (MAXDOP 1);
    COMMIT TRANSACTION;

    /* Loaded in parallel, which a columnstore takes a large load fastest by. Should SQL Server pick this load as a
       deadlock victim among its own threads, it is loaded again on one thread. */
    DECLARE @periodTry INT = 0;
    WHILE 1 = 1
    BEGIN
        BEGIN TRY
            BEGIN TRANSACTION;
            IF @mayTruncate = 1 TRUNCATE TABLE dash.CreditUsePeriod; ELSE DELETE FROM dash.CreditUsePeriod;
            IF @periodTry = 0
                INSERT INTO dash.CreditUsePeriod WITH (TABLOCK) (EndKey, Days, UserKey, AgentKey, I, A, B, BW, BA, R, M, Lic, AnyBilled)
                SELECT EndKey, Days, UserKey, AgentKey, I, A, B, BW, BA, R, M, Lic, AnyBilled FROM #creditperiod;
            ELSE
                INSERT INTO dash.CreditUsePeriod WITH (TABLOCK) (EndKey, Days, UserKey, AgentKey, I, A, B, BW, BA, R, M, Lic, AnyBilled)
                SELECT EndKey, Days, UserKey, AgentKey, I, A, B, BW, BA, R, M, Lic, AnyBilled FROM #creditperiod
                OPTION (MAXDOP 1);
            COMMIT TRANSACTION;
            BREAK;
        END TRY
        BEGIN CATCH
            IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
            IF ERROR_NUMBER() <> 1205 OR @periodTry > 0 THROW;
            SET @periodTry = 1;
            RAISERROR('  The load of dash.CreditUsePeriod was chosen as a deadlock victim: loading it again on one thread.', 0, 1) WITH NOWAIT;
        END CATCH;
    END;

    /* Further totals, each in a procedure of its own below (resources read; sessions and hours of the day): each is
       given the first day this run rewrote (NULL: every day) and the last two days the period totals end on. */
    DECLARE @prevKey INT = (SELECT MAX(DateKey) FROM dash.UserDay WHERE DateKey < @maxKey);
    IF OBJECT_ID(N'dash.usp_RefreshResourceTotals', N'P') IS NOT NULL
        EXEC dash.usp_RefreshResourceTotals @FromKey = @itemFrom, @MaxKey = @maxKey, @PrevKey = @prevKey, @MinKey = @minKey;
    IF OBJECT_ID(N'dash.usp_RefreshActivityTotals', N'P') IS NOT NULL
        EXEC dash.usp_RefreshActivityTotals @FromKey = @itemFrom, @MaxKey = @maxKey, @PrevKey = @prevKey, @MinKey = @minKey;

    DECLARE @ends NVARCHAR(40) = CONCAT(@maxKey, N',', (SELECT MAX(DateKey) FROM dash.UserDay WHERE DateKey < @maxKey));
    MERGE dash.Setting AS t
    USING (SELECT 'DashTotalsEnds' AS Name, @ends AS Value) AS s ON t.Name = s.Name
    WHEN MATCHED THEN UPDATE SET Value = s.Value, UpdatedBy = N'dash.usp_RefreshDashboard', UpdatedUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED THEN INSERT (Name, Value, Description, UpdatedBy)
        VALUES (s.Name, s.Value, N'The last days the dashboard''s period totals end on. Written by each refresh once they are built.', N'dash.usp_RefreshDashboard');

    /* ---- 4. The columnstores over what this run wrote ------------------------ */

    /* Rows written to a table with a columnstore beside it wait in a delta store, read row by row, until a million have
       gathered: the newest days, the ones the 7- to 30-day pages read. Compressing them is online and takes seconds,
       and is done before this run is recorded, so the pages built ahead after it read them compressed. It needs ALTER
       on the tables: a refresh run by an account without it leaves them for the next. */
    IF @mayTruncate = 1
    BEGIN
        ALTER INDEX NCCI_DashUserDay ON dash.UserDay REORGANIZE WITH (COMPRESS_ALL_ROW_GROUPS = ON);
        ALTER INDEX NCCI_DashResourceDay ON dash.ResourceDay REORGANIZE WITH (COMPRESS_ALL_ROW_GROUPS = ON);
        ALTER INDEX NCCI_DashUserWeek ON dash.UserWeek REORGANIZE WITH (COMPRESS_ALL_ROW_GROUPS = ON);
        ALTER INDEX NCCI_DashAdoptionDay ON dash.AdoptionDay REORGANIZE WITH (COMPRESS_ALL_ROW_GROUPS = ON);
        ALTER INDEX NCCI_DashUserItemDay ON dash.UserItemDay REORGANIZE WITH (COMPRESS_ALL_ROW_GROUPS = ON);
        ALTER INDEX NCCI_DashUserItemWeek ON dash.UserItemWeek REORGANIZE WITH (COMPRESS_ALL_ROW_GROUPS = ON);
        ALTER INDEX CCI_DashUserItemPeriod ON dash.UserItemPeriod REORGANIZE WITH (COMPRESS_ALL_ROW_GROUPS = ON);
        ALTER INDEX NCCI_DashCreditUseDay ON dash.CreditUseDay REORGANIZE WITH (COMPRESS_ALL_ROW_GROUPS = ON);
        ALTER INDEX CCI_DashCreditUsePeriod ON dash.CreditUsePeriod REORGANIZE WITH (COMPRESS_ALL_ROW_GROUPS = ON);
    END;

    INSERT INTO dash.RefreshRun (StartedUtc, EndedUtc, SinceUtc, FromDateKey, People, ClosureRows, UserDayRows, BiRunId)
    VALUES (@started, SYSUTCDATETIME(), @SinceUtc, @fromKey, @people, @closure, @dayRows, @biRun);

    SET @msg = CONCAT(N'Dashboard refreshed', CASE WHEN @fromKey IS NULL THEN N' (every day)' ELSE CONCAT(N' from ', @fromKey) END,
                      N': ', FORMAT(@people, 'N0'), N' people, ', FORMAT(@closure, 'N0'), N' org pairs, ',
                      FORMAT(@dayRows, 'N0'), N' person-days written.',
                      CASE WHEN @top IS NULL THEN N' No top of the organization: none is set, and no one person has half the directory under them, so nobody sees the whole organization by level.'
                           WHEN @detected = @top AND NOT EXISTS (SELECT 1 FROM dash.Setting WHERE Name = 'OrgTopUpn' AND NULLIF(LTRIM(RTRIM(Value)), N'') IS NOT NULL)
                                THEN CONCAT(N' Top of the organization, from the org chart: ', @top, N'.')
                           ELSE N'' END);
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
END;
GO
