namespace LearnMore.Services.Mobile;

public static class MobileAccountMutationGate
{
    public static void RequireAvailable() => throw new MobileAuthException(
        "帳號連結與刪除尚待安全驗證，目前無法使用。", 503);
}

public static class MobileIdentityPolicy
{
    public static void RequireNewAccountEmail(bool emailExists)
    {
        if (emailExists)
            throw new MobileAuthException("此電子郵件已有帳號，不能僅憑電子郵件自動連結登入方式。", 409);
    }
}

public static class MobileMutationProofPolicy
{
    public static void RequireFresh(ProviderIdentity proof, DateTimeOffset now)
    {
        if (proof.Provider is not ("google" or "apple") || string.IsNullOrWhiteSpace(proof.Subject) ||
            proof.AuthenticatedAt is not { } authenticated || authenticated < now.AddMinutes(-5) ||
            authenticated > now.AddSeconds(30))
            throw new MobileAuthException("請重新驗證身分，不能使用過期或缺少驗證時間的憑證。", 403);
    }

    public static void RequireOwner(int userId, int? proofOwner)
    {
        if (userId <= 0 || proofOwner != userId)
            throw new MobileAuthException("請使用此帳號已連結的登入方式重新驗證。", 403);
    }
}
