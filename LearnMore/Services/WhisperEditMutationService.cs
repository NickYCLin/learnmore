using System.Data.SqlClient;
using LearnMore.Models;

namespace LearnMore.Services;

public class WhisperEditMutationService : IWhisperEditMutationService
{
    private readonly IConfiguration _configuration;

    public WhisperEditMutationService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public async Task UpdateSongAndCollaboratorsAsync(string songUid, EditSongViewModel model, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(songUid))
        {
            throw new ArgumentException("Invalid songUid.", nameof(songUid));
        }

        if (model.Song == null)
        {
            throw new ArgumentException("Invalid song data.", nameof(model));
        }

        var collaboratorEmails = (model.Collaborators ?? new List<string>())
            .SelectMany(email => email?.Split(',') ?? Array.Empty<string>())
            .Select(email => email.Trim())
            .Where(email => !string.IsNullOrWhiteSpace(email))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        await using var connection = new SqlConnection(_configuration.GetConnectionString("DefaultConnection"));
        await connection.OpenAsync(cancellationToken);
        await EnsurePerformerColumnExistsAsync(connection, cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            const string updateSongQuery = @"
UPDATE [Songs]
SET [Title] = @Title, [Artist] = @Artist, [Performer] = @Performer, [Translator] = @Translator
WHERE [SongUid] = @SongUid";

            await using (var command = new SqlCommand(updateSongQuery, connection, transaction))
            {
                command.Parameters.AddWithValue("@Title", model.Song.Title);
                command.Parameters.AddWithValue("@Artist", model.Song.Artist);
                var performer = ResolvePerformer(model.Song.Performer, null, model.Song.Artist);
                command.Parameters.AddWithValue("@Performer", string.IsNullOrWhiteSpace(performer) ? (object)DBNull.Value : performer);
                command.Parameters.AddWithValue("@Translator", string.IsNullOrWhiteSpace(model.Song.Translator) ? (object)DBNull.Value : model.Song.Translator);
                command.Parameters.AddWithValue("@SongUid", songUid);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            var usersCollab = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            const string getUsersQuery = "SELECT Email, Collaboration FROM [Users] WITH (UPDLOCK, ROWLOCK)";
            await using (var command = new SqlCommand(getUsersQuery, connection, transaction))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    string email = reader["Email"].ToString() ?? string.Empty;
                    string collabData = reader["Collaboration"] == DBNull.Value ? string.Empty : reader["Collaboration"].ToString() ?? string.Empty;
                    usersCollab[email] = collabData
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .ToList();
                }
            }

            const string updateCollabQuery = @"
UPDATE [Users]
SET [Collaboration] = @Collab
WHERE [Email] = @Email";

            foreach (var (email, existingCollabs) in usersCollab)
            {
                if (collaboratorEmails.Contains(email, StringComparer.OrdinalIgnoreCase))
                {
                    if (!existingCollabs.Contains(songUid, StringComparer.Ordinal))
                    {
                        existingCollabs.Add(songUid);
                    }
                }
                else
                {
                    existingCollabs.RemoveAll(uid => string.Equals(uid, songUid, StringComparison.Ordinal));
                }

                string updatedCollab = existingCollabs.Count > 0 ? string.Join(",", existingCollabs) : string.Empty;
                await using var updateCommand = new SqlCommand(updateCollabQuery, connection, transaction);
                updateCommand.Parameters.AddWithValue("@Collab", string.IsNullOrWhiteSpace(updatedCollab) ? (object)DBNull.Value : updatedCollab);
                updateCommand.Parameters.AddWithValue("@Email", email);
                await updateCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static async Task EnsurePerformerColumnExistsAsync(SqlConnection connection, CancellationToken cancellationToken = default)
    {
        const string sql = @"
IF COL_LENGTH('dbo.Songs', 'Performer') IS NULL
BEGIN
    ALTER TABLE [language].[dbo].[Songs] ADD [Performer] NVARCHAR(255) NULL;
END";
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string? ResolvePerformer(string? performer, string? cover, string? artist)
    {
        if (!string.IsNullOrWhiteSpace(performer))
        {
            return performer;
        }

        if (!string.IsNullOrWhiteSpace(cover))
        {
            return cover;
        }

        return string.IsNullOrWhiteSpace(artist) ? null : artist;
    }
}
