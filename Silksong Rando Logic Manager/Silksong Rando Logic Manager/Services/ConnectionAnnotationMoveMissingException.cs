namespace Silksong_Rando_Logic_Manager.Services;

/// <summary>Expected stale-route admission failure for a connection scene move.</summary>
public sealed class ConnectionAnnotationMoveMissingException : InvalidOperationException
{
    public ConnectionAnnotationMoveMissingException() : base("This connection is no longer in the current room.") { }
}
