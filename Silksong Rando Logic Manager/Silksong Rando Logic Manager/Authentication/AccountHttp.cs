using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using SilksongRandoLogicManager.Contracts;

namespace Silksong_Rando_Logic_Manager.Authentication;

public static class AccountHttp
{
    public static void MapAccountHttp(this WebApplication app)
    {
        app.MapGet("/api/auth/session", async (HttpContext context, LiveAccess access, AuthenticationMode mode, IAntiforgery antiforgery) =>
        {
            NoStore(context);
            var identity = await access.CurrentAsync(context.RequestAborted);
            var token = antiforgery.GetAndStoreTokens(context).RequestToken!;
            return Results.Json(new AuthenticationSession(identity is not null, identity?.Replacement ?? false, identity?.Username,
                identity?.CanManageAdmins ?? false, identity?.CanManageUsers ?? false, identity?.CanAuthor ?? false,
                identity?.CanPublishDocumentation ?? false, mode.Bypass, identity?.WorkspaceKey, token));
        });
        app.MapGet("/account/login", LoginGet).RequireRateLimiting("account-login");
        app.MapPost("/account/login", LoginPost).RequireRateLimiting("account-login");
        app.MapGet("/account/replace-password", ReplaceGet);
        app.MapPost("/account/replace-password", ReplacePost);
        app.MapPost("/account/logout", async (HttpContext context, AuthenticationMode mode) =>
        {
            NoStore(context);
            if (mode.Bypass) return Results.Redirect("/");
            await context.SignOutAsync(IdentityConstants.ApplicationScheme);
            return Results.Redirect("/account/login");
        });
        app.MapGet("/account/admin", async (HttpContext context, AccountsService accounts) => await Admin(context, accounts));
        app.MapPost("/account/admin/{action}", AdminPost);
    }

    private static async Task<IResult> LoginGet(HttpContext context, LiveAccess access, AuthenticationMode mode)
    {
        NoStore(context);
        var identity = await access.CurrentAsync();
        if (identity is not null) return Results.Redirect(identity.Replacement ? ReplacementDestination(context.Request.Query["returnUrl"]) : LocalReturn(context.Request.Query["returnUrl"]));
        if (mode.Bypass) return Results.Redirect("/");
        return Login(context, context.Request.Query["changed"] == "1" ? "Password replaced. Sign in with the new password." : null);
    }
    private static IResult Login(HttpContext context, string? message, string? returnUrl = null)
    {
        var destination = LocalReturn(returnUrl ?? context.Request.Query["returnUrl"]);
        return Page("Sign in", (message is null ? "" : Notice(message)) + Form(context, "/account/login", """
            <label>Username <input name="username" autocomplete="username" required maxlength="64"></label>
            <label>Password <input name="credential" type="password" autocomplete="current-password" required maxlength="128"></label>
            """ + Hidden("returnUrl", destination) + "<button>Sign in</button>"), signInPresentation: true);
    }
    private static async Task<IResult> LoginPost(HttpContext context, LiveAccess access, AccountsService accounts, AuthenticationMode mode)
    {
        NoStore(context);
        var values = await context.Request.ReadFormAsync();
        var identity = await access.CurrentAsync();
        if (identity is not null) return Results.Redirect(identity.Replacement ? ReplacementDestination(values["returnUrl"]) : LocalReturn(values["returnUrl"]));
        if (mode.Bypass) return Results.Redirect("/");
        var principal = await accounts.AuthenticateAsync(values["username"].ToString(), values["credential"].ToString());
        if (principal is null) { context.Response.StatusCode = 400; return Login(context, "Could not sign in. Check your username and password, or try again later.", LocalReturn(values["returnUrl"])); }
        await context.SignInAsync(IdentityConstants.ApplicationScheme, principal,
            new AuthenticationProperties { IsPersistent = true });
        return Results.Redirect(principal.HasClaim(AccessPolicies.ReplacementClaim, "true") ? ReplacementDestination(values["returnUrl"]) : LocalReturn(values["returnUrl"]));
    }

