SET NOCOUNT ON;
CREATE TABLE #t (g INT, Name NVARCHAR(MAX), SiteUrl NVARCHAR(MAX), Act NVARCHAR(4000));
INSERT INTO #t VALUES (1,N'a',N'http://x',N'Read'),(1,N'b',N'http://y',N'Read'),(2,N'c',N'http://z',N'Write');

DECLARE @tests TABLE (id INT IDENTITY, label NVARCHAR(200), sql NVARCHAR(MAX));

INSERT INTO @tests (label, sql) VALUES
(N'A. 2 aggs, identical single-col ORDER BY',
 N'SELECT g, STRING_AGG(Name,'';'') WITHIN GROUP (ORDER BY LEFT(Name,400)) a,
             STRING_AGG(SiteUrl,'';'') WITHIN GROUP (ORDER BY LEFT(Name,400)) b
   FROM #t GROUP BY g'),
(N'B. 2 aggs, identical MULTI-col ORDER BY',
 N'SELECT g, STRING_AGG(Name,'';'') WITHIN GROUP (ORDER BY LEFT(SiteUrl,400), LEFT(Name,400)) a,
             STRING_AGG(SiteUrl,'';'') WITHIN GROUP (ORDER BY LEFT(SiteUrl,400), LEFT(Name,400)) b
   FROM #t GROUP BY g'),
(N'C. 3 aggs, identical MULTI-col ORDER BY (current code)',
 N'SELECT g, STRING_AGG(Name,'';'') WITHIN GROUP (ORDER BY LEFT(SiteUrl,400), LEFT(Name,400)) a,
             STRING_AGG(SiteUrl,'';'') WITHIN GROUP (ORDER BY LEFT(SiteUrl,400), LEFT(Name,400)) b,
             STRING_AGG(CASE WHEN Act=''Read'' THEN SiteUrl END,'';'') WITHIN GROUP (ORDER BY LEFT(SiteUrl,400), LEFT(Name,400)) c
   FROM #t GROUP BY g'),
(N'D. 3 aggs, identical SINGLE-col ORDER BY',
 N'SELECT g, STRING_AGG(Name,'';'') WITHIN GROUP (ORDER BY LEFT(SiteUrl,400)) a,
             STRING_AGG(SiteUrl,'';'') WITHIN GROUP (ORDER BY LEFT(SiteUrl,400)) b,
             STRING_AGG(CASE WHEN Act=''Read'' THEN SiteUrl END,'';'') WITHIN GROUP (ORDER BY LEFT(SiteUrl,400)) c
   FROM #t GROUP BY g'),
(N'E. 3 aggs, NO WITHIN GROUP at all',
 N'SELECT g, STRING_AGG(Name,'';'') a, STRING_AGG(SiteUrl,'';'') b,
             STRING_AGG(CASE WHEN Act=''Read'' THEN SiteUrl END,'';'') c
   FROM #t GROUP BY g'),
(N'F. 2 aggs + MAX + COUNT, identical single-col ORDER BY',
 N'SELECT g, STRING_AGG(Name,'';'') WITHIN GROUP (ORDER BY LEFT(SiteUrl,400)) a,
             STRING_AGG(SiteUrl,'';'') WITHIN GROUP (ORDER BY LEFT(SiteUrl,400)) b,
             MAX(CASE WHEN Act=''Read'' THEN 1 ELSE 0 END) x, COUNT(*) n
   FROM #t GROUP BY g'),
(N'G. single-col ORDER BY on the plain column (no LEFT)',
 N'SELECT g, STRING_AGG(Name,'';'') WITHIN GROUP (ORDER BY SiteUrl) a,
             STRING_AGG(SiteUrl,'';'') WITHIN GROUP (ORDER BY SiteUrl) b
   FROM #t GROUP BY g');

DECLARE @i INT = 1, @n INT = (SELECT MAX(id) FROM @tests), @label NVARCHAR(200), @sql NVARCHAR(MAX);
DECLARE @out TABLE (label NVARCHAR(200), result NVARCHAR(400));

WHILE @i <= @n
BEGIN
    SELECT @label = label, @sql = sql FROM @tests WHERE id = @i;
    BEGIN TRY
        -- Wrapped so the test query's own rows never reach the client; the
        -- statement is still compiled, which is what we are probing.
        DECLARE @wrapped NVARCHAR(MAX) =
            N'SELECT * INTO #sink FROM (' + @sql + N') q; DROP TABLE #sink;';
        EXEC sp_executesql @wrapped;
        INSERT INTO @out VALUES (@label, N'OK');
    END TRY
    BEGIN CATCH
        INSERT INTO @out VALUES (@label, N'FAIL: ' + LEFT(ERROR_MESSAGE(), 300));
    END CATCH;
    SET @i += 1;
END;

SELECT label, result FROM @out;
