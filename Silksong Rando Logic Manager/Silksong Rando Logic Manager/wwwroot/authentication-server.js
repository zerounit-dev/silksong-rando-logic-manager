// The retained server host starts only after acquiring its request token.
// SignalR supplies it on HTTP negotiation/send requests; websocket admission
// additionally validates the browser Origin on the server.
(async () => {
    const response = await fetch('/api/auth/session', { credentials: 'same-origin', cache: 'no-store' });
    if (!response.ok) throw new Error('Unable to acquire sign-in state.');
    const session = await response.json();
    if (!session.signedIn || session.replacementRequired) {
        location.assign('/account/login?returnUrl=' + encodeURIComponent(location.pathname));
        return;
    }
    await Blazor.start({ circuit: { configureSignalR: builder => builder.withUrl('/_blazor', {
        headers: { 'X-CSRF-TOKEN': session.antiforgeryToken }
    }) } });
})().catch(() => {
    const failure = document.createElement('p');
    failure.setAttribute('role', 'alert');
    failure.textContent = 'The server connection could not be initialized. Local work has not been replayed. Reopen the application or sign in again.';
    document.body.append(failure);
});