    private static async Task<IResult> ReplaceGet(HttpContext context, LiveAccess access)
    {
        NoStore(context);
        await access.RequireAsync(AccessPolicies.Replacement);
        return Replacement(context, null);
    }
    private static IResult Replacement(HttpContext context, string? message, string? returnUrl = null) => Page("Replace temporary password",
        "<p>Choose a new password of 12–128 characters, different from your temporary password. You’ll need to sign in again.</p>" + (message is null ? "" : Notice(message)) +
        Form(context, "/account/replace-password", """
            <label>New password <input name="credential" type="password" autocomplete="new-password" required minlength="12" maxlength="128"></label>
            <label>Confirm new password <input name="confirmation" type="password" autocomplete="new-password" required minlength="12" maxlength="128"></label>
            <button>Replace password</button>
            """ + Hidden("returnUrl", LocalReturn(returnUrl ?? context.Request.Query["returnUrl"]))) + Form(context, "/account/logout", "<button>Sign out</button>"), signInPresentation: true);
    private static async Task<IResult> ReplacePost(HttpContext context, AccountsService accounts)
    {
        NoStore(context);
        var values = await context.Request.ReadFormAsync();
        try { await accounts.ReplaceAsync(values["credential"].ToString(), values["confirmation"].ToString()); }
        catch (AccountCommandException error) { context.Response.StatusCode = 400; return Replacement(context, error.Message, values["returnUrl"]); }
        await context.SignOutAsync(IdentityConstants.ApplicationScheme);
        return Results.Redirect(LoginDestination(values["returnUrl"]) + "&changed=1");
    }

