// Mention autocomplete for composer textareas: detects "@query" before the caret,
// reports it to .NET, and replaces it with a {@key} token when a result is chosen.
window.socialPoster = {
    attachAll(ids, dotnet) {
        for (const id of ids) this.attach(document.getElementById(id), dotnet);
    },

    attach(el, dotnet) {
        if (!el || el._sp) return;
        const state = { open: false, lastQuery: null };
        el._sp = state;
        const re = /(^|[\s(\[])@([\w.\-]{1,64})$/;

        const report = (q) => {
            if (q === state.lastQuery) return;
            state.lastQuery = q;
            dotnet.invokeMethodAsync('OnMentionQuery', el.id, q);
        };
        const check = () => {
            const before = el.value.slice(0, el.selectionStart);
            const m = before.match(re);
            report(m ? m[2] : null);
        };

        el.addEventListener('input', check);
        el.addEventListener('click', check);
        el.addEventListener('keyup', e => {
            if (['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(e.key)) check();
        });
        el.addEventListener('keydown', e => {
            if (!state.open) return;
            if (['ArrowDown', 'ArrowUp', 'Enter', 'Tab', 'Escape'].includes(e.key)) {
                e.preventDefault();
                dotnet.invokeMethodAsync('OnMentionKey', el.id, e.key);
            }
        });
        el.addEventListener('blur', () => setTimeout(() => {
            if (document.activeElement !== el) report(null);
        }, 150));
    },

    setOpen(id, open) {
        const el = document.getElementById(id);
        if (el && el._sp) el._sp.open = open;
    },

    insertMention(id, key) {
        const el = document.getElementById(id);
        if (!el) return;
        const pos = el.selectionStart;
        const before = el.value.slice(0, pos);
        const m = before.match(/@[\w.\-]*$/);
        const start = m ? pos - m[0].length : pos;
        const token = `{@${key}} `;
        el.value = el.value.slice(0, start) + token + el.value.slice(pos);
        const caret = start + token.length;
        el.focus();
        el.setSelectionRange(caret, caret);
        if (el._sp) { el._sp.lastQuery = null; el._sp.open = false; }
        el.dispatchEvent(new Event('input', { bubbles: true }));
    },

    copy(text) {
        return navigator.clipboard.writeText(text);
    },

    /** @returns {boolean} false if the browser's pop-up blocker stopped it. */
    openInNewTab(url) {
        const w = window.open(url, '_blank', 'noopener');
        return !!w;
    },

    saveDraft(json) {
        try { localStorage.setItem('socialPoster.draft', json); } catch { }
    },

    loadDraft() {
        try { return localStorage.getItem('socialPoster.draft'); } catch { return null; }
    },

    getTheme() {
        try { return localStorage.getItem('socialPoster.theme'); } catch { return null; }
    },

    setTheme(value) {
        try { localStorage.setItem('socialPoster.theme', value); } catch { }
    },

    scrollToTop() {
        window.scrollTo({ top: 0, behavior: 'auto' });
    }
};
