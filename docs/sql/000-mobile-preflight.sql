-- READ ONLY. Run on a staging copy of the production database before 001-mobile-accounts.sql.
-- No emails, tokens or user content are returned by this inspection.
SET NOCOUNT ON;
SELECT DB_NAME() AS CurrentDatabase;

DECLARE @Required TABLE(TableName sysname, ColumnName sysname);
INSERT INTO @Required VALUES
('Users','Id'),('Users','Email'),('Users','Name'),('Users','NickName'),('Users','Avatar'),('Users','EnableRoman'),
('SongGroup','GroupId'),('SongGroup','GroupUid'),('SongGroup','GroupName'),('SongGroup','UserId'),('SongGroup','CreateTime'),
('SongGroupMapping','GroupId'),('SongGroupMapping','SongUid'),
('Songs','SongID'),('Songs','SongUid'),('Songs','AddedByUserId'),('Songs','ChannelThumbnailUrl'),('Songs','YouTubeVideoUrl'),
('Comments','CommentId'),('Comments','UserEmail'),('CommentReplies','CommentId'),('CommentReplies','AdminEmail'),
('Wish','Id'),('Wish','UserId'),('WishReply','WishId'),('WishReply','UserId'),
('Feedbacks','Email'),('ErrorReports','UserEmail');
SELECT R.TableName, R.ColumnName,
       CASE WHEN C.column_id IS NULL THEN 'MISSING' ELSE 'OK' END AS Status,
       T.name AS DataType, C.max_length, C.is_nullable
FROM @Required R
LEFT JOIN sys.tables TB ON TB.name = R.TableName AND TB.schema_id = SCHEMA_ID('dbo')
LEFT JOIN sys.columns C ON C.object_id = TB.object_id AND C.name = R.ColumnName
LEFT JOIN sys.types T ON T.user_type_id = C.user_type_id
ORDER BY R.TableName, R.ColumnName;

-- Existing duplicate emails must be resolved deliberately before provider linking is enabled.
SELECT COUNT(*) AS DuplicateEmailGroups FROM (SELECT Email FROM dbo.Users GROUP BY Email HAVING COUNT(*) > 1) D;
SELECT COUNT(*) AS DuplicateMembershipGroups FROM
    (SELECT GroupId, SongUid FROM dbo.SongGroupMapping GROUP BY GroupId, SongUid HAVING COUNT(*) > 1) D;

-- Inspect all dependent tables before account deletion is enabled. Unknown dependencies must be handled.
SELECT FK.name AS ForeignKey, OBJECT_SCHEMA_NAME(FK.parent_object_id) AS ChildSchema,
       OBJECT_NAME(FK.parent_object_id) AS ChildTable, PC.name AS ChildColumn,
       OBJECT_NAME(FK.referenced_object_id) AS ParentTable, RC.name AS ParentColumn,
       FK.delete_referential_action_desc AS OnDelete
FROM sys.foreign_keys FK
JOIN sys.foreign_key_columns FC ON FC.constraint_object_id = FK.object_id
JOIN sys.columns PC ON PC.object_id = FC.parent_object_id AND PC.column_id = FC.parent_column_id
JOIN sys.columns RC ON RC.object_id = FC.referenced_object_id AND RC.column_id = FC.referenced_column_id
WHERE OBJECT_NAME(FK.referenced_object_id) IN ('Users','Songs','SongGroup','Comments','Wish')
ORDER BY ParentTable, ChildTable;

SELECT C.name AS PotentialIdentityColumn, OBJECT_NAME(C.object_id) AS TableName
FROM sys.columns C JOIN sys.tables T ON T.object_id = C.object_id
WHERE C.name LIKE '%Email%' OR C.name LIKE '%UserId%'
ORDER BY TableName, PotentialIdentityColumn;
