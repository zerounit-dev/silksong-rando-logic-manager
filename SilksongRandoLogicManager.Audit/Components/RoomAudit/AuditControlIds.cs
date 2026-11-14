namespace Silksong_Rando_Logic_Manager.Components.RoomAudit;

internal static class AuditControlIds
{
    public static string ControlId(string key) => "audit-control-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)));
}
