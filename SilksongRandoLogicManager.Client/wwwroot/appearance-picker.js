(() => {
    const appearance = window.logicAppearance;
    if (!appearance || customElements.get('logic-theme-picker')) return;
    window.logicBindAppearancePicker = (element, callback) => { element.accountWriter = callback; };
    let accountWrites = Promise.resolve();
    const labels = Object.freeze({ Auto: 'auto', Light: 'lace', Dark: 'phantom', Void: 'void', Hornet: 'hornet', Hero: 'wanderer', Custom: 'mask maker' });

    // Blazor owns the empty host. This element owns its children and their events.
    customElements.define('logic-theme-picker', class extends HTMLElement {
        #close;
        #dismiss;

        connectedCallback() {
            if (this.firstChild) return;
            const trigger = document.createElement('button');
            trigger.type = 'button';
            trigger.className = 'tree-action sidebar-utility-action';
            trigger.setAttribute('aria-haspopup', 'menu');
            trigger.setAttribute('aria-expanded', 'false');
            trigger.setAttribute('aria-controls', 'logic-theme-menu');
            trigger.innerHTML = '<span class="v3-icon-slot" aria-hidden="true"><i class="fa-solid fa-fw fa-palette"></i></span>';

            const menu = document.createElement('div');
            menu.id = 'logic-theme-menu';
            menu.className = 'sidebar-theme-menu';
            menu.hidden = true;
            menu.setAttribute('role', 'menu');
            menu.setAttribute('aria-label', 'Theme');
            let customSwatch;
            const items = appearance.modes.map(mode => {
                const item = document.createElement('button');
                item.type = 'button';
                item.tabIndex = -1;
                item.setAttribute('role', 'menuitemradio');
                const slot = document.createElement('span');
                slot.className = 'sidebar-theme-choice-slot';
                slot.setAttribute('aria-hidden', 'true');
                const tick = document.createElement('i');
                tick.className = 'fa-solid fa-fw fa-check';
                const swatch = document.createElement('span');
                swatch.className = 'sidebar-theme-preview';
                swatch.dataset.themePreview = mode;
                if (mode === 'Custom') {
                    customSwatch = swatch;
                    item.title = 'Choose your own color';
                    item.setAttribute('aria-description', item.title);
                }
                slot.append(tick, swatch);
                item.append(slot, labels[mode]);
                item.addEventListener('click', () => {
                    close(true);
                    if (mode === 'Custom') { openCustom(); return; }
                    appearance.select(mode);
                    sync();
                });
                menu.append(item);
                return item;
            });
            const selected = () => appearance.modes.indexOf(appearance.mode);
            function sync() {
                trigger.title = `Theme: ${labels[appearance.mode]}`;
                trigger.setAttribute('aria-label', trigger.title);
                items.forEach((item, index) => item.setAttribute('aria-checked', String(index === selected())));
                if (appearance.savedColor) customSwatch.style.setProperty('--theme-preview-color', appearance.savedColor);
                else customSwatch.style.removeProperty('--theme-preview-color');
            }
            let openEvents;
            function close(returnFocus = false) {
                if (menu.hidden) return;
                openEvents.abort();
                trigger.setAttribute('aria-expanded', 'false');
                if (returnFocus) trigger.focus({ preventScroll: true });
                menu.hidden = true;
            }
            this.#close = close;

            const dialog = document.createElement('dialog');
            dialog.className = 'confirmation-modal custom-appearance-dialog';
            dialog.setAttribute('aria-labelledby', 'custom-appearance-title');
            dialog.innerHTML = `<h2 id="custom-appearance-title">mask maker</h2>
                <form>
                    <label>Theme color <input type="color" required autofocus></label>
                    <div class="confirmation-actions">
                        <button type="submit">Save</button><button type="button">Cancel</button>
                    </div>
                </form>`;
            const status = document.createElement('div');
            status.className = 'custom-appearance-status';
            status.setAttribute('role', 'status');
            status.setAttribute('aria-atomic', 'true');
            const input = dialog.querySelector('input');
            const syncPreview = () => appearance.preview(input.value.toLowerCase());
            function openCustom() {
                input.value = appearance.savedColor ?? '#526d99';
                dialog.showModal(); // Top layer, background inert, native modal keyboard handling.
                syncPreview();
            }
            function dismiss(returnFocus = true) {
                appearance.cancelPreview();
                if (dialog.open) dialog.close();
                if (returnFocus) trigger.focus({ preventScroll: true });
            }
            this.#dismiss = dismiss;
            input.addEventListener('input', syncPreview);
            dialog.querySelector('[type="button"]').addEventListener('click', () => dismiss());
            dialog.addEventListener('cancel', event => { event.preventDefault(); dismiss(); });
            dialog.addEventListener('close', () => { if (!dialog.open) appearance.cancelPreview(); });
            let saveNumber = 0;
            dialog.querySelector('form').addEventListener('submit', event => {
                event.preventDefault();
                const color = input.value.toLowerCase();
                const { colorStored, modeStored } = appearance.saveCustom(color);
                dismiss();
                sync();
                const number = ++saveNumber;
                const browserFailure = [!colorStored && 'The color could not be saved for this tab.',
                    !modeStored && 'The theme choice could not be remembered.'].filter(Boolean).join(' ');
                status.textContent = browserFailure;
                const writer = this.accountWriter;
                // Preserve Save order for the single document without making the
                // browser commit depend on transport, identity or previous writes.
                accountWrites = accountWrites.then(async () => {
                    let saved = false;
                    try { saved = await writer?.invokeMethodAsync('SaveAccountAppearance', color); }
                    catch { /* Browser choice remains active; no automatic retry. */ }
                    if (!saved && number === saveNumber && this.isConnected)
                        status.textContent = ['The color could not be saved to your account.', browserFailure].filter(Boolean).join(' ');
                });
            });

            function position() {
                const { left, top } = trigger.getBoundingClientRect();
                menu.style.left = `${left}px`;
                menu.style.bottom = `calc(100% - ${top}px + .2rem)`;
                menu.style.maxHeight = `max(0px, calc(${top}px - .2rem))`;
            }
            const open = (index = selected()) => {
                if (!menu.hidden) return;
                sync();
                position();
                menu.hidden = false;
                trigger.setAttribute('aria-expanded', 'true');
                openEvents = new AbortController();
                const signal = openEvents.signal;
                document.addEventListener('pointerdown', event => {
                    if (!this.contains(event.target)) close();
                }, { capture: true, signal });
                window.addEventListener('resize', position, { signal });
                document.addEventListener('scroll', event => {
                    if (event.target !== menu) position();
                }, { capture: true, signal });
                items[index].focus();
            };
            trigger.addEventListener('click', () => menu.hidden ? open() : close(true));
            const keydown = event => {
                const index = items.indexOf(event.target);
                if (event.key === 'Tab' && !menu.hidden) {
                    // Hand normal forward/backward tab order back to the trigger position.
                    close(true);
                    return;
                }
                let next;
                switch (event.key) {
                    case 'Escape': if (menu.hidden) return; break;
                    case 'ArrowDown': next = (index + 1) % items.length; break;
                    case 'ArrowUp': next = index < 0 ? items.length - 1 : (index + items.length - 1) % items.length; break;
                    case 'Home': if (index < 0) return; next = 0; break;
                    case 'End': if (index < 0) return; next = items.length - 1; break;
                    default: return; // Native Enter and Space activation.
                }
                event.preventDefault();
                event.stopPropagation();
                if (event.key === 'Escape') close(true);
                else if (menu.hidden) open(next);
                else items[next].focus();
            };
            trigger.addEventListener('keydown', keydown);
            menu.addEventListener('keydown', keydown);
            menu.addEventListener('focusout', event => {
                if (event.relatedTarget !== trigger && !menu.contains(event.relatedTarget)) close();
            });
            trigger.addEventListener('focusout', event => {
                if (!menu.contains(event.relatedTarget)) close();
            });
            sync();
            this.append(trigger, menu, dialog, status);
        }

        disconnectedCallback() { this.#close?.(); if (this.querySelector('dialog')?.open) this.#dismiss?.(false); }
    });
})();
