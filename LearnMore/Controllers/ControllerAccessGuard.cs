using System.Data.SqlClient;
using Microsoft.AspNetCore.Mvc;

namespace LearnMore.Controllers;

internal static class ControllerAccessGuard
{
    public static bool IsSignedIn(ControllerBase controller)
        => !string.IsNullOrWhiteSpace(controller.HttpContext.Session.GetString("Email"));

    public static IActionResult LoginRequired(ControllerBase controller)
        => controller.Unauthorized(new { success = false, message = "請先登入" });

    public static async Task<IActionResult?> RequireManagerAsync(
        ControllerBase controller,
        IConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        string? email = controller.HttpContext.Session.GetString("Email");
        if (string.IsNullOrWhiteSpace(email))
        {
            return LoginRequired(controller);
        }

        await using var connection = new SqlConnection(configuration.GetConnectionString("DefaultConnection"));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("SELECT Manager FROM Users WHERE Email = @Email", connection);
        command.Parameters.AddWithValue("@Email", email);
        object? result = await command.ExecuteScalarAsync(cancellationToken);

        bool isManager = result != null && result != DBNull.Value && Convert.ToBoolean(result);
        return isManager ? null : controller.Forbid();
    }
}