    private static async Task<IResult> Admin(HttpContext context, AccountsService accounts, string? message = null, CredentialDisclosure? disclosure = null)
    {
        NoStore(context);
        var actor = await context.RequestServices.GetRequiredService<LiveAccess>().RequireAsync(AccessPolicies.ManageAccounts);
        var rows = await accounts.ListAsync();
        var content = new StringBuilder();
        if (message is not null) content.Append(AdminFeedback(message, context.Response.StatusCode == 409));
        if (disclosure is not null) content.Append(TemporaryPassword(disclosure));
        content.Append("<div class=\"toolbar\"><p>").Append(rows.Count).Append(rows.Count == 1 ? " account" : " accounts")
            .Append("</p>").Append(DialogTrigger("create-account", "create account", "create account", "primary")).Append("</div>");
        var dialogs = new StringBuilder(AccountDialog(context, "create-account", "create account", "/account/admin/create",
            "<p>You’ll receive a temporary password to share privately with the user. They must change it when signing in.</p>" +
            Fields("", actor.CanManageUsers ? AccountPermissions.ReadOnly : AccountPermissions.Admin, true, actor), "create account"));
        if (rows.Count == 0) content.Append("<p>No accounts yet.</p>");
        else content.Append("<div class=\"table-scroll\" role=\"region\" aria-label=\"Accounts\" tabindex=\"0\"><table><caption class=\"visually-hidden\">Accounts</caption><thead><tr><th scope=\"col\">Username</th><th scope=\"col\">Permissions</th><th scope=\"col\">Status</th><th scope=\"col\">Password</th><th scope=\"col\">Actions</th></tr></thead><tbody>");
        foreach (var row in rows)
        {
            var identity = Hidden("id", row.Id.ToString("D")) + Hidden("concurrencyToken", row.ConcurrencyToken);
            var key = row.Id.ToString("N");
            var manageable = actor.CanManage(row.Permissions);
            content.Append("<tr><th scope=\"row\">").Append(E(row.Username)).Append("</th><td>").Append(E(row.Permissions.Summary))
                .Append("</td><td>").Append(row.Enabled ? "Enabled" : "Disabled").Append("</td><td>")
                .Append(row.ReplacementRequired ? "Change required" : "Set").Append("</td><td><div class=\"row-actions\">")
                .Append(DialogTrigger("edit-" + key, "edit", "edit " + row.Username, available: manageable, explanation: "unavailable-" + key))
                .Append(DialogTrigger("reset-" + key, "reset password", "reset password for " + row.Username, available: manageable, explanation: "unavailable-" + key))
                .Append(DialogTrigger("delete-" + key, "delete", "delete " + row.Username, "danger", manageable, "unavailable-" + key)).Append("</div>");
            if (!manageable) content.Append("<small id=\"unavailable-").Append(key).Append("\">Requires ")
                .Append(row.Permissions.ManageAdmins ? "Manage admins" : "Manage users").Append(".</small>");
            content.Append("</td></tr>");
            if (!manageable) continue; // No active forms or target tokens for inaccessible tasks.
            dialogs.Append(AccountDialog(context, "edit-" + key, "edit " + row.Username, "/account/admin/update",
                identity + Fields(row.Username, row.Permissions, row.Enabled, actor) + "<p>Changes to the username, permissions, or enabled status invalidate this user’s sign-ins at native validation (normally every 30 minutes). An unchanged save does not.</p>" + AccessWarning, "save changes"));
            var confirmation = identity + "<label>Type username to confirm <input name=\"confirmedUsername\" autocomplete=\"off\" required autofocus></label>";
            dialogs.Append(AccountDialog(context, "reset-" + key, "reset password for " + row.Username, "/account/admin/reset",
                "<p>Resetting generates a temporary password and invalidates this user’s sign-ins at native validation (normally every 30 minutes). The user must choose a new password when signing in.</p>" + confirmation, "reset password"));
            dialogs.Append(AccountDialog(context, "delete-" + key, "delete " + row.Username, "/account/admin/delete",
                "<p>This permanently deletes the account. Existing sign-ins fail at native validation (normally every 30 minutes); already-admitted work may finish. Their authored logic is kept.</p>" + AccessWarning + confirmation, "delete account permanently", danger: true));
        }
        if (rows.Count > 0) content.Append("</tbody></table></div>");
        content.Append(dialogs);
        return Page("accounts", content.ToString(), adminPresentation: true, documentTitle: "Accounts", toolsActor: actor);
    }
    private static async Task<IResult> AdminPost(HttpContext context, string action, AccountsService accounts, LiveAccess access)
    {
        NoStore(context);
        var originalActor = await access.RequireAsync(AccessPolicies.ManageAccounts);
        var values = await context.Request.ReadFormAsync();
        CredentialDisclosure? disclosure = null;
        var selfChanged = false;
        try
        {
            if (action == "create" || action == "update")
            {
                var permissions = new AccountPermissions(values["manageAdmins"] == "true", values["manageUsers"] == "true",
                    values["editLogic"] == "true", values["publishDocumentation"] == "true");
                var enabled = values["enabled"] == "true";
                if (action == "create") disclosure = await accounts.CreateAsync(values["username"].ToString(), permissions, enabled);
                else
                {
                    var id = ParseId(values["id"]);
                    var changed = await accounts.UpdateAsync(new(id, values["concurrencyToken"].ToString(), values["username"].ToString(), permissions, enabled));
                    selfChanged = changed && !originalActor.Bypass && originalActor.Account == id.ToString("D");
                }
            }
            else if (action is "reset" or "delete")
            {
                var id = ParseId(values["id"]);
                if (action == "reset") disclosure = await accounts.ResetAsync(id, values["concurrencyToken"].ToString(), values["confirmedUsername"].ToString());
                else await accounts.DeleteAsync(id, values["concurrencyToken"].ToString(), values["confirmedUsername"].ToString());
                selfChanged = !originalActor.Bypass && originalActor.Account == id.ToString("D");
            }
            else return Results.NotFound();
        }
        catch (AccountCommandException error)
        {
            var contextMessage = action switch
            {
                "create" => "Account could not be created.",
                "update" => "Account could not be saved.",
                "reset" => "Password could not be reset.",
                _ => "Account could not be deleted."
            };
            context.Response.StatusCode = 409;
            return await Admin(context, accounts, contextMessage + " " + error.Message);
        }
        // A self-change may have revoked the response's actor. Do not attempt to
        // reload protected account data or lose a successful reset disclosure.
        var success = action switch
        {
            "create" => "Account created.",
            "update" => "Account saved.",
            "reset" => "Password reset.",
            _ => "Account deleted."
        };
        if (selfChanged)
        {
            await context.SignOutAsync(IdentityConstants.ApplicationScheme);
            return Page("account changed", AdminFeedback(success) +
                (disclosure is null ? "" : TemporaryPassword(disclosure)) +
                "<p>This sign-in has ended. Sign in again, or use operator-configured bypass recovery if you no longer have access.</p><p><a href=\"/account/login\">Sign in</a></p>", adminPresentation: true, documentTitle: "Account changed");
        }
        return await Admin(context, accounts, success, disclosure);
    }
    private static Guid ParseId(string? value) => Guid.TryParse(value, out var id) ? id : throw new AccountCommandException("Reload the account list before trying again.");
    private const string AccessWarning = "<p class=\"consequence\">Changing your own account may remove your management access and ends this sign-in. Removing, disabling, or deleting the last enabled account with Manage admins may require operator-configured bypass recovery.</p>";
    private static string DialogTrigger(string id, string label, string accessibleLabel, string style = "", bool available = true, string explanation = "") =>
        "<button type=\"button\" " + (available ? "data-dialog=\"" + E(id) + "\"" : "disabled aria-describedby=\"" + E(explanation) + "\"") + " class=\"tool-action " + E(style) + "\" aria-label=\"" + E(accessibleLabel) + "\">" + E(label) + "</button>";
    private static string AccountDialog(HttpContext context, string id, string title, string action, string fields, string submit, bool danger = false) =>
        "<dialog id=\"" + E(id) + "\" aria-labelledby=\"" + E(id) + "-title\">" +
        Form(context, action, "<div class=\"dialog-body\"><h2 id=\"" + E(id) + "-title\">" + E(title) + "</h2>" + fields +
            "</div><div class=\"dialog-actions\"><button type=\"submit\" class=\"" + (danger ? "danger" : "primary") + "\">" + E(submit) +
            "</button><button type=\"button\" data-close>cancel</button></div>") + "</dialog>";
    private static string AdminFeedback(string message, bool failed = false) => "<p class=\"" + (failed ? "tool-error" : "tool-result") + "\" role=\"status\" tabindex=\"-1\" data-result>" + E(message) + "</p>";
    private static string Fields(string username, AccountPermissions permissions, bool enabled, ExecutionIdentity actor) =>
        "<div class=\"fields\"><label>Username <input name=\"username\" required autofocus minlength=\"3\" maxlength=\"64\" value=\"" + E(username) + "\"><small>3–64 ASCII letters, digits, dots, underscores or hyphens.</small></label><label>Preset <select data-preset>" +
        string.Concat(new[] { "Admin", "Contributor", "Read only", "Custom" }.Select(value => "<option value=\"" + value + "\"" + (value == permissions.Preset ? " selected" : "") + (value == "Admin" && !actor.CanManageAdmins ? " disabled" : "") + ">" + value + "</option>")) +
        "</select><small>Presets only select checkboxes. Permissions are independent.</small></label><fieldset class=\"permissions\"><legend>Permissions</legend>" +
        PermissionBox("manageAdmins", "Manage admins", permissions.ManageAdmins, !actor.CanManageAdmins) +
        PermissionBox("manageUsers", "Manage users", permissions.ManageUsers) +
        PermissionBox("editLogic", "Edit logic", permissions.EditLogic) +
        PermissionBox("publishDocumentation", "Publish documentation", permissions.PublishDocumentation) +
        "<small>Manage admins manages protected accounts; Manage users manages ordinary accounts and may delegate ordinary permissions. Neither grants editing or publishing.</small></fieldset>" +
        "<label class=\"enabled\"><input type=\"checkbox\" name=\"enabled\" value=\"true\"" + (enabled ? " checked" : "") + "> Enabled</label></div>";
    private static string PermissionBox(string name, string label, bool selected, bool disabled = false) =>
        "<label><input type=\"checkbox\" name=\"" + name + "\" value=\"true\" data-permission" + (selected ? " checked" : "") + (disabled ? " disabled" : "") + "> " + label + "</label>";
    private static string TemporaryPassword(CredentialDisclosure disclosure) =>
        "<section class=\"credential\" tabindex=\"-1\" aria-labelledby=\"credential-title\"><h2 class=\"tool-section-title\" id=\"credential-title\">temporary password for " + E(disclosure.Username) +
        "</h2><p>Copy this password now and share it privately. It is shown only once. The user must choose a new password when signing in. If you lose it, reset the password again.</p><label>Temporary password <textarea readonly autocomplete=\"off\" spellcheck=\"false\">" + E(disclosure.TemporaryCredential) + "</textarea></label></section>";
    private static string Form(HttpContext context, string action, string content) => "<form method=\"post\" action=\"" + action + "\">" +
        Hidden("__RequestVerificationToken", context.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(context).RequestToken!) +
        (action == "/account/logout" ? Hidden(WorkspaceBinding.FormField,
            (context.Items[typeof(ExecutionIdentity)] as ExecutionIdentity)?.WorkspaceKey ?? "") : "") + content + "</form>";
    private static string Hidden(string name, string value) => "<input type=\"hidden\" name=\"" + name + "\" value=\"" + E(value) + "\">";
    private static string E(string value) => WebUtility.HtmlEncode(value);
    private static string Notice(string message) => "<p role=\"status\">" + E(message) + "</p>";
    internal static IResult DenialPage(AccessDeniedException denial) => Page("Request not admitted", Notice(denial.Message), documentTitle: "Access denied");
    private const string AdminStyles = """
        :root{font-family:"Cascadia Mono","SFMono-Regular",Consolas,"Liberation Mono",monospace;font-size:14px;color:#171717;background:#f6f5f1}
        *{box-sizing:border-box}
        body{max-width:76rem;margin:2rem auto;padding:0 1rem;line-height:1.5;overflow-wrap:anywhere}
        h1{font-size:1.4rem;line-height:1.3;margin:0 0 1rem}
        h2{font-size:1.15rem;line-height:1.3;margin:1.25rem 0 .6rem}
        p{margin:.6rem 0}
        a{color:inherit;text-underline-offset:.15em}
        a:hover{background:#eeede7}
        .toolbar{display:flex;align-items:center;justify-content:space-between;gap:1rem;margin:1.25rem 0 .75rem}
        .toolbar p{margin:0}
        .table-scroll{max-width:100%;overflow-x:auto}
        table{width:100%;min-width:44rem;border-collapse:collapse;table-layout:fixed;background:#fbfaf7}
        th,td{text-align:left;vertical-align:top;padding:.65rem .6rem;border-bottom:1px solid #d5d3cb;font-weight:normal}
        thead th{background:#eeede7;font-weight:bold;border-top:1px solid #aaa9a1}
        thead th:first-child{width:23%}thead th:nth-child(2){width:15%}thead th:nth-child(3){width:12%}thead th:nth-child(4){width:17%}thead th:last-child{width:33%}
        .row-actions{display:flex;flex-wrap:wrap;gap:.35rem}
        .visually-hidden{position:absolute;width:1px;height:1px;padding:0;margin:-1px;overflow:hidden;clip-path:inset(50%);white-space:nowrap}
        small{font-weight:normal;color:#575650}
        small{display:block;font-size:.8rem;margin-top:.3rem}
        .permissions{min-width:0;margin:.6rem 0;padding:.5rem .75rem;border:1px solid #d5d3cb}
        dialog{width:32rem;max-width:calc(100% - 2rem);max-height:calc(100dvh - 2rem);padding:0;border:1px solid #aaa9a1;border-radius:0;background:#fbfaf7;color:inherit;overflow:hidden}
        dialog::backdrop{background:rgb(23 23 23 / .35)}
        dialog form{display:flex;flex-direction:column;max-height:calc(100dvh - 2rem - 2px);margin:0}
        .dialog-body{min-height:0;overflow-y:auto;padding:1rem}
        .dialog-body h2{margin:0 0 .75rem}
        .dialog-actions{display:flex;flex-wrap:wrap;gap:.5rem;flex-shrink:0;padding:.75rem 1rem;border-top:1px solid #d5d3cb;background:#eeede7}
        .consequence{border-left:3px solid #aaa9a1;padding-left:.75rem;font-size:.85rem}
        button.primary{background:#171717;color:#fbfaf7;border-color:#171717}
        button.primary:hover:not(:disabled){background:#393833;color:#fbfaf7}
        button.danger{border-color:#97645a;color:#783b32}
        button.danger:hover:not(:disabled){border-color:#783b32;background:#f2e6e1}
        .credential{border:2px solid #575650;background:#fbfaf7;padding:1rem;margin:1rem 0}
        .credential h2{margin-top:0}
        label{display:block;margin:.6rem 0}
        input,select,textarea,button{font:inherit;color:inherit;max-width:100%;padding:.25rem .4rem;border:1px solid #99978f;border-radius:0;background:#fbfaf7}
        input:not([type=hidden]):not([type=checkbox]),select{display:block;width:100%;min-width:0}
        input[type=checkbox]{margin:0 .3rem 0 0;padding:0;vertical-align:middle}
        textarea{display:block;width:100%;min-height:5rem;resize:vertical}
        button{min-height:1.6rem;background:transparent;cursor:pointer;white-space:normal;overflow-wrap:anywhere}
        button:hover:not(:disabled){border-color:#575650;background:#eeede7}
        button:disabled{border-color:#c9c8c2;background:#eeede7;color:#85837b;cursor:not-allowed;opacity:.72}
        :focus-visible,.credential:focus,[data-result]:focus{outline:2px solid #575650;outline-offset:2px}
        [role=status]{border:1px solid #aaa9a1;background:#eeede7;padding:.6rem .75rem}
        @media(max-width:40rem){body{margin:1rem auto}}
        """;
    private const string AdminScript = """
        <script>
        (() => {
            const triggers = new WeakMap();
            const syncPreset = form => {
                const preset = form.querySelector('[data-preset]');
                if (!preset) return;
                const values = [...form.querySelectorAll('[data-permission]')].map(box => box.checked);
                preset.value = values.every(Boolean) ? 'Admin' : values.every(value => !value) ? 'Read only' :
                    !values[0] && !values[1] && values[2] && !values[3] ? 'Contributor' : 'Custom';
            };
            document.querySelectorAll('[data-preset]').forEach(preset => {
                const form = preset.closest('form');
                preset.addEventListener('change', () => {
                    const value = preset.value;
                    if (value !== 'Custom') form.querySelectorAll('[data-permission]').forEach(box => {
                        if (!box.disabled) box.checked = value === 'Admin' || value === 'Contributor' && box.name === 'editLogic';
                    });
                    syncPreset(form);
                });
                form.querySelectorAll('[data-permission]').forEach(box => box.addEventListener('change', () => syncPreset(form)));
                syncPreset(form);
            });
            document.querySelectorAll('[data-dialog]').forEach(trigger => {
                trigger.addEventListener('click', () => {
                    const dialog = document.getElementById(trigger.dataset.dialog);
                    triggers.set(dialog, trigger);
                    syncPreset(dialog.querySelector('form'));
                    dialog.showModal();
                });
            });
            document.querySelectorAll('dialog').forEach(dialog => {
                dialog.querySelector('[data-close]').addEventListener('click', () => dialog.close());
                dialog.addEventListener('close', () => {
                    dialog.querySelector('form').reset();
                    syncPreset(dialog.querySelector('form'));
                    triggers.get(dialog)?.focus();
                });
            });
            (document.querySelector('.credential') ?? document.querySelector('[data-result]'))?.focus();
        })();
        </script>
        """;
    private const string SignInStyles = """
        :root{font-family:"Cascadia Mono","SFMono-Regular",Consolas,"Liberation Mono",monospace;font-size:14px;color:#171717;background:#f6f5f1}
        *{box-sizing:border-box}
        body{margin:0;padding:3rem 1rem;line-height:1.5;overflow-wrap:anywhere}
        main{width:100%;max-width:24rem;margin:0 auto}
        .app-name{font-size:.85rem;color:#575650;margin:0 0 .5rem}
        h1{font-size:1.4rem;line-height:1.3;margin:0 0 1rem}
        p{margin:.75rem 0}
        form{margin:1rem 0}
        label{display:block;margin:.75rem 0}
        input,button{font:inherit;color:inherit;border:1px solid #99978f;border-radius:0;padding:.4rem .5rem;background:#fbfaf7;max-width:100%}
        input:not([type=hidden]){display:block;width:100%;min-width:0;margin-top:.25rem}
        button{background:#171717;color:#fbfaf7;border-color:#171717;cursor:pointer;white-space:normal;overflow-wrap:anywhere}
        button:hover{background:#393833}
        form+form button{background:transparent;color:inherit;border-color:#99978f}
        form+form button:hover{background:#eeede7;border-color:#575650}
        :focus-visible{outline:2px solid #575650;outline-offset:2px}
        [role=status]{border-left:3px solid #aaa9a1;background:#eeede7;padding:.6rem .75rem}
        @media(max-width:40rem){body{padding:1.5rem 1rem}}
        """;
    private static IResult Page(string title, string content, bool adminPresentation = false, string? documentTitle = null, ExecutionIdentity? toolsActor = null, bool signInPresentation = false) => Results.Content("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>" + E((documentTitle ?? title) + " | Silksong Logic Manager") +
        "</title><style>" + (signInPresentation ? SignInStyles : adminPresentation ? AdminStyles : "body{font:16px system-ui;max-width:54rem;margin:2rem auto;padding:0 1rem}label{display:block;margin:.6rem 0}input,select,textarea,button{font:inherit;padding:.35rem}section{border:1px solid #888;padding:1rem;margin:1rem 0}form{margin:1rem 0}textarea{display:block;width:95%}") + "</style>" +
        (toolsActor is null ? "" : "<link rel=\"stylesheet\" href=\"/lib/fontawesome/css/all.min.css\"><link rel=\"stylesheet\" href=\"/tools-header.css\">") +
        (adminPresentation ? toolsActor is not null ? "<link rel=\"stylesheet\" href=\"/tools-body.css\">" : "<style>" + ToolsBodyStyles() + "</style>" : "") + "</head><body" +
        (adminPresentation ? " class=\"accounts-tools\"" : "") + ">" +
        (toolsActor is null ? "" : Components.Navigation.ToolsNavigation.Html(toolsActor, "/account/admin")) +
        (adminPresentation ? "<main class=\"tools-body\"><h1 class=\"tool-title\">" : signInPresentation ? "<main><p class=\"app-name\">Silksong Logic Manager</p><h1>" : "<main><h1>") + E(signInPresentation ? title.ToLowerInvariant() : title) + "</h1>" + content + "</main>" + (adminPresentation ? AdminScript : "") + "</body></html>", "text/html; charset=utf-8");
    private static string ToolsBodyStyles()
    {
        // A successful self-change may already have revoked access to protected static assets.
        // Embed the one shared stylesheet in that response; never relax resource admission.
        using var reader = new StreamReader(typeof(AccountHttp).Assembly.GetManifestResourceStream("ToolsBodyStyles")!);
        return reader.ReadToEnd();
    }
    internal static void NoStore(HttpContext context) { context.Response.Headers.CacheControl = "no-store"; context.Response.Headers.Pragma = "no-cache"; }
    public static string LoginDestination(string? value) => "/account/login?returnUrl=" + Uri.EscapeDataString(LocalReturn(value));
    private static string ReplacementDestination(string? value) => "/account/replace-password?returnUrl=" + Uri.EscapeDataString(LocalReturn(value));
    public static string LocalReturn(string? value)
    {
        var candidate = value ?? "/";
        try
        {
            for (var i = 0; i < 8; i++)
            {
                var decoded = Uri.UnescapeDataString(candidate);
                if (decoded == candidate) break;
                candidate = decoded;
                if (i == 7) return "/";
            }
            if (!candidate.StartsWith('/') || candidate.StartsWith("//", StringComparison.Ordinal) || candidate.Contains('\\') || candidate.Any(char.IsControl)) return "/";
            var path = candidate.Split(new[] { '?', '#' }, 2)[0];
            if (path is "/" or "/requirements") return path;
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts is ["requirements", var kind] && (kind is "predicates" or "crests" or "methods" or "items" or "countables" or "souls") && path == "/requirements/" + kind)
                return "/requirements/" + kind;
            if ((parts is ["rooms", _] || parts is ["rooms", _, "audit"]) && Guid.TryParseExact(parts[1], "D", out var id))
            {
                var target = "/rooms/" + id.ToString("D") + (parts.Length == 3 ? "/audit" : "");
                return string.Equals(path, target, StringComparison.OrdinalIgnoreCase) ? target : "/";
            }
            return "/";
        }
        catch (UriFormatException) { return "/"; }
    }
}
