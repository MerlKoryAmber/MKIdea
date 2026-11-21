// Сеансы через guacd в браузере: экран, клавиатура, мышь, буфер обмена; проигрывание записей.
// Модуль грузит Blazor (IJSRuntime → import). Пароли сюда не приходят — только номер пропуска.
import Guacamole from './lib/guacamole/guacamole-common.min.js';

const sessions = new Map();

export function open(elId, ticket, dotnet, protocol) {
    close(elId);
    const host = document.getElementById(elId);
    if (!host) return;
    host.innerHTML = '';
    host.tabIndex = 0;

    const tunnel = new Guacamole.WebSocketTunnel('guac/tunnel');
    const client = new Guacamole.Client(tunnel);
    const display = client.getDisplay();
    const view = display.getElement();
    host.appendChild(view);

    const size = () => ({ w: Math.max(200, host.clientWidth | 0), h: Math.max(150, host.clientHeight | 0) });
    const s0 = size();
    const tz = Intl.DateTimeFormat().resolvedOptions().timeZone || '';
    const dpi = Math.round(96 * (window.devicePixelRatio || 1));

    client.onstatechange = st => { try { dotnet.invokeMethodAsync('OnGuacState', st); } catch { } };
    client.onerror = status => { try { dotnet.invokeMethodAsync('OnGuacError', status && status.message ? status.message : 'ошибка сеанса'); } catch { } };

    // мышь
    const mouse = new Guacamole.Mouse(view);
    const sendMouse = e => client.sendMouseState(e.state, true);
    if (mouse.onEach) mouse.onEach(['mousedown', 'mousemove', 'mouseup'], sendMouse);
    else mouse.onmousedown = mouse.onmouseup = mouse.onmousemove = st => client.sendMouseState(st);
    view.addEventListener('mousedown', () => host.focus());

    // клавиатура — только когда фокус на экране сеанса, остальная страница печатает как обычно
    const keyboard = new Guacamole.Keyboard(host);
    // Ctrl+V, Ctrl+Shift+V, Shift+Insert — не на хост, а браузеру: он пришлёт событие paste с буфером ПК
    const isPaste = k => ((k === 0x76 || k === 0x56) && keyboard.modifiers.ctrl) || (k === 0xFF63 && keyboard.modifiers.shift);
    let pasteFallback = 0;
    keyboard.onkeydown = k => {
        if (isPaste(k)) {
            // запасной путь: браузер не прислал paste — читаем буфер сами (спросит разрешение)
            clearTimeout(pasteFallback);
            pasteFallback = setTimeout(() => {
                if (navigator.clipboard && navigator.clipboard.readText) navigator.clipboard.readText().then(sendPaste).catch(() => { });
            }, 200);
            return true;
        }
        client.sendKeyEvent(1, k);
        return false;
    };
    keyboard.onkeyup = k => client.sendKeyEvent(0, k);

    // буфер обмена: вставка (Ctrl+V/Shift+Insert в браузере) → в сеанс; выделенное в сеансе → в буфер ПК
    const onPaste = e => {
        // событие paste браузер шлёт фокусу или body — ловим на document, но только когда фокус в этом сеансе
        if (document.activeElement !== host) return;
        clearTimeout(pasteFallback);
        const text = e.clipboardData ? e.clipboardData.getData('text/plain') : '';
        if (!text) return;
        e.preventDefault();
        sendPaste(text);
    };
    const sendPaste = text => {
        if (!text) return;
        const stream = client.createClipboardStream('text/plain');
        const writer = new Guacamole.StringWriter(stream);
        writer.sendText(text);
        writer.sendEnd();
        // буфер уже у хоста; вставить: терминал guacd — Ctrl+Shift+V, Windows (RDP) — Ctrl+V
        const CTRL = 0xFFE3, SHIFT = 0xFFE1;
        const seq = protocol === 'rdp'
            ? [[1, CTRL], [1, 0x76], [0, 0x76], [0, CTRL]]
            : [[1, CTRL], [1, SHIFT], [1, 0x56], [0, 0x56], [0, SHIFT], [0, CTRL]];
        seq.forEach(([d, k]) => client.sendKeyEvent(d, k));
        // клавиши, которые пользователь ещё держит, — снова нажаты
        if (keyboard.modifiers.ctrl) client.sendKeyEvent(1, CTRL);
        if (keyboard.modifiers.shift) client.sendKeyEvent(1, SHIFT);
    };
    document.addEventListener('paste', onPaste);
    client.onclipboard = (stream, mimetype) => {
        if (!/^text\//.test(mimetype)) return;
        const reader = new Guacamole.StringReader(stream);
        let data = '';
        reader.ontext = t => data += t;
        reader.onend = () => { if (navigator.clipboard && data) navigator.clipboard.writeText(data).catch(() => { }); };
    };

    // экран целиком в окне: при масштабе Windows > 100 % guacd делает рабочий стол RDP крупнее окна (размер × dpi / 96)
    const fit = () => {
        const w = display.getWidth(), h = display.getHeight();
        if (w && h) display.scale(Math.min(host.clientWidth / w, host.clientHeight / h, 1));
    };
    display.onresize = fit;

    // размер экрана — за окном
    let timer = 0;
    const ro = new ResizeObserver(() => {
        fit();
        clearTimeout(timer);
        timer = setTimeout(() => { const s = size(); client.sendSize(s.w, s.h); }, 150);
    });
    ro.observe(host);

    sessions.set(elId, { client, keyboard, ro, host, onPaste });
    client.connect(`t=${encodeURIComponent(ticket)}&w=${s0.w}&h=${s0.h}&dpi=${dpi}&tz=${encodeURIComponent(tz)}`);
    host.focus();
}

export function focus(elId) { const s = sessions.get(elId); if (s) s.host.focus(); }

export function close(elId) {
    const s = sessions.get(elId);
    if (!s) return;
    sessions.delete(elId);
    try { s.ro.disconnect(); } catch { }
    try { s.keyboard.onkeydown = s.keyboard.onkeyup = null; s.keyboard.reset(); } catch { }
    try { document.removeEventListener('paste', s.onPaste); } catch { }
    try { s.client.disconnect(); } catch { }
    s.host.innerHTML = '';
}

// ---------- проигрывание записи ----------
const players = new Map();

export function play(elId, url, dotnet) {
    stop(elId);
    const host = document.getElementById(elId);
    if (!host) return;
    host.innerHTML = '';
    const rec = new Guacamole.SessionRecording(new Guacamole.StaticHTTPTunnel(url));
    const display = rec.getDisplay();
    const view = display.getElement();
    host.appendChild(view);
    const fit = () => {
        const w = display.getWidth(), h = display.getHeight();
        if (w && h) display.scale(Math.min(host.clientWidth / w, host.clientHeight / h, 1));
    };
    display.onresize = fit;
    const ro = new ResizeObserver(fit);
    ro.observe(host);
    const report = () => { try { dotnet.invokeMethodAsync('OnRecProgress', rec.getPosition(), rec.getDuration(), rec.isPlaying()); } catch { } };
    rec.onprogress = () => report();
    rec.onload = () => { report(); rec.play(); };
    rec.onplay = report;
    rec.onpause = report;
    rec.onseek = report;
    rec.onerror = msg => { try { dotnet.invokeMethodAsync('OnGuacError', 'запись: ' + (msg || 'не прочитана')); } catch { } };
    const tick = setInterval(() => { if (rec.isPlaying()) report(); }, 500);
    players.set(elId, { rec, ro, host, tick });
    rec.connect();
}

export function playPause(elId) {
    const p = players.get(elId);
    if (!p) return;
    if (p.rec.isPlaying()) p.rec.pause(); else p.rec.play();
}

export function seek(elId, ms) { const p = players.get(elId); if (p) p.rec.seek(ms); }

export function stop(elId) {
    const p = players.get(elId);
    if (!p) return;
    players.delete(elId);
    clearInterval(p.tick);
    try { p.ro.disconnect(); } catch { }
    try { p.rec.pause(); p.rec.disconnect(); } catch { }
    p.host.innerHTML = '';
}
