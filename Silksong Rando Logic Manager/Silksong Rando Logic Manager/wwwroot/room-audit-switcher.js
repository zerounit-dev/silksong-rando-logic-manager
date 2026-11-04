// Audit-local helpers, independent of the scene/camera owner.
// Inline search exclusively owns its descendants and live editing/result state.
export function bindHelpFocus(dialog, launcher, root, owner) {
    let disposed = false, opening = 0;
    const current = () => !disposed && root.isConnected && dialog.isConnected && launcher.isConnected &&
        Number(root.closest('.room-audit').dataset.auditPageOwner) === owner;
    const toggle = event => {
        const stamp = ++opening;
        if (event.newState !== 'closed') return;
        queueMicrotask(() => {
            if (current() && stamp === opening && !dialog.open &&
                !root.closest('.room-audit').querySelector('dialog[data-audit-modal][open]'))
                launcher.focus({ preventScroll: true });
        });
    };
    dialog.addEventListener('beforetoggle', toggle);
    return { dispose() { disposed = true; ++opening; dialog.removeEventListener('beforetoggle', toggle); } };
}

// Only this dialog's native lifetime; no scene, document or window event owner.
export function bindRequirements(dialog, root, dotnet, owner) {
    let disposed = false, request = 0, retired = 0, opening = 0, launcher = null;
    const current = () => !disposed && root.isConnected && dialog.isConnected &&
        Number(root.dataset.auditPageOwner) === owner;
    const otherModal = () => [...root.querySelectorAll('dialog[data-audit-modal][open]')].some(item => item !== dialog);
    const toggle = event => {
        if (event.newState !== 'closed' || !current()) return;
        const id = request, original = launcher, stamp = ++opening;
        retired = Math.max(retired, id); request = 0; launcher = null;
        if (id) dotnet.invokeMethodAsync('RequirementsClosedAsync', owner, id).catch(() => {});
        queueMicrotask(() => {
            if (!id || !current() || opening !== stamp || Number(dialog.dataset.requirementsRequest) > id || dialog.open || otherModal()) return;
            const target = original?.isConnected && root.contains(original) ? original : root.querySelector('#audit-room-points-title');
            target?.focus({ preventScroll: true });
        });
    };
    dialog.addEventListener('beforetoggle', toggle);
    return {
        reconcile(id, original) {
            if (!current()) return false;
            if (!id) {
                request = 0; launcher = null; ++opening;
                if (dialog.open) dialog.close();
                return false;
            }
            if (id <= retired || dialog.dataset.requirementsRequest !== String(id) || otherModal()) return false;
            if (request !== id) {
                ++opening; request = id;
                launcher = original;
            }
            if (!dialog.open) dialog.showModal();
            return true;
        },
        dispose() {
            disposed = true; request = 0; launcher = null; ++opening;
            dialog.removeEventListener('beforetoggle', toggle);
        }
    };
}

