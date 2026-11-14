using System.Net;
using System.Text;
using Silksong_Rando_Logic_Manager.Authentication;

namespace Silksong_Rando_Logic_Manager.Components.Navigation;

// Presentation only: endpoint/service admission remains the authority.
internal static class ToolsNavigation
{
    internal enum Capability { Normal, Author, Accounts, Publishing }
    internal sealed record Entry(string Label, string Path, string Icon, Capability Access = Capability.Normal);
    internal sealed record Link(Entry Entry, string Href, bool Enabled, bool Current);
    internal static readonly Entry Back = new("back to main app", "/", "fa-solid fa-arrow-left");
    private static readonly Entry[] Entries = [
        new("room exchange", "/tools/room-exchange", "fa-solid fa-right-left"),
        new("map manifest", "/tools/map-manifest", "fa-solid fa-file-arrow-up"),
        new("room images", "/tools/room-images", "fa-regular fa-image", Capability.Author),
        new("graph export", "/tools/graph-export", "fa-solid fa-diagram-project"),
        new("documentation publishing", "/tools/publishing", "fa-solid fa-upload", Capability.Publishing),
        new("accounts", "/account/admin", "fa-solid fa-users-gear", Capability.Accounts),
#if DEBUG
        new("diagnostics", "/tools/diagnostics", "fa-solid fa-bug"),
#endif
    ];

    internal static IEnumerable<Link> For(ExecutionIdentity actor, string path)
    {
        if (actor.Replacement) yield break;
        if (path == "/tools/room-import") path = "/tools/room-exchange";
        foreach (var entry in Entries)
        {
            if (entry.Access == Capability.Accounts && !actor.CanManageAccounts) continue;
            if (entry.Access == Capability.Publishing && !actor.CanPublishDocumentation) continue;
            yield return new(entry, entry.Path,
                entry.Access != Capability.Author || actor.CanAuthor, path == entry.Path);
        }
    }

    internal static string Html(ExecutionIdentity actor, string path)
    {
        static string E(string value) => WebUtility.HtmlEncode(value);
        var html = new StringBuilder("<header class=\"tools-header\"><a class=\"tools-back\" href=\"")
            .Append(E(Back.Path)).Append("\" data-enhance-nav=\"false\"><i aria-hidden=\"true\" class=\"")
            .Append(E(Back.Icon)).Append("\"></i><span>").Append(E(Back.Label)).Append("</span></a><nav aria-label=\"Tools\">");
        foreach (var link in For(actor, path))
        {
            html.Append(link.Enabled ? "<a href=\"" + E(link.Href) + "\" data-enhance-nav=\"false\"" : "<span aria-disabled=\"true\"")
                .Append(link.Current ? " aria-current=\"page\"" : "")
                .Append("><i aria-hidden=\"true\" class=\"").Append(E(link.Entry.Icon)).Append("\"></i><span>")
                .Append(E(link.Entry.Label)).Append(link.Enabled ? "</span></a>" : "</span></span>");
        }
        return html.Append("</nav></header>").ToString();
    }
}
