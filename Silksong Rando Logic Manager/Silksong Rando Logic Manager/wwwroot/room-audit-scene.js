// One explicitly mounted audit-only owner. Camera motion never crosses the circuit.
const owners = new WeakMap();
export function mount(root, dotnet, owner, externalModal = null) {
    owners.get(root)?.dispose();
    const svg = root.querySelector('svg.audit-scene');
    // Escape is scoped to this mounted audit article, never document/window.
    const keyboardRoot = root.closest('.room-audit') ?? root;
    const modalOpen = () => externalModal?.open === true || !!keyboardRoot.querySelector('dialog[data-audit-modal][open]');
    const fit = svg ? svg.dataset.fit.split(/\s+/).map(Number) : [];
    if (svg && (fit.length !== 4 || !fit.every(Number.isFinite) || fit[2] <= 0 || fit[3] <= 0))
        throw new Error('Audit scene extent is unusable.');
    const heightHandle = root.querySelector('[data-audit-image-height]');
    const output = root.closest('.audit-output');
    let box = fit.slice(), scale = 1, gesture = null, heightDrag = null, disposed = false, failed = false;
    let imageHeight = svg ? svg.getBoundingClientRect().height : 330;
    const shown = { subroom: true, exit: true, check: true };
    const listeners = [];
    const current = () => !disposed && !failed && root.isConnected && Number(root.dataset.auditOwner) === owner && Number(keyboardRoot.dataset.auditPageOwner) === owner && owners.get(root) === api;
    function release() {
        if (gesture && svg?.hasPointerCapture(gesture.id)) svg.releasePointerCapture(gesture.id);
        gesture = null;
        cancelHeight();
    }
    function fail(error) {
        if (!current()) return;
        failed = true; release();
        for (const button of root.querySelectorAll('[data-camera], [data-audit-layer-toggle], [data-audit-image-height]')) button.disabled = true;
        dotnet.invokeMethodAsync('CameraFailedAsync', owner, String(error)).catch(() => {});
    }
    function listen(node, type, handler, options) {
        const guarded = event => { if (current()) try {
            if (modalOpen()) { release(); return; }
            handler(event);
        } catch (error) { fail(error); } };
        node.addEventListener(type, guarded, options);
        listeners.push(() => node.removeEventListener(type, guarded, options));
    }
    // Native dialog commands own opening, focus containment, Escape/Close and focus return,
    // even without geometry or a mounted camera. The existing owner only cancels gestures.
    // Only dialog membership/open changes are observed. No layer or viewport observation.
    const modals = new MutationObserver(records => {
        if (!current()) return;
        if (records.some(record => record.type === 'attributes' &&
            (record.target === externalModal || record.target.matches('dialog[data-audit-modal]')) &&
            (record.target.open || record.oldValue !== null)) || modalOpen()) release();
    });
    modals.observe(keyboardRoot, { subtree: true, childList: true, attributes: true,
        attributeFilter: ['open', 'data-audit-modal'], attributeOldValue: true });
    if (externalModal instanceof HTMLDialogElement)
        modals.observe(externalModal, { attributes: true, attributeFilter: ['open'], attributeOldValue: true });
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
    function heightLimits() {
        // Measure this actual output/scene composition, not guessed header offsets.
        // Leave a usable inspection lane below camera controls and endpoint chin.
        const minimum = 120;
        const chrome = root.getBoundingClientRect().height - svg.getBoundingClientRect().height;
        return [minimum, Math.max(minimum, (output?.clientHeight ?? root.clientHeight) - chrome - 120)];
    }
    function applyHeight(value) {
        if (!svg || !heightHandle || !root.isConnected || Number(keyboardRoot.dataset.auditPageOwner) !== owner || owners.get(root) !== api) return imageHeight;
        const [minimum, maximum] = heightLimits();
        const height = Math.min(maximum, Math.max(minimum, value));
        // Article-local CSS dies with this page. No storage or scene-data update.
        keyboardRoot.style.setProperty('--audit-scene-height', `${height}px`);
        heightHandle.setAttribute('aria-valuemin', String(minimum));
        heightHandle.setAttribute('aria-valuemax', String(Math.round(maximum)));
        heightHandle.setAttribute('aria-valuenow', String(Math.round(height)));
        return height;
    }
    function cancelHeight() {
        if (!heightDrag) return;
        const id = heightDrag.id; heightDrag = null;
        if (heightHandle.hasPointerCapture(id)) heightHandle.releasePointerCapture(id);
        if (root.isConnected && Number(keyboardRoot.dataset.auditPageOwner) === owner) applyHeight(imageHeight);
    }
    if (svg && heightHandle) {
        listen(heightHandle, 'pointerdown', e => {
            if (e.button !== 0 || heightDrag) return;
            release(); e.preventDefault(); heightHandle.focus({ preventScroll: true });
            heightDrag = { id: e.pointerId, y: e.clientY, height: svg.getBoundingClientRect().height };
            heightHandle.setPointerCapture(e.pointerId);
        });
        listen(heightHandle, 'pointermove', e => {
            if (heightDrag?.id === e.pointerId) applyHeight(heightDrag.height + e.clientY - heightDrag.y);
        });
        listen(heightHandle, 'pointerup', e => {
            if (heightDrag?.id !== e.pointerId) return;
            imageHeight = applyHeight(heightDrag.height + e.clientY - heightDrag.y);
            heightDrag = null;
            if (heightHandle.hasPointerCapture(e.pointerId)) heightHandle.releasePointerCapture(e.pointerId);
        });
        listen(heightHandle, 'pointercancel', cancelHeight);
        listen(heightHandle, 'lostpointercapture', cancelHeight);
        listen(heightHandle, 'keydown', e => {
            const [minimum, maximum] = heightLimits();
            const next = e.key === 'Home' ? minimum : e.key === 'End' ? maximum :
                e.key === 'ArrowUp' ? imageHeight - 16 : e.key === 'ArrowDown' ? imageHeight + 16 : null;
            if (next === null) return;
            e.preventDefault(); cancelHeight(); imageHeight = applyHeight(next);
        });
    }
    function zoom(factor, x, y) {
        const next = Math.min(32, Math.max(1 / 8, scale * factor));
        const ratio = scale / next, anchor = point(x, y);
        box = [anchor.x + (box[0] - anchor.x) * ratio, anchor.y + (box[1] - anchor.y) * ratio, box[2] * ratio, box[3] * ratio];
        scale = next; paint();
    }
    const category = kind => kind === 'connection' || kind === 'exit' ? 'exit' : kind === 'subroom' ? 'subroom' : kind === 'check' ? 'check' : null;
    function visibleHit(hit) {
        return current() && !!hit && !!svg?.contains(hit) &&
            shown[category(hit.dataset.auditKind)] === true && !hit.closest('[hidden]');
    }
    function reconcile() {
        if (!current()) return;
        for (const layer of Object.keys(shown)) {
            root.toggleAttribute(`data-audit-hide-${layer}`, !shown[layer]);
            for (const node of root.querySelectorAll(`[data-audit-layer="${layer}"]`)) {
                node.toggleAttribute('hidden', !shown[layer]);
                if (node.matches('[data-audit-kind]')) node.setAttribute('tabindex', shown[layer] ? '0' : '-1');
            }
        }
        if (gesture?.hit && (!visibleHit(gesture.hit) || gesture.hit.dataset.auditKind !== gesture.kind || gesture.hit.dataset.auditId !== gesture.key)) release();
        for (const button of root.querySelectorAll('[data-audit-layer-toggle]')) {
            const layer = button.dataset.auditLayerToggle;
            const visible = layer === 'all' ? Object.values(shown).every(Boolean) : shown[layer];
            const name = layer === 'all' ? 'annotations' : layer === 'subroom' ? 'subrooms' : layer === 'exit' ? 'exits and connections' : 'check locations and enemies';
            button.setAttribute('aria-pressed', String(visible));
            button.setAttribute('aria-label', `${visible ? 'Hide' : 'Show'} ${name}`);
            button.title = `${visible ? 'Hide' : 'Show'} ${name}`;
            button.disabled = !svg;
        }
    }
    function select(hit, origin = true, kind = hit?.dataset.auditKind, key = hit?.dataset.auditId) {
        if (!visibleHit(hit) || modalOpen() || !key || hit.dataset.auditKind !== kind || hit.dataset.auditId !== key) return;
        dotnet.invokeMethodAsync('SelectAsync', { owner, kind: hit.dataset.auditKind, id: hit.dataset.auditId,
            origin: hit.dataset.auditKind !== 'connection' && origin }).catch(fail);
    }
    if (svg) {
    listen(svg, 'wheel', e => {
        e.preventDefault();
        zoom(Math.exp(-Math.max(-120, Math.min(120, e.deltaY)) * .002), e.clientX, e.clientY);
    }, { passive: false });
    listen(svg, 'pointerdown', e => {
        if (![0, 2].includes(e.button) || gesture || heightDrag) return;
        const target = e.target.closest('[data-audit-kind]');
        const hit = visibleHit(target) ? target : null;
        if (e.button === 2 && !hit) return;
        gesture = { id: e.pointerId, button: e.button, x: e.clientX, y: e.clientY, lastX: e.clientX, lastY: e.clientY, dragged: false, hit, kind: hit?.dataset.auditKind, key: hit?.dataset.auditId };
        svg.setPointerCapture(e.pointerId); e.preventDefault(); svg.focus({ preventScroll: true });
    });
    listen(svg, 'pointermove', e => {
        if (!gesture || gesture.id !== e.pointerId) return;
        if (Math.hypot(e.clientX - gesture.x, e.clientY - gesture.y) >= 5) gesture.dragged = true;
        if (!gesture.dragged || gesture.button !== 0) return;
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
        if (!dragged) select(done.hit, done.button === 0, done.kind, done.key);
    });
    listen(svg, 'pointercancel', release);
    listen(svg, 'lostpointercapture', () => { gesture = null; });
    listen(svg, 'contextmenu', e => {
        // Background and the rest of the page keep the native menu.
        if (visibleHit(e.target.closest('[data-audit-kind]'))) e.preventDefault();
    });
    }
    listen(keyboardRoot, 'keydown', e => {
        const tab = e.target.closest('[role="tab"]');
        if (tab && keyboardRoot.contains(tab) && ['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(e.key)) {
            // Blazor owns category selection/focus; this mounted owner only suppresses native scrolling.
            e.preventDefault();
            return;
        }
        if (e.key === 'Escape') {
            release();
            return;
        }
        if (svg?.contains(e.target) && (e.key === 'Enter' || e.key === ' ') && e.target.matches('[data-audit-kind]')) { e.preventDefault(); select(e.target, !e.shiftKey); }
    });
    for (const button of root.querySelectorAll('[data-camera]')) listen(button, 'click', () => {
        if (!svg || button.disabled) return;
        release();
        if (button.dataset.camera === 'fit') { box = fit.slice(); scale = 1; paint(); }
        else { const r = svg.getBoundingClientRect(); zoom(button.dataset.camera === 'in' ? 1.25 : .8, r.x + r.width / 2, r.y + r.height / 2); }
    });
    for (const button of root.querySelectorAll('[data-audit-layer-toggle]')) listen(button, 'click', () => {
        if (!svg || button.disabled) return;
        const layer = button.dataset.auditLayerToggle;
        if (layer === 'all') {
            const visible = !Object.values(shown).every(Boolean);
            for (const key of Object.keys(shown)) shown[key] = visible;
        } else if (Object.hasOwn(shown, layer)) shown[layer] = !shown[layer];
        reconcile();
    });
    const api = { reconcile() {
        // Reapply the existing flags to fresh DOM even under a modal; this changes no
        // flag or camera state and dispatch remains blocked by modal admission.
        if (!current()) return;
        try { reconcile(); if (modalOpen()) release(); if (!heightDrag) imageHeight = applyHeight(imageHeight); } catch (error) { fail(error); }
    }, dispose() {
        if (disposed) return;
        disposed = true; modals.disconnect(); release(); listeners.splice(0).forEach(remove => remove());
        if (owners.get(root) === api) owners.delete(root);
    } };
    owners.set(root, api); reconcile(); paint(); imageHeight = applyHeight(imageHeight); return api;
}
