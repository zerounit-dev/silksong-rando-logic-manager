(() => {
    if (window.logicAppearance) return;

    const key = 'silksong-rando-logic.appearance';
    const brightnessKey = `${key}.hero-brightness`;
    const colorKey = `${key}.custom-color`;
    const validColor = color => typeof color === 'string' && color.length === 7 && /^#[0-9a-f]{6}$/.test(color);
    const script = document.currentScript;
    if (script?.hasAttribute('data-logic-login-destination')) {
        const { logicLoginColor, logicLoginDestination } = script.dataset;
        if (validColor(logicLoginColor)) {
            try { sessionStorage.setItem(colorKey, logicLoginColor); } catch { /* Continue sign-in with available storage. */ }
        }
        // The native POST supplied its validated destination. Do not resolve a
        // transient palette here: only the destination may take a Hero step.
        window.location.replace(logicLoginDestination);
        return;
    }

    const root = document.documentElement;
    const minBrightness = .22;
    const maxBrightness = .97;
    const modes = Object.freeze(['Auto', 'Light', 'Dark', 'Void', 'Hornet', 'Hero', 'Custom']);
    const systemDark = window.matchMedia?.('(prefers-color-scheme: dark)');
    let mode = 'Auto';
    let pathname = window.location.pathname;
    let startupHistory = script?.hasAttribute('data-logic-appearance-startup-history');
    let hero, custom, preview;
    try {
        const saved = localStorage.getItem(key);
        if (modes.includes(saved)) mode = saved;
    } catch { /* This origin may not have readable storage. */ }
    try {
        const saved = sessionStorage.getItem(colorKey);
        if (validColor(saved)) custom = { color: saved };
    } catch { /* Use Auto if the selected Custom color cannot be read. */ }

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

    function tintChannels(y, angle, strength) {
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
        return rgb;
    }

    const cssColor = rgb => `color(srgb-linear ${rgb.map(channel => channel.toFixed(8)).join(' ')})`;
    const tint = (y, angle, strength) => cssColor(tintChannels(y, angle, strength));

    // Compare only colored members of the same status family. Contrast comes
    // first; once readable, favor chromatic separation rather than more white/black.
    const statusShades = {
        danger: ['#ff929c', '#ff455e', '#aa1236', '#700023'],
        warning: ['#f6d64e', '#ffb51e', '#9a5800', '#633900'],
        success: ['#70ec93', '#22c971', '#057443', '#004c29'],
        info: ['#b9edff', '#31c9ed', '#06638a', '#002b36'],
    };
    const luminance = rgb => rgb.reduce((sum, c, i) => sum + c * weights[i], 0);
    const shadeRgb = shade => [1, 3, 5].map(i => parseInt(shade.slice(i, i + 2), 16) / 255);
    const shadeLinearRgb = rgb => rgb.map(c => c <= .04045 ? c / 12.92 : ((c + .055) / 1.055) ** 2.4);
    const shadeLuminance = rgb => luminance(shadeLinearRgb(rgb));
    function shadeHue(shade) {
        // Linear sRGB -> cube-root LMS -> OKLab hue (radians, as used by tint).
        const [r, g, b] = shadeLinearRgb(shadeRgb(shade));
        const l = Math.cbrt(.4122214708 * r + .5363325363 * g + .0514459929 * b);
        const m = Math.cbrt(.2119034982 * r + .6806995451 * g + .1073969566 * b);
        const s = Math.cbrt(.0883024619 * r + .2817188376 * g + .6299787005 * b);
        return Math.atan2(.0259040371 * l + .7827717662 * m - .8086757660 * s,
            1.9779984951 * l - 2.4285922050 * m + .4505937099 * s);
    }
    const contrast = (a, b) => (Math.max(a, b) + .05) / (Math.min(a, b) + .05);
    function statusInk(shades, ...fills) {
        const backgrounds = fills.map(fill => ({
            y: luminance(fill),
            bg: fill.map(c => c <= .0031308 ? 12.92 * c : 1.055 * c ** (1 / 2.4) - .055),
        }));
        let best, bestScore = -Infinity;
        for (const shade of shades) {
            const rgb = shadeRgb(shade);
            const inkY = shadeLuminance(rgb);
            // Maximize the weakest pairing when the same ink serves several fills.
            const score = Math.min(...backgrounds.map(({ y, bg }) => {
                // Opponent-channel distance ignores a uniform light/dark shift.
                const [r, g, b] = rgb.map((c, i) => c - bg[i]);
                return Math.min(contrast(y, inkY), 5) + .5 * Math.hypot(r - g, b - g);
            }));
            if (score > bestScore) { bestScore = score; best = shade; }
        }
        return best;
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
        const statusFills = [];
        for (const [role, [offset, chroma]] of fills) {
            const rgb = tintChannels((p - offset * towardSplit) ** 3, angle, chroma);
            values[`--hero-${role}`] = cssColor(rgb);
            if (role === 'canvas' || role === 'sidebar' || role === 'selection') statusFills.push(rgb);
        }

        const nearest = (p - towardSplit) ** 3;
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
        // retain their semantic hue. Standalone chromatic inks are set in CSS.
        for (const [role, degrees] of Object.entries({ danger: 12, warning: 52, success: 140, info: 195 })) {
            const common = statusInk(statusShades[role], ...statusFills);
            const diagnosticHue = degrees * Math.PI / 180;
            const warning = role === 'warning';
            const h = warning ? shadeHue(common) : diagnosticHue;
            const baseY = (p - .64 * towardSplit) ** 3;
            const hoverY = (p - .82 * towardSplit) ** 3;
            values[`--hero-${role}`] = common;
            // Subdued warning fills: even before gamut reduction C <= .16 * .36.
            values[`--hero-${role}-bg`] = tint(baseY, h, warning ? .30 : .88);
            values[`--hero-${role}-hover`] = tint(hoverY, h, warning ? .36 : .98);
            if (role !== 'info') {
                const inkY = shadeLuminance(shadeRgb(common));
                const contrastInk = ink(4.55, .75, .037, .90, h);
                const commonOnBase = contrast(inkY, baseY) >= 4.5;
                values[`--hero-${role}-on-fill`] = commonOnBase ? common : contrastInk;
                values[`--hero-${role}-on-hover-fill`] = contrast(inkY, hoverY) >= 4.5 ? common : contrastInk;
                // The enabled scene-alignment glyph sits on the fixed viewer, not the Hero canvas.
                if (role === 'success')
                    values['--hero-success-on-viewer'] = contrast(inkY, shadeLuminance(shadeRgb('#171717'))) >= 3
                        ? common : statusShades.success[0];
                if (role !== 'success') {
                    const diagnosticInk = warning ? ink(4.55, .75, .037, .90, diagnosticHue) : contrastInk;
                    values[`--hero-${role}-diagnostic-ink`] = diagnosticInk;
                    if (warning) values['--hero-warning-caret'] = commonOnBase ? common : diagnosticInk;
                }
            }
        }
        return { tone: dark ? 'Dark' : 'Light', values };
    }

    function makeCustom(color) {
        const rgb = shadeRgb(color);
        const y = shadeLuminance(rgb);
        const dark = y < .179;
        const angle = shadeHue(color);
        const strength = Math.max(...rgb) - Math.min(...rgb);
        // Give seed brightness room to vary within each polarity, then reserve
        // contrast for text (including syntax) all the way through selection.
        const base = dark ? .008 + .042 * y / .179 : .64 + .29 * (y - .179) / .821;
        const surfaceY = offset => base + (dark ? .04 : -.07) * offset;
        const values = {};
        for (const [role, offset, chroma] of [
            ['canvas', 0, .47], ['sidebar', .16, .53], ['surface', .29, .50],
            ['raised', .44, .55], ['muted-surface', .56, .60], ['hover-subtle', .68, .70],
            ['hover', .82, .79], ['selection', 1, .90],
        ]) values[`--custom-${role}`] = tint(surfaceY(offset), angle, chroma * strength);
        for (const [role, lightInk, darkInk, chroma] of [
            ['ink', .94, .013, .13], ['muted', .66, .061, .25], ['secondary', .79, .039, .33],
            ['disabled', .57, .080, .26], ['line', .40, .144, .35], ['boundary', .57, .092, .66],
            ['focus', .86, .030, .90], ['primary', .69, .061, .97], ['primary-hover', .91, .028, 1],
        ]) values[`--custom-${role}`] = tint(dark ? lightInk : darkInk, angle, chroma * strength);

        for (const [role, degrees] of Object.entries({ danger: 15, warning: 67, success: 148, info: 242 })) {
            const hue = degrees * Math.PI / 180;
            values[`--custom-${role}`] = tint(dark ? .72 : .055, hue, .85);
            values[`--custom-${role}-bg`] = tint(surfaceY(.56), hue, .79);
            values[`--custom-${role}-hover`] = tint(surfaceY(.82), hue, .92);
        }
        values['--custom-on-primary'] = dark ? '#000' : '#fff';
        return { tone: dark ? 'Dark' : 'Light', values };
    }

    function drawHero() {
        const hue = Math.random();
        const p = nextBrightness();
        hero = makeHero(p, hue);
        try { sessionStorage.setItem(brightnessKey, String(p)); } catch { /* Keep the live draw. */ }
    }

    function apply() {
        const active = preview ?? (mode === 'Custom' ? custom : undefined);
        const automatic = systemDark?.matches ? 'Dark' : 'Light';
        const palette = active ? 'Custom' : mode === 'Auto' || mode === 'Custom' ? automatic : mode;
        if (root.dataset.logicMode !== mode) root.dataset.logicMode = mode;
        if (root.dataset.logicPalette !== palette) root.dataset.logicPalette = palette;
        if (hero) {
            if (root.dataset.logicHeroTone !== hero.tone) root.dataset.logicHeroTone = hero.tone;
            for (const [role, value] of Object.entries(hero.values))
                if (root.style.getPropertyValue(role) !== value) root.style.setProperty(role, value);
        }
        if (active) {
            const { tone, values } = active.palette ??= makeCustom(active.color);
            if (root.dataset.logicCustomTone !== tone) root.dataset.logicCustomTone = tone;
            for (const [role, value] of Object.entries(values))
                if (root.style.getPropertyValue(role) !== value) root.style.setProperty(role, value);
        }
    }

    function pathnameChanged() {
        const next = window.location.pathname;
        if (next === pathname) return;
        pathname = next;
        if (mode !== 'Hero') return;
        drawHero();
        apply();
    }

    function observeStartupHistory() {
        if (!startupHistory) return;
        try { pathnameChanged(); } catch { /* Appearance cannot affect history traversal. */ }
    }

    window.logicAppearance = Object.freeze({
        modes,
        get mode() { return mode; },
        get savedColor() { return custom?.color; },
        pathnameChanged,
        finishStartupHistory() {
            if (!startupHistory) return;
            startupHistory = false;
            window.removeEventListener('popstate', observeStartupHistory);
            pathnameChanged();
        },
        select(next) {
            if (!modes.includes(next) || next === 'Custom' || next === mode) return;
            if (next === 'Hero') drawHero();
            mode = next;
            apply();
            try { localStorage.setItem(key, mode); } catch { /* Keep the live choice. */ }
        },
        preview(color) {
            if (!validColor(color) || preview?.color === color) return;
            preview = { color };
            apply();
        },
        cancelPreview() {
            if (!preview) return;
            preview = undefined;
            apply();
        },
        saveCustom(color) {
            if (!validColor(color)) throw new TypeError('Invalid custom color');
            custom = preview?.color === color ? preview : { color };
            preview = undefined;
            mode = 'Custom';
            apply();
            let colorStored = false, modeStored = false;
            try { sessionStorage.setItem(colorKey, color); colorStored = true; } catch { /* Live color is committed. */ }
            try { localStorage.setItem(key, mode); modeStored = true; } catch { /* Live mode is committed. */ }
            return { colorStored, modeStored };
        },
    });
    if (mode === 'Hero') drawHero();
    apply();
    if (startupHistory) window.addEventListener('popstate', observeStartupHistory, { passive: true });
    systemDark?.addEventListener('change', apply);

    // Blazor's enhanced document merge synchronizes <html> attributes. Restore
    // just our values in the mutation microtask, before rendering, with no reread or reroll.
    if (script?.hasAttribute('data-logic-appearance-enhanced')) {
        new MutationObserver(apply).observe(root, {
            attributeFilter: ['data-logic-mode', 'data-logic-palette', 'data-logic-hero-tone', 'data-logic-custom-tone', 'style'],
        });
    }
})();
