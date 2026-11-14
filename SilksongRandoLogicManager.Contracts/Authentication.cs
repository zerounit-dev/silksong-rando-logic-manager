namespace SilksongRandoLogicManager.Contracts;

public sealed record AuthenticationSession(
    bool SignedIn, bool ReplacementRequired, string? Username,
    bool ManageAdmins, bool ManageUsers, bool EditLogic, bool PublishDocumentation,
    bool Bypass, string? WorkspaceKey, string AntiforgeryToken);

public sealed record AccessDenial(string Code, string Message);
