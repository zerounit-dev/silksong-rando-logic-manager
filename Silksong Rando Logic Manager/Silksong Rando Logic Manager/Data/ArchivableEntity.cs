namespace Silksong_Rando_Logic_Manager.Data;

public abstract class ArchivableEntity : AuditedEntity
{
    public bool IsArchived { get; set; }

    public DateTime? ArchivedUtc { get; set; }
}
