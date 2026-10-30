// One explicitly mounted audit-only owner. Camera motion never crosses the circuit.
const owners = new WeakMap();
export function mount(root, dotnet, owner) {
    owners.get(root)?.dispose();
    const svg = root.querySelector('svg');
    // Escape is scoped to this mounted audit article, never document/window.
    const keyboardRoot = root.closest('.room-audit') ?? root;
    const fit = svg ? svg.dataset.fit.split(/\s+/).map(Number) : [];
    if (svg && (fit.length !== 4 || !fit.every(Number.isFinite) || fit[2] <= 0 || fit[3] <= 0))
        throw new Error('Audit scene extent is unusable.');
    let box = fit.slice(), scale = 1, gesture = null, disposed = false, failed = false;
    const listeners = [];
    const current = () => !disposed && !failed && root.isConnected && Number(root.dataset.auditOwner) === owner && owners.get(root) === api;
    function release() {
        if (gesture && svg?.hasPointerCapture(gesture.id)) svg.releasePointerCapture(gesture.id);
        gesture = null;
    }
    function fail(error) {
        if (!current()) return;
        failed = true; release();
        dotnet.invokeMethodAsync('CameraFailedAsync', owner, String(error)).catch(() => {});
    }
    function listen(node, type, handler, options) {
        const guarded = event => { if (current()) try { handler(event); } catch (error) { fail(error); } };
        node.addEventListener(type, guarded, options);
        listeners.push(() => node.removeEventListener(type, guarded, options));
    }
    function paint() {
        if (!svg) return;
        if (!box.every(Number.isFinite) || box[2] <= 0 || box[3] <= 0) throw new Error('Nonfinite audit camera.');
        svg.setAttribute('viewBox', box.join(' '));
    }
    function point(x, y) {
        const matrix = svg.getScreenCTM();
        if (!matrix) throw new Error('Audit camera has no screen projection.');
        return new DOMPoint(x, y).matrixTransform(matrix.inverse());
    }
    function zoom(factor, x, y) {
        const next = Math.min(32, Math.max(1 / 8, scale * factor));
        const ratio = scale / next, anchor = point(x, y);
        box = [anchor.x + (box[0] - anchor.x) * ratio, anchor.y + (box[1] - anchor.y) * ratio, box[2] * ratio, box[3] * ratio];
        scale = next; paint();
    }
    function select(hit) {
        if (!hit || !svg.contains(hit)) return;
        const pick = root.dataset.pick === 'true';
        if (pick && !['exit', 'subroom'].includes(hit.dataset.auditKind)) return;
        dotnet.invokeMethodAsync('SelectAsync', { owner, kind: hit.dataset.auditKind, id: hit.dataset.auditId, pickOrigin: pick }).catch(fail);
    }
    if (svg) {
    listen(svg, 'wheel', e => {
        e.preventDefault();
        zoom(Math.exp(-Math.max(-120, Math.min(120, e.deltaY)) * .002), e.clientX, e.clientY);
    }, { passive: false });
    listen(svg, 'pointerdown', e => {
        if (e.button !== 0 || gesture) return;
        const hit = e.target.closest('[data-audit-kind]');
        gesture = { id: e.pointerId, x: e.clientX, y: e.clientY, lastX: e.clientX, lastY: e.clientY, dragged: false, hit };
        svg.setPointerCapture(e.pointerId); e.preventDefault(); svg.focus({ preventScroll: true });
    });
    listen(svg, 'pointermove', e => {
        if (!gesture || gesture.id !== e.pointerId) return;
        if (Math.hypot(e.clientX - gesture.x, e.clientY - gesture.y) >= 5) gesture.dragged = true;
        if (!gesture.dragged) return;
        const before = point(gesture.lastX, gesture.lastY), after = point(e.clientX, e.clientY);
        box[0] += before.x - after.x; box[1] += before.y - after.y;
        gesture.lastX = e.clientX; gesture.lastY = e.clientY; paint();
    });
    listen(svg, 'pointerup', e => {
        if (!gesture || gesture.id !== e.pointerId) return;
        const done = gesture;
        // Also measure on release, so a coalesced/lost move cannot turn a drag into a click.
        const dragged = done.dragged || Math.hypot(e.clientX - done.x, e.clientY - done.y) >= 5;
        release();
        if (!dragged) select(done.hit);
    });
    listen(svg, 'pointercancel', release);
    listen(svg, 'lostpointercapture', () => { gesture = null; });
    }
    listen(keyboardRoot, 'keydown', e => {
        if (e.key === 'Escape') {
            release();
            if (root.dataset.pick === 'true') dotnet.invokeMethodAsync('SelectAsync', { owner, kind: 'cancel', id: '00000000-0000-0000-0000-000000000000', pickOrigin: true }).catch(fail);
            return;
        }
        if (svg?.contains(e.target) && (e.key === 'Enter' || e.key === ' ') && e.target.matches('[data-audit-kind]')) { e.preventDefault(); select(e.target); }
    });
    for (const button of root.querySelectorAll('[data-camera]')) listen(button, 'click', () => {
        if (!svg || button.disabled) return;
        release();
        if (button.dataset.camera === 'fit') { box = fit.slice(); scale = 1; paint(); }
        else { const r = svg.getBoundingClientRect(); zoom(button.dataset.camera === 'in' ? 1.25 : .8, r.x + r.width / 2, r.y + r.height / 2); }
    });
    const api = { revealControl(id) {
        if (!current()) return;
        const control = Array.from(keyboardRoot.querySelectorAll('[id]')).find(element => element.id === id);
        if (!control) return;
        for (let ancestor = control.parentElement; ancestor && ancestor !== keyboardRoot; ancestor = ancestor.parentElement)
            if (ancestor instanceof HTMLDetailsElement) ancestor.open = true;
        const input = control.matches('input,select,button') ? control : control.querySelector('input,select,button');
        input?.focus({ preventScroll: true });
        control.scrollIntoView({ block: 'nearest' });
    }, dispose() {
        if (disposed) return;
        disposed = true; release(); listeners.splice(0).forEach(remove => remove());
        if (owners.get(root) === api) owners.delete(root);
    } };
    owners.set(root, api); paint(); return api;
}
