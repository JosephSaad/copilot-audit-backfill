-- Widening only (the schema never narrows): the workflow engine stores the specialist reviews a request
-- triggers (B8) as JSON in wf.Request.TriggeredReviews, which is longer than the original varchar(200).
SET NOCOUNT ON;
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('wf.Request') AND name = 'TriggeredReviews' AND max_length <> -1)
    ALTER TABLE wf.Request ALTER COLUMN TriggeredReviews nvarchar(max) NULL;
GO
