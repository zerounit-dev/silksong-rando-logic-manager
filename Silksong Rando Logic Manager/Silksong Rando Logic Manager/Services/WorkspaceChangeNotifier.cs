namespace Silksong_Rando_Logic_Manager.Services;

public sealed class WorkspaceChangeNotifier
{
    public event Action<WorkspaceChangeScope>? Changed;

    public void NotifyChanged(WorkspaceChangeScope scope = WorkspaceChangeScope.All) => Changed?.Invoke(scope);
}

[Flags]
public enum WorkspaceChangeScope
{
    None = 0,
    Sidebar = 1,
    SceneLayout = 2,
    All = Sidebar | SceneLayout
}