export function mountInline(host, dotnet, owner) {
    const article = host.closest('.room-audit');
    const abort = new AbortController(), options = { signal: abort.signal };
    let disposed = false, request = 0, context = -1, currentRoom = null;
    let state = 'loading', index = [], rows = [], active = null, open = false, composing = false;
    const current = () => !disposed && host.isConnected && article?.isConnected && host.dataset.searchOwner === owner;
    const admitted = (key, id) => current() && key === owner && id === request;
    const element = (tag, attributes = {}, text = null) => {
        const node = document.createElement(tag);
        for (const [key, value] of Object.entries(attributes)) node.setAttribute(key, value);
        if (text !== null) node.textContent = text;
        return node;
    };
    const prefix = `audit-search-${owner}`;
    const label = element('label', { for: `${prefix}-input` }, 'Room name or game ID');
    const input = element('input', { id: `${prefix}-input`, type: 'search', role: 'combobox',
        'aria-autocomplete': 'list', 'aria-expanded': 'false', 'aria-controls': `${prefix}-list`,
        'aria-describedby': `${prefix}-status`, autocomplete: 'off' });
    const popup = element('div', { class: 'audit-search-popup' });
    const status = element('div', { id: `${prefix}-status`, class: 'audit-search-status', role: 'status', 'aria-live': 'polite' });
    const retry = element('button', { type: 'button' }, 'Retry');
    const list = element('div', { id: `${prefix}-list`, role: 'listbox', 'aria-label': 'Rooms' });
    popup.hidden = true; retry.hidden = true;
    popup.append(status, retry, list); host.replaceChildren(label, input, popup);

    const display = (value, fallback) => !value || !value.trim() ? fallback : value;
    function prepare(rooms) {
        // Service order is the canonical ordinal name/GUID organizational comparator. Retain
        // its position as the final tie key rather than introducing localeCompare differences.
        const prepared = rooms.map((room, order) => ({ ...room, order,
            name: display(room.friendlyName, 'Unnamed room'),
            game: display(room.inGameId, 'No game ID'),
            group: room.groupId === null ? 'Ungrouped' : display(room.groupName, 'Unnamed group'),
            searchName: room.friendlyName.toUpperCase(), searchId: (room.inGameId ?? '').toUpperCase(), suffix: '' }));
        const collisions = new Map();
        for (const room of prepared) {
            const key = JSON.stringify([room.name, room.game, room.group]);
            if (!collisions.has(key)) collisions.set(key, []);
            collisions.get(key).push(room);
        }
        for (const group of collisions.values()) {
            if (group.length < 2) continue;
            const ids = group.map(room => room.id.replaceAll('-', ''));
            group.forEach((room, i) => {
                let length = 1;
                while (ids.some((id, j) => j !== i && id.startsWith(ids[i].slice(0, length)))) length++;
                room.suffix = ids[i].slice(0, length);
            });
        }
        return prepared;
    }
    function filter() {
        const query = input.value.trim().toUpperCase(), tokens = query ? query.split(/\s+/u) : [];
        const tier = room => !query || room.searchName === query || room.searchId === query ? 0
            : room.searchName.startsWith(query) || room.searchId.startsWith(query) ? 1 : 2;
        rows = state === 'ready' ? index.filter(room => tokens.every(token =>
            room.searchName.includes(token) || room.searchId.includes(token)))
            .sort((a, b) => tier(a) - tier(b) || a.order - b.order) : [];
        active = rows[0]?.id ?? null;
        renderRows();
    }
    function renderRows() {
        const fragment = document.createDocumentFragment();
        for (const room of rows) {
            const row = element('div', { id: `${prefix}-${room.id}`, role: 'option', 'data-search-room': room.id,
                'aria-selected': String(active === room.id), title: `${room.name} · ${room.game} · ${room.group} · ${room.id}` });
            row.append(element('strong', {}, room.name));
            const marker = element('span', { 'data-current-marker': '' }, ' — current room');
            marker.hidden = room.id !== currentRoom; row.append(marker);
            if (room.id === currentRoom) row.setAttribute('aria-current', 'page');
            row.append(element('span', { class: 'audit-search-context' },
                `${room.game} · ${room.group}${room.suffix ? ` · ${room.suffix}` : ''}`));
            fragment.append(row);
        }
        list.replaceChildren(fragment);
        list.setAttribute('aria-busy', String(state === 'loading'));
        status.textContent = state === 'loading' ? 'Loading rooms…' : state === 'failure'
            ? 'Rooms could not load. Retry to read fresh data.' : index.length === 0 ? 'No active rooms.'
            : rows.length === 0 ? 'No matching rooms.' : `${rows.length} rooms.`;
        retry.hidden = state !== 'failure';
        syncActive();
    }
    function syncActive() {
        for (const row of list.children) row.setAttribute('aria-selected', String(row.dataset.searchRoom === active));
        if (open && active) input.setAttribute('aria-activedescendant', `${prefix}-${active}`);
        else input.removeAttribute('aria-activedescendant');
        if (!open || !active) return;
        const row = [...list.children].find(row => row.dataset.searchRoom === active);
        if (!row) return;
        // Scroll the list only; do not use scrollIntoView (which can move the audit/map).
        const box = row.getBoundingClientRect(), port = list.getBoundingClientRect();
        if (box.top < port.top) list.scrollTop -= port.top - box.top;
        else if (box.bottom > port.bottom) list.scrollTop += box.bottom - port.bottom;
    }
    function show() {
        if (!current()) return;
        open = true; popup.hidden = false; input.setAttribute('aria-expanded', 'true'); syncActive();
    }
    function dismiss() {
        open = false; popup.hidden = true; input.setAttribute('aria-expanded', 'false'); syncActive();
    }
    function choose(id) {
        if (!current() || !open || state !== 'ready' || !rows.some(room => room.id === id)) return;
        dismiss();
        // Only a committed GUID (plus owner/request/context admission) crosses the circuit.
        if (id !== currentRoom) dotnet.invokeMethodAsync('ChooseAsync', owner, request, context, id).catch(() => {});
    }
    input.addEventListener('focus', show, options);
    input.addEventListener('input', () => { if (current()) { filter(); show(); } }, options);
    input.addEventListener('compositionstart', () => composing = true, options);
    input.addEventListener('compositionend', () => composing = false, options);
    input.addEventListener('keydown', event => {
        if (!current() || event.isComposing || composing || event.keyCode === 229 || event.altKey || event.ctrlKey || event.metaKey) return;
        // Suppress the native search-field clear action even when already dismissed.
        if (event.key === 'Escape') { event.preventDefault(); dismiss(); return; }
        if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
            event.preventDefault();
            if (!open) { show(); return; }
            if (!rows.length) return;
            const at = rows.findIndex(room => room.id === active);
            active = rows[Math.max(0, Math.min(rows.length - 1, at + (event.key === 'ArrowDown' ? 1 : -1)))].id;
            syncActive();
        } else if (event.key === 'Enter' && open) {
            event.preventDefault();
            if (active) choose(active);
        }
    }, options);
    // Keep pointer selection ahead of native blur. This is list-only, never outside action suppression.
    list.addEventListener('pointerdown', event => {
        if (event.button === 0 && event.target.closest('[data-search-room]')) event.preventDefault();
    }, options);
    list.addEventListener('click', event => {
        const row = event.target.closest('[data-search-room]');
        if (row && list.contains(row)) choose(row.dataset.searchRoom);
    }, options);
    host.addEventListener('focusout', event => {
        if (!host.contains(event.relatedTarget)) dismiss();
    }, options);
    article.addEventListener('pointerdown', event => {
        if (!host.contains(event.target)) dismiss();
    }, options);
    retry.addEventListener('click', () => {
        if (!current() || state !== 'failure') return;
        state = 'loading'; index = []; filter();
        dotnet.invokeMethodAsync('RetryAsync', owner, request).catch(() => {});
    }, options);
    return {
        setContext(key, revision, id) {
            if (!current() || key !== owner || revision < context) return;
            context = revision; currentRoom = id;
            // Update indication only; don't rebuild results, reset active, close, or touch input.
            for (const row of list.children) {
                const selected = row.dataset.searchRoom === id;
                row.querySelector('[data-current-marker]').hidden = !selected;
                if (selected) row.setAttribute('aria-current', 'page'); else row.removeAttribute('aria-current');
            }
        },
        loading(key, id) {
            if (!current() || key !== owner || id <= request) return;
            request = id; state = 'loading'; index = []; filter();
        },
        loaded(key, id, rooms) {
            if (!admitted(key, id)) return;
            index = prepare(rooms); state = 'ready'; filter();
        },
        failure(key, id) {
            if (!admitted(key, id)) return;
            state = 'failure'; index = []; filter();
        },
        dispose() { disposed = true; ++request; abort.abort(); host.replaceChildren(); }
    };
}
