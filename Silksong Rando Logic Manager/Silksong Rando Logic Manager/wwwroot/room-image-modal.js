// One explicit owner for the new Room images surface only. No observer or
// ownership of other modal hosts; the diagnostic overlay always takes precedence.
export function create(dialog) {
    let active = true;
    const owns = () => active && dialog instanceof HTMLElement && dialog.isConnected;
    const diagnosticOpen = () => !!document.querySelector('.diagnostic-backdrop');
    const controls = () => [...dialog.querySelectorAll('button,input,select,textarea,a[href],[tabindex]')]
        .filter(node => !node.matches(':disabled') && node.tabIndex >= 0 && !node.closest('[inert]') && node.getClientRects().length > 0);
    const focusDialog = () => { if (owns() && !diagnosticOpen()) dialog.focus({ preventScroll: true }); };
    const keydown = event => {
        if (!owns() || diagnosticOpen() || event.key !== 'Tab') return;
        const items = controls(), at = items.indexOf(document.activeElement);
        event.preventDefault();
        const next = items.length ? items[(at < 0 ? (event.shiftKey ? items.length - 1 : 0) : (at + (event.shiftKey ? -1 : 1) + items.length) % items.length)] : dialog;
        next.focus({ preventScroll: true });
    };
    const focusin = event => {
        if (owns() && !diagnosticOpen() && !dialog.contains(event.target)) focusDialog();
    };
    document.addEventListener('keydown', keydown, true);
    document.addEventListener('focusin', focusin, true);
    focusDialog();
    return {
        reconcile: () => {
            if (!owns() || diagnosticOpen()) return;
            if (!dialog.contains(document.activeElement) || document.activeElement?.matches?.(':disabled')) focusDialog();
        },
        dispose: () => {
            if (!active) return;
            active = false;
            document.removeEventListener('keydown', keydown, true);
            document.removeEventListener('focusin', focusin, true);
        }
    };
}
