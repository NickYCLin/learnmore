using System.Data;
using System.Data.SqlClient;
using System.Globalization;

namespace LearnMore.Services;

public sealed record ErrorReportItem(string ReportId, string? SongUid, string? Title,
    string? VideoSource, string? LyricId, string? Description, string? Status, DateTime? ReportDate);

public sealed record ErrorReportPage(int Page, int PageSize, long TotalCount, IReadOnlyList<ErrorReportItem> Items, string ReportIdColumn = "")
{
    public long TotalPages => (TotalCount + PageSize - 1) / PageSize;
}

public interface IErrorReportReader
{
    Task<ErrorReportPage> ReadAsync(int page, int pageSize, CancellationToken cancellationToken);
}

public sealed class ErrorReportSchemaException : Exception
{
    public ErrorReportSchemaException() : base("ErrorReports 缺少已知欄位或可驗證的單欄唯一鍵，無法安全列出完整分頁。") { }
}

public sealed class ErrorReportReader(IConfiguration configuration) : IErrorReportReader
{
    public static bool IsValidPage(int page, int pageSize) => page >= 1 && pageSize is >= 1 and <= 100;
    public static string QuoteIdentifier(string identifier) => "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";

    internal const string SchemaSql = """
            SELECT TOP (1) columns.name
            FROM sys.indexes indexes
            JOIN sys.index_columns keys ON keys.object_id = indexes.object_id AND keys.index_id = indexes.index_id AND keys.key_ordinal = 1
            JOIN sys.columns columns ON columns.object_id = keys.object_id AND columns.column_id = keys.column_id
            WHERE indexes.object_id = OBJECT_ID(N'dbo.ErrorReports') AND indexes.is_unique = 1
              AND indexes.has_filter = 0 AND indexes.is_disabled = 0 AND indexes.is_hypothetical = 0 AND columns.is_nullable = 0
              AND TYPE_NAME(columns.system_type_id) IN ('int','bigint','smallint','tinyint','uniqueidentifier','nvarchar','varchar','nchar','char','decimal','numeric')
              AND NOT EXISTS (SELECT 1 FROM sys.index_columns extra WHERE extra.object_id = indexes.object_id AND extra.index_id = indexes.index_id AND extra.key_ordinal > 1)
              AND (SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.ErrorReports') AND name IN
                ('UserEmail','ErrorDescription','ErrorSongUid','ErrorLyricID','ReportDate','Status')) = 6
            ORDER BY indexes.is_primary_key DESC, indexes.index_id;
            """;

    public async Task<ErrorReportPage> ReadAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        if (!IsValidPage(page, pageSize)) throw new ArgumentOutOfRangeException(nameof(page));
        await using var connection = new SqlConnection(configuration.GetConnectionString("DefaultConnection"));
        await connection.OpenAsync(cancellationToken);
        await using var schemaCommand = new SqlCommand(SchemaSql, connection);
        var key = await schemaCommand.ExecuteScalarAsync(cancellationToken) as string;
        if (string.IsNullOrEmpty(key)) throw new ErrorReportSchemaException();
        var quotedKey = QuoteIdentifier(key);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var query = $"""
            SELECT COUNT_BIG(*) FROM dbo.ErrorReports;
            SELECT reports.{quotedKey}, reports.ErrorSongUid,
                   (SELECT songs.Title FROM dbo.Songs songs WHERE songs.SongUid = reports.ErrorSongUid),
                   (SELECT songs.YouTubeVideoUrl FROM dbo.Songs songs WHERE songs.SongUid = reports.ErrorSongUid),
                   reports.ErrorLyricID, reports.ErrorDescription, reports.Status, reports.ReportDate
            FROM dbo.ErrorReports reports
            ORDER BY reports.ReportDate DESC, reports.{quotedKey} DESC
            OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;
            """;
        await using var command = new SqlCommand(query, connection, transaction);
        command.Parameters.Add("@Offset", SqlDbType.BigInt).Value = ((long)page - 1) * pageSize;
        command.Parameters.Add("@PageSize", SqlDbType.Int).Value = pageSize;
        long totalCount;
        var items = new List<ErrorReportItem>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            await reader.ReadAsync(cancellationToken);
            totalCount = reader.GetInt64(0);
            await reader.NextResultAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                string? Text(int ordinal) => reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
                items.Add(new ErrorReportItem(Text(0)!, Text(1), Text(2), Text(3), Text(4), Text(5), Text(6),
                    reader.IsDBNull(7) ? null : reader.GetDateTime(7)));
            }
        }
        await transaction.CommitAsync(cancellationToken);
        return new ErrorReportPage(page, pageSize, totalCount, items, key);
    }
}
