// Presentation only. The .NET timeline retains the original UTC instants and order.
export function formatHistoryTimes(utcMilliseconds) {
    const locale = navigator.languages.length ? navigator.languages : undefined;
    const timeZone = new Intl.DateTimeFormat().resolvedOptions().timeZone;
    const day = new Intl.DateTimeFormat('en-US-u-ca-gregory-nu-latn', { timeZone, year: 'numeric', month: '2-digit', day: '2-digit', era: 'short' });
    const year = new Intl.DateTimeFormat(locale, { timeZone, year: 'numeric' });
    const date = new Intl.DateTimeFormat(locale, { timeZone, month: 'short', day: 'numeric' });
    const datedYear = new Intl.DateTimeFormat(locale, { timeZone, year: 'numeric', month: 'short', day: 'numeric' });
    const time = new Intl.DateTimeFormat(locale, { timeZone, hour: 'numeric', minute: '2-digit' });
    const full = new Intl.DateTimeFormat(locale, { timeZone, year: 'numeric', month: 'short', day: 'numeric',
        hour: 'numeric', minute: '2-digit', second: '2-digit', timeZoneName: 'longOffset' });
    const now = Date.now();
    const today = day.format(now), currentYear = year.format(now);
    return utcMilliseconds.map(instant => {
        const dayKey = day.format(instant);
        return { dayKey, isToday: dayKey === today, date: (year.format(instant) === currentYear ? date : datedYear).format(instant),
            time: time.format(instant), full: full.format(instant) };
    });
}
