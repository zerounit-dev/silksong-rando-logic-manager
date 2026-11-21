(() => {
    if (window.logicAppearance) return;

    const root = document.documentElement;
    const key = 'silksong-rando-logic.appearance';
    const brightnessKey = `${key}.hero-brightness`;
    const minBrightness = .22;
    const maxBrightness = .97;
    const modes = Object.freeze(['Auto', 'Light', 'Dark', 'Void', 'Hero']);
    const systemDark = window.matchMedia?.('(prefers-color-scheme: dark)');
    let mode = 'Auto';
    let hero;
    try {
        const saved = localStorage.getItem(key);
        if (modes.includes(saved)) mode = saved;
    } catch { /* This origin may not have readable storage. */ }

    function nextBrightness() {
        let previous = NaN;
        try {
            const saved = sessionStorage.getItem(brightnessKey);
            if (saved?.trim()) previous = Number(saved);
        } catch { /* Treat an unavailable tab history as a new walk. */ }
        const roll = Math.random();
        let p = minBrightness + roll * (maxBrightness - minBrightness);
        if (previous >= minBrightness && previous <= maxBrightness) {
            // One quarter of the .75 span; reflection never lengthens a step.
            p = previous + (2 * roll - 1) * .1875;
            if (p < minBrightness) p = 2 * minBrightness - p;
            if (p > maxBrightness) p = 2 * maxBrightness - p;
        }
        return p;
    }

    // Start on the requested OKLCH ray and scale the linear RGB triplet to
    // exact WCAG luminance. Uniform scaling preserves OKLCH hue. Where the
    // tinted ray leaves sRGB, reduce chroma, not luminance or hue.
    const weights = [.2126, .7152, .0722];
    function linearRgb(y, a, b) {
        const l = (1 + .3963377774 * a + .2158037573 * b) ** 3;
        const m = (1 - .1055613458 * a - .0638541728 * b) ** 3;
        const s = (1 - .0894841775 * a - 1.2914855480 * b) ** 3;
        const rgb = [
            4.0767416621 * l - 3.3077115913 * m + .2309699292 * s,
            -1.2684380046 * l + 2.6097574011 * m - .3413193965 * s,
            -.0041960863 * l - .7034186147 * m + 1.7076147010 * s,
        ];
        const scale = y / rgb.reduce((sum, channel, i) => sum + channel * weights[i], 0);
        return rgb.map(channel => channel * scale);
    }

    function tint(y, angle, strength) {
        const a = Math.cos(angle);
        const b = Math.sin(angle);
        let hi = .16 * strength;
        let rgb = linearRgb(y, a * hi, b * hi);
        const inGamut = channels => channels.every(channel => channel >= 0 && channel <= 1);
        if (!inGamut(rgb)) {
            let lo = 0;
            rgb = [y, y, y];
            for (let i = 0; i < 12; i++) {
                const r = (hi + lo) / 2;
                const trial = linearRgb(y, a * r, b * r);
                if (inGamut(trial)) { lo = r; rgb = trial; }
                else hi = r;
            }
        }
        return `color(srgb-linear ${rgb.map(channel => channel.toFixed(8)).join(' ')})`;
    }

    function makeHero(p, hue) {
        const angle = hue * 2 * Math.PI;
        const split = Math.cbrt(.179);
        const dark = p < split;
        // Bring the tonal ladder together near the ink/scheme crossover. Each
        // role stays on the canvas's contrast side and has slope in [.2, 1].
        const towardSplit = Math.sign(p - split) * Math.min(.11, .8 * Math.abs(p - split));
        const fills = Object.entries({
            canvas: [0, .23],
            sidebar: [.19, .27],
            surface: [.32, .25],
            'surface-raised': [.52, .27],
            'surface-muted': [.64, .32],
            'hover-subtle': [.70, .38],
            hover: [.82, .45],
            selection: [1, .57],
        });
        const values = {};
        const crossoverChroma = Math.min(1, .5 + 4 * Math.abs(p - split));
        for (const [role, [offset, chroma]] of fills)
            values[`--hero-${role}`] = tint((p - offset * towardSplit) ** 3, angle, chroma * crossoverChroma);

        const nearest = (p - towardSplit) ** 3;
        // The weakest fixed syntax inks allow Y <= .07588 or Y >= .45385.
        // Only provide a local backing when the shared chrome cannot supply it.
        const pairSyntax = dark ? nearest > .075 : nearest < .455;
        const syntaxY = dark ? .068 : .47;
        values['--hero-syntax-backing'] = pairSyntax ? tint(syntaxY, angle, .16) : 'transparent';
        const inkLimit = ratio => dark ? (nearest + .05) * ratio - .05 : (nearest + .05) / ratio - .05;
        const ink = (ratio, lightY, darkY, chroma = 0, h = angle) =>
            tint(dark ? Math.max(lightY, inkLimit(ratio)) : Math.min(darkY, inkLimit(ratio)), h, chroma);
        values['--hero-ink'] = ink(4.58, .89, .012, .24);
        values['--hero-muted'] = ink(4.55, .67, .045, .25);
        values['--hero-secondary'] = ink(4.56, .76, .032, .29);
        values['--hero-line'] = ink(3.08, .37, .12, .40);
        values['--hero-boundary'] = ink(3.5, .54, .08, .47);
        values['--hero-focus'] = ink(4.57, .83, .024, .43);
        values['--hero-primary'] = ink(4.57, .71, .035, .63);
        values['--hero-primary-hover'] = ink(4.58, .88, .016, .62);

        // Semantic surfaces follow the same bounded ladder, independent of Hero
        // hue. Even where shared ink has to approach black/white, these fills
        // retain their semantic hue. Text-sized semantic pairs are set in CSS.
        for (const [role, degrees] of Object.entries({ danger: 12, warning: 52, success: 140, info: 195 })) {
            const h = degrees * Math.PI / 180;
            values[`--hero-${role}`] = ink(4.55, .75, .037, .90, h);
            values[`--hero-${role}-bg`] = tint((p - .64 * towardSplit) ** 3, h, .88);
            values[`--hero-${role}-hover`] = tint((p - .82 * towardSplit) ** 3, h, .98);
            if (role === 'danger' || role === 'warning')
                values[`--hero-${role}-name-backing`] = pairSyntax ? tint(syntaxY, h, .9) : 'transparent';
        }
        return { tone: dark ? 'Dark' : 'Light', values };
    }

    function apply() {
        const palette = mode === 'Auto' ? (systemDark?.matches ? 'Dark' : 'Light') : mode;
        if (palette === 'Hero' && !hero) {
            const hue = Math.random();
            const p = nextBrightness();
            hero = makeHero(p, hue);
            try { sessionStorage.setItem(brightnessKey, String(p)); } catch { /* Keep the live draw. */ }
        }
        if (root.dataset.logicMode !== mode) root.dataset.logicMode = mode;
        if (root.dataset.logicPalette !== palette) root.dataset.logicPalette = palette;
        if (hero) {
            if (root.dataset.logicHeroTone !== hero.tone) root.dataset.logicHeroTone = hero.tone;
            for (const [role, value] of Object.entries(hero.values))
                if (root.style.getPropertyValue(role) !== value) root.style.setProperty(role, value);
        }
    }

    window.logicAppearance = Object.freeze({
        modes,
        get mode() { return mode; },
        select(next) {
            if (!modes.includes(next)) return;
            mode = next;
            apply();
            try { localStorage.setItem(key, mode); } catch { /* Keep the live choice. */ }
        },
    });
    apply();
    systemDark?.addEventListener('change', () => { if (mode === 'Auto') apply(); });

    // Blazor's enhanced document merge synchronizes <html> attributes. Restore
    // just our values in the mutation microtask, before rendering, with no reread or reroll.
    if (document.currentScript?.hasAttribute('data-logic-appearance-enhanced')) {
        new MutationObserver(apply).observe(root, {
            attributeFilter: ['data-logic-mode', 'data-logic-palette', 'data-logic-hero-tone', 'style'],
        });
    }
})();
