using LearnMore.Models;

namespace LearnMore.Services;

public interface IWhisperManageQueryService
{
    Task<ManageViewModel> GetManageViewModelAsync(string userEmail, CancellationToken cancellationToken = default);
    Task<ManageViewModel> GetPagedManageViewModelAsync(string userEmail, int producerPage, int collaborationPage,
        CancellationToken cancellationToken = default);
}
