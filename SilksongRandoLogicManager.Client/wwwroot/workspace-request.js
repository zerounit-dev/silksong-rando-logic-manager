// Application-wide download admission. One compiled caller supplies identity;
// imports pin this exact module, so feature modules cannot negotiate a version.
let runtime = null, applicationVersion = null, retired = false;
export function initialize(callback, version) {
    if (runtime || version?.length !== 25 || !/^app-[0-9]{21}$/.test(version)) throw new Error('Invalid runtime control registration.');
    runtime = callback; applicationVersion = version;
}
export function retire() { retired = true; }
export function requireActive() { if (retired || !runtime) throw new Error('The application runtime is retired.'); }
export function applicationHeaders(workspace) {
    requireActive();
    if (!workspace) throw new Error('Missing application workspace.');
    return { 'X-Logic-Workspace': workspace, 'X-Logic-App-Version': applicationVersion };
}
export async function checkApplicationResponse(response) {
    requireActive();
    if (response.status === 401 || response.status === 403) return;
    const mediaType = response.headers.get('Content-Type')?.split(';', 1)[0].trim().toLowerCase();
    if (mediaType === 'text/html') throw new Error('An application response was expected.');
    const version = response.headers.get('X-Logic-App-Version');
    if (version?.length !== 25 || !/^app-[0-9]{21}$/.test(version)) throw new Error('The application response identity could not be verified.');
    let mismatch = version !== applicationVersion;
    if (!mismatch && response.status === 409 && mediaType === 'application/json') {
        const body = await response.clone().json();
        if (body?.code === 'application_version_mismatch') {
            if (body.serverVersion !== version) throw new Error('The update response could not be verified.');
            mismatch = true;
        }
    }
    if (mismatch) {
        retired = true;
        await runtime.invokeMethodAsync('ApplicationVersionRejected', version);
        throw new Error('Client update required.');
    }
    requireActive();
}
export async function accessDenial(response) {
    if (response.status !== 401 && response.status !== 403) return null;
    let denial = null;
    try {
        const body = await response.json();
        if (typeof body?.code === "string" && typeof body?.message === "string") denial = body;
    } catch { /* Non-JSON access errors are still not downloadable bytes. */ }
    // An unparseable proxy/error response must neither become a download nor
    // fabricate confirmed credential loss. Existing feature error handling owns it.
    if (!denial) throw new Error("The access response could not be verified. No operation was automatically repeated.");
    return denial;
}
