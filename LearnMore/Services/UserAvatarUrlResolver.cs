using Microsoft.AspNetCore.Http;

namespace LearnMore.Services;

public static class UserAvatarUrlResolver
{
    public static string Resolve(string? uploadedAvatarPath, string? googlePictureUrl, PathString pathBase)
    {
        if (!string.IsNullOrWhiteSpace(uploadedAvatarPath))
        {
            return ResolveUploadedAvatar(uploadedAvatarPath, pathBase);
        }

        if (!string.IsNullOrWhiteSpace(googlePictureUrl) && !string.Equals(googlePictureUrl, "None", StringComparison.OrdinalIgnoreCase))
        {
            return googlePictureUrl;
        }

        return BuildPathBaseUrl(pathBase, "/images/default-avatar.png");
    }

    private static string ResolveUploadedAvatar(string uploadedAvatarPath, PathString pathBase)
    {
        if (Uri.TryCreate(uploadedAvatarPath, UriKind.Absolute, out _))
        {
            return uploadedAvatarPath;
        }

        string normalizedAvatar = uploadedAvatarPath.StartsWith('/')
            ? uploadedAvatarPath
            : "/" + uploadedAvatarPath;

        string basePath = pathBase.HasValue ? pathBase.Value ?? string.Empty : string.Empty;
        if (!string.IsNullOrWhiteSpace(basePath) && normalizedAvatar.StartsWith(basePath + "/", StringComparison.OrdinalIgnoreCase))
        {
            return normalizedAvatar;
        }

        return BuildPathBaseUrl(pathBase, normalizedAvatar);
    }

    private static string BuildPathBaseUrl(PathString pathBase, string localPath)
    {
        string basePath = pathBase.HasValue ? pathBase.Value ?? string.Empty : string.Empty;
        return basePath + localPath;
    }
}
