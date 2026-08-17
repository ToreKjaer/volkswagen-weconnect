namespace volkswagen_weconnect.Login;

/// <summary>
/// The stages of VW's IDKit browser-based login pages (identity.vwgroup.io), as used by the
/// OAuth "device authorization" flow that VW moved to after the Auth0 migration.
/// </summary>
internal enum IdKitStage
{
    Identifier,
    Authenticate,
    CodeConfirmation,
    VerificationSuccess
}

internal static class IdKitStageExtensions
{
    public static IdKitStage ParseTemplateValue(string? templateValue)
    {
        return templateValue switch
        {
            "loginIdentifier" => IdKitStage.Identifier,
            "loginAuthenticate" => IdKitStage.Authenticate,
            "codeConfirmation" => IdKitStage.CodeConfirmation,
            "verificationSuccess" => IdKitStage.VerificationSuccess,
            _ => throw new InvalidOperationException($"Unknown IDKit stage '{templateValue}'. VW may have changed its login flow again.")
        };
    }
}
