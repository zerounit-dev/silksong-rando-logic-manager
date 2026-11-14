const preferenceKey = "silksong-rando-logic.v3-sidebar-state";
let initiator = null, errorFocus = null;
let roomQueueWarning = false;
let updateDiscard = false, runtimeRetired = false;
export function discardForClientUpdate() {
    updateDiscard = true;
    setRoomQueueWarning(false);
}
export function retireRuntime() {
    runtimeRetired = true;
    for (const root of document.querySelectorAll('.v3-shell, .v3-audit-layout')) root.inert = true;
    const connectionDialog = document.getElementById('archipelago-modal');
    if (connectionDialog) { connectionDialog.hidden = true; connectionDialog.inert = true; }
    for (const dialog of document.querySelectorAll('dialog[open]')) dialog.close();
}
const warnRoomQueueLoss = event => {
    if (!roomQueueWarning) return;
    event.preventDefault();
    event.returnValue = "";
};
export function setRoomQueueWarning(unfinished) {
    if (runtimeRetired && !updateDiscard) return;
    const enabled = !updateDiscard && unfinished === true;
    if (enabled === roomQueueWarning) return;
    roomQueueWarning = enabled;
    if (enabled) window.addEventListener("beforeunload", warnRoomQueueLoss);
    else window.removeEventListener("beforeunload", warnRoomQueueLoss);
}
export function closeRoomDepartureDialog(dialog) {
    if (dialog instanceof HTMLDialogElement && dialog.open) dialog.close();
}
export function dispatchRoomDocument(target, top) {
    (top ? window.top : window).location.assign(target);
}
export function submitLogout(antiforgery, workspace) {
    const form = document.createElement("form");
    form.method = "post";
    form.action = "/account/logout";
    for (const [name, value] of [["__RequestVerificationToken", antiforgery], ["__LogicWorkspace", workspace]]) {
        const input = document.createElement("input");
        input.type = "hidden"; input.name = name; input.value = value;
        form.append(input);
    }
    document.body.append(form);
    form.submit();
}
// One-shot paint boundary for genuine initial preparation stages. No listener,
// polling timer, fabricated percentage or application refresh obligation.
export function paintInitialProgress() {
    return new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
}
export function loadPreferences() {
    try {
        const value = JSON.parse(sessionStorage.getItem(preferenceKey));
        if (!value || typeof value.showArchivedRooms !== "boolean" || !Array.isArray(value.expandedGroupIds)
            || value.expandedGroupIds.some(x => typeof x !== "string" || !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(x))) return null;
        return value;
    } catch { return null; }
}
export function savePreferences(value) { try { sessionStorage.setItem(preferenceKey, JSON.stringify(value)); } catch { /* Safe non-retention; current state remains usable. */ } }
export function rememberInitiator() { initiator = document.activeElement; }
export function showDialog(dialog, errorDialog) {
    if (runtimeRetired || errorDialog.open || !dialog.isConnected) return false;
    if (!dialog.open) { if (!initiator) rememberInitiator(); dialog.showModal(); }
    return true;
}
export function select(input, errorDialog) {
    if (!runtimeRetired && !errorDialog.open && eligibleFocus(input) && input.closest("dialog:modal")) { input.focus(); input.select(); }
}
export function freezeDialogs(value, ...dialogs) {
    for (const dialog of dialogs) {
        dialog.dataset.pending = value ? "true" : "false";
        const fields = dialog.querySelector("fieldset");
        if (fields) fields.disabled = value;
    }
}
export function replaceDialog(previous, next, errorDialog) {
    if (runtimeRetired || errorDialog.open || !next.isConnected) return false;
    previous.close();
    return showDialog(next, errorDialog);
}
function visible(element) {
    return element.isConnected && !element.closest("[hidden], [inert], dialog:not([open])")
        && element.getClientRects().length > 0 && getComputedStyle(element).visibility === "visible";
}
function eligibleFocus(element) {
    return element instanceof HTMLElement && element !== document.body && visible(element)
        && !element.matches(":disabled, input[type=hidden]") && element.tabIndex >= 0;
}
function restoreFocus(preferred) {
    const dialogs = [...document.querySelectorAll("dialog[open]")].filter(visible);
    const roomDialog = [...document.querySelectorAll('.room-editor-v2 .confirmation-modal[role="dialog"][aria-modal="true"]')].filter(visible).at(-1);
    const managerGuide = [...document.querySelectorAll('#rc-syntax-guide.confirmation-modal[role="dialog"][aria-modal="true"]')].filter(visible).at(-1);
    const active = dialogs.find(dialog => dialog.matches(".v3-error:modal"))
        ?? dialogs.filter(dialog => dialog.matches(":modal")).at(-1) ?? dialogs.at(-1) ?? roomDialog ?? managerGuide;
    // A connected preferred target can still be inert outside a native modal.
    // Never put the title in the same selector list as correcting-modal controls:
    // querySelector chooses document order, not selector priority.
    const valid = (eligibleFocus(preferred) || roomDialog && preferred === roomDialog || managerGuide && preferred === managerGuide) && (!active || active.contains(preferred));
    const target = valid ? preferred : active
        ? [...active.querySelectorAll("input, button, textarea, select, a[href], [tabindex]")].find(eligibleFocus)
        : [...document.querySelectorAll(".nav-title, .room-audit > header a.audit-action")].find(eligibleFocus);
    target?.focus();
}
export function closeDialogs(...dialogs) {
    for (const dialog of dialogs) dialog.close();
}
export function restoreInitiator() {
    if (runtimeRetired) return;
    restoreFocus(initiator);
    initiator = null;
}
export function showError(dialog) {
    if (runtimeRetired || dialog.open || !dialog.isConnected) return;
    errorFocus = document.activeElement;
    dialog.showModal();
    dialog.querySelector('[data-error-close]')?.focus();
}
export async function copyDiagnosticDetails(text) {
    try {
        if (typeof text !== 'string' || text.length > 8192 || !navigator.clipboard?.writeText) return false;
        await navigator.clipboard.writeText(text);
        return true;
    } catch { return false; }
}
export function closeError(dialog, deferFocus = false) {
    dialog.close();
    if (deferFocus) return;
    restoreErrorFocus();
}
export function restoreErrorFocus() {
    if (runtimeRetired) return false;
    // A resumed older after-render callback must not consume the saved initiator
    // before the standalone audit's unblocked render has removed inert.
    if (document.querySelector('.v3-error[open], .room-audit[inert]')) return false;
    restoreFocus(errorFocus);
    errorFocus = null;
    return true;
}
export function mount(callback) {
    let disposed = false;
    // Native dialog contains keyboard focus. Prevent Escape/backdrop dismissal
    // during mutation independently of delayed component reconciliation.
    const cancel = event => { if (event.target instanceof HTMLDialogElement && event.target.dataset.pending === "true") event.preventDefault(); };
    document.addEventListener("cancel", cancel, true);
    return { dispose() { if (disposed) return; disposed = true; document.removeEventListener("cancel", cancel, true); } };
}
