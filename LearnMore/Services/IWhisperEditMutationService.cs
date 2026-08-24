using LearnMore.Models;

namespace LearnMore.Services;

public interface IWhisperEditMutationService
{
    Task UpdateSongAndCollaboratorsAsync(string songUid, EditSongViewModel model, CancellationToken cancellationToken = default);
}
