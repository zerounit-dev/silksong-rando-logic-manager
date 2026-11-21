(() => {
    const appearance = window.logicAppearance;
    if (!appearance || customElements.get('logic-theme-picker')) return;

    // Blazor owns the empty host. This element owns its children and their events.
    customElements.define('logic-theme-picker', class extends HTMLElement {
        #close;

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
            const items = appearance.modes.map(mode => {
                const item = document.createElement('button');
                item.type = 'button';
                item.tabIndex = -1;
                item.setAttribute('role', 'menuitemradio');
                const tick = document.createElement('i');
                tick.className = 'fa-solid fa-fw fa-check';
                tick.setAttribute('aria-hidden', 'true');
                item.append(tick, mode);
                item.addEventListener('click', () => {
                    appearance.select(mode);
                    sync();
                    close(true);
                });
                menu.append(item);
                return item;
            });
            const selected = () => appearance.modes.indexOf(appearance.mode);
            function sync() {
                trigger.title = `Theme: ${appearance.mode}`;
                trigger.setAttribute('aria-label', trigger.title);
                items.forEach((item, index) => item.setAttribute('aria-checked', String(index === selected())));
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
            this.append(trigger, menu);
        }

        disconnectedCallback() { this.#close?.(); }
    });
})();
