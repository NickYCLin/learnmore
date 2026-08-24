using LearnMore.Models;

namespace LearnMore.Services;

public interface IWhisperEditQueryService
{
    Task<EditSongViewModel?> GetEditSongViewModelAsync(string userEmail, string songUid, CancellationToken cancellationToken = default);
}
