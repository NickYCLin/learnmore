using LearnMore.Models;

namespace LearnMore.Services;

public interface IWhisperManageQueryService
{
    Task<ManageViewModel> GetManageViewModelAsync(string userEmail, CancellationToken cancellationToken = default);
}
