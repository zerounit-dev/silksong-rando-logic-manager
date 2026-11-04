namespace Silksong_Rando_Logic_Manager.Services;

public enum AuditRequirementsMode { Basic, Advanced }

// Page-operation identity only. Never part of retained scenario serialization.
public readonly record struct AuditRequirementsContext(AuditRequirementsMode Mode, long Revision)
{
    public bool Advanced => Mode == AuditRequirementsMode.Advanced;
}
