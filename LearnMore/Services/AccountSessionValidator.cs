using System.Data.SqlClient;

namespace LearnMore.Services;

public interface IAccountSessionValidator
{
    bool Exists(int userId, string? email = null);
}

public sealed class AccountSessionValidator(IConfiguration configuration) : IAccountSessionValidator
{
    public bool Exists(int userId, string? email = null)
    {
        if (userId <= 0) return false;
        using var connection = new SqlConnection(configuration.GetConnectionString("DefaultConnection"));
        connection.Open();
        using var command = new SqlCommand("SELECT COUNT(*) FROM dbo.Users WHERE Id = @Id AND (@Email IS NULL OR Email = @Email)", connection);
        command.Parameters.AddWithValue("@Id", userId);
        command.Parameters.AddWithValue("@Email", (object?)email ?? DBNull.Value);
        return Convert.ToInt32(command.ExecuteScalar()) == 1;
    }
}
