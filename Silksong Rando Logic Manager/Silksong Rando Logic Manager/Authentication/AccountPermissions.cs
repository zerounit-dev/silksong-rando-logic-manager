namespace Silksong_Rando_Logic_Manager.Authentication;

// Presets are display conveniences only. Commands carry the exact four values.
public sealed record AccountPermissions(bool ManageAdmins, bool ManageUsers, bool EditLogic, bool PublishDocumentation)
{
    public static AccountPermissions Admin { get; } = new(true, true, true, true);
    public static AccountPermissions Contributor { get; } = new(false, false, true, false);
    public static AccountPermissions ReadOnly { get; } = new(false, false, false, false);
    public string Preset => this == Admin ? "Admin" : this == Contributor ? "Contributor" : this == ReadOnly ? "Read only" : "Custom";
    public string Summary => Preset != "Custom" ? Preset : "Custom: " + string.Join(", ",
        new[] { ManageAdmins ? "Manage admins" : null, ManageUsers ? "Manage users" : null,
            EditLogic ? "Edit logic" : null, PublishDocumentation ? "Publish documentation" : null }.OfType<string>());

    internal static AccountPermissions From(Account account) => new(account.ManageAdmins, account.ManageUsers, account.EditLogic, account.PublishDocumentation);
    internal void Apply(Account account)
    {
        account.ManageAdmins = ManageAdmins;
        account.ManageUsers = ManageUsers;
        account.EditLogic = EditLogic;
        account.PublishDocumentation = PublishDocumentation;
    }
}
