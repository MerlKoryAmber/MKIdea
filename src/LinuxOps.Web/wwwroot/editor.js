// Files → правка текста (0.7.9). Текст в .NET уходит потоком (IJSStreamReference) — сообщение SignalR ограничено 32 КБ.
const unloads = new Map();
export function init(id, dotnet) {
    const el = document.getElementById(id);
    if (!el) return;
    el._dirty = false;
    el.addEventListener('input', () => {
        if (el._dirty) return;
        el._dirty = true;
        dotnet.invokeMethodAsync('Dirty').catch(() => { });
    });
    el.addEventListener('keydown', e => {
        if (e.key === 'Tab' && !e.shiftKey && !e.ctrlKey && !e.altKey && !el.readOnly) {
            e.preventDefault();
            // insertText сохраняет историю отмены (Ctrl+Z); нет — вставка напрямую
            if (!document.execCommand || !document.execCommand('insertText', false, '\t')) {
                el.setRangeText('\t', el.selectionStart, el.selectionEnd, 'end');
                el.dispatchEvent(new Event('input'));
            }
        }
    });
    // Ctrl+S — во всём окне правки (фокус может быть на кнопке)
    const win = el.closest('.editor-window') || el;
    win.addEventListener('keydown', e => {
        if ((e.ctrlKey || e.metaKey) && e.code === 'KeyS') {
            e.preventDefault();
            dotnet.invokeMethodAsync('SaveKey').catch(() => { });
        }
    });
    // закрыть вкладку браузера или F5 с несохранённой правкой — вопрос браузера
    const unload = e => { if (el._dirty && document.body.contains(el)) { e.preventDefault(); e.returnValue = ''; } };
    unloads.set(id, unload);
    window.addEventListener('beforeunload', unload);
    el.focus();
    el.setSelectionRange(0, 0);
    el.scrollTop = 0;
}

// снимок для сохранения: clean сбросит «несохранённое», только если после снимка ничего не дописали
export function text(id) {
    const el = document.getElementById(id);
    if (!el) return new Uint8Array(0);
    el._snap = el.value;
    return new TextEncoder().encode(el._snap);
}

export function dirty(id) {
    const el = document.getElementById(id);
    return !!(el && el._dirty);
}

// true — остались несохранённые изменения (дописали во время записи)
export function clean(id) {
    const el = document.getElementById(id);
    if (!el) return false;
    el._dirty = el.value !== el._snap;
    return el._dirty;
}

export function dispose(id) {
    const u = unloads.get(id);
    if (u) { window.removeEventListener('beforeunload', u); unloads.delete(id); }
}
