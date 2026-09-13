namespace Silksong_Rando_Logic_Manager.Data;

public abstract class AuditedEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }
}
