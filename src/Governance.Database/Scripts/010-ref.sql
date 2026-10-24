-- Reference data: settings, business calendar, caveat register, editable wording.
SET NOCOUNT ON;

IF OBJECT_ID('ref.Setting') IS NULL
CREATE TABLE ref.Setting
(
    SettingKey    varchar(100)   NOT NULL CONSTRAINT PK_Setting PRIMARY KEY,
    SettingValue  nvarchar(2000) NOT NULL,
    Description   nvarchar(400)  NULL,
    UpdatedUtc    datetime2      NOT NULL CONSTRAINT DF_Setting_Updated DEFAULT SYSUTCDATETIME(),
    UpdatedByPersonId int        NULL,
    RowVer        rowversion
);

IF OBJECT_ID('ref.Holiday') IS NULL
CREATE TABLE ref.Holiday
(
    HolidayDate date          NOT NULL CONSTRAINT PK_Holiday PRIMARY KEY,
    Name        nvarchar(100) NOT NULL
);

IF OBJECT_ID('ref.Caveat') IS NULL
CREATE TABLE ref.Caveat
(
    CaveatCode  varchar(10)    NOT NULL CONSTRAINT PK_Caveat PRIMARY KEY,   -- C01 to C22
    Title       nvarchar(200)  NOT NULL,
    Control     nvarchar(1000) NOT NULL,
    Guidance    nvarchar(2000) NULL
);

-- Business wording the Platform Owner edits between releases (headers, guidance text, field help).
IF OBJECT_ID('ref.TextBlock') IS NULL
CREATE TABLE ref.TextBlock
(
    TextKey     varchar(120)   NOT NULL,
    VersionNo   int            NOT NULL,
    Body        nvarchar(4000) NOT NULL,
    Status      varchar(20)    NOT NULL CONSTRAINT CK_TextBlock_Status CHECK (Status IN ('DRAFT','ACTIVE','RETIRED')),
    ApprovedByPersonId int     NULL,
    UpdatedUtc  datetime2      NOT NULL CONSTRAINT DF_TextBlock_Updated DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_TextBlock PRIMARY KEY (TextKey, VersionNo)
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_TextBlock_OneActive')
    CREATE UNIQUE INDEX UX_TextBlock_OneActive ON ref.TextBlock (TextKey) WHERE Status = 'ACTIVE';
GO
