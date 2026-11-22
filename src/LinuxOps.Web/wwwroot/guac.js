// Сеансы через guacd в браузере: экран, клавиатура, мышь, буфер обмена; проигрывание записей.
// Модуль грузит Blazor (IJSRuntime → import). Пароли сюда не приходят — только номер пропуска.
import Guacamole from './lib/guacamole/guacamole-common.min.js';

const sessions = new Map();

export function open(elId, ticket, dotnet, protocol, viewOnly) {
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
    let last = s0;
    const tz = Intl.DateTimeFormat().resolvedOptions().timeZone || '';
    const dpi = Math.round(96 * (window.devicePixelRatio || 1));

    client.onstatechange = st => { try { dotnet.invokeMethodAsync('OnGuacState', st); } catch { } };
    client.onerror = status => { try { dotnet.invokeMethodAsync('OnGuacError', status && status.message ? status.message : 'ошибка сеанса'); } catch { } };

    // просмотр вживую: только экран — без мыши, клавиатуры, буфера и смены размера (сервис их и так не пропустит)
    if (viewOnly) {
        const fitView = () => {
            const w = display.getWidth(), h = display.getHeight();
            if (w && h && host.clientWidth && host.clientHeight) display.scale(Math.min(host.clientWidth / w, host.clientHeight / h, 1));
        };
        display.onresize = fitView;
        const roView = new ResizeObserver(fitView);
        roView.observe(host);
        sessions.set(elId, { client, keyboard: null, ro: roView, host, onPaste: null });
        client.connect(`t=${encodeURIComponent(ticket)}&w=${s0.w}&h=${s0.h}&dpi=${dpi}&tz=${encodeURIComponent(tz)}`);
        return;
    }

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
        if (w && h && host.clientWidth && host.clientHeight) display.scale(Math.min(host.clientWidth / w, host.clientHeight / h, 1));
    };
    display.onresize = fit;

    // размер экрана — за окном
    let timer = 0;
    const ro = new ResizeObserver(() => {
        fit();
        clearTimeout(timer);
        // спрятанный экран (другая вкладка сеанса или шапки) — размер 0: не трогать сеанс, при возврате подгонится
        timer = setTimeout(() => {
            if (!host.clientWidth || !host.clientHeight) return;
            const s = size();
            if (s.w === last.w && s.h === last.h) return;
            last = s;
            client.sendSize(s.w, s.h);
        }, 150);
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
    try { if (s.keyboard) { s.keyboard.onkeydown = s.keyboard.onkeyup = null; s.keyboard.reset(); } } catch { }
    try { if (s.onPaste) document.removeEventListener('paste', s.onPaste); } catch { }
    // закрываем сами — окну уже не сообщать «отключено» (иначе запоздалое событие старого соединения попадёт в новое)
    try { s.client.onstatechange = null; s.client.onerror = null; s.client.disconnect(); } catch { }
    s.host.innerHTML = '';
}

// ---------- проигрывание записи ----------
const players = new Map();

// Своё время проигрывания: SessionRecording 1.5 играет только 1×. Таймер двигает позицию через seek
// (вперёд seek досчитывает кадры от текущего — дёшево); скорость 1–8×; «пропускать простой» — паузы
// длиннее 5 с (список с сервера, по sync в записи) проматываются, остаётся 1 с.
const SKIP_KEY = 'lo.rec.skipIdle';
const TICK = 100;

export function play(elId, url, dotnet, wrapId) {
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
        if (w && h && host.clientWidth && host.clientHeight) display.scale(Math.min(host.clientWidth / w, host.clientHeight / h));
    };
    display.onresize = fit;
    const ro = new ResizeObserver(fit);
    ro.observe(host);

    let skip = false;
    try { skip = localStorage.getItem(SKIP_KEY) === '1'; } catch { }
    const p = { rec, ro, host, tick: 0, playing: false, loaded: false, seeking: false, pos: 0, speed: 1, skip, gaps: [], wrapId, onKey: null };
    // позиция — целые мс: дробное число .NET (long) не примет, и отчёт пропадёт молча
    const report = () => { try { dotnet.invokeMethodAsync('OnRecProgress', Math.round(p.pos), Math.round(rec.getDuration()), p.playing, p.speed, p.skip).catch(() => { }); } catch { } };
    p.report = report;
    rec.onprogress = () => report();
    rec.onload = () => { p.loaded = true; p.playing = true; report(); };
    rec.onerror = msg => {
        // 404 — файла нет: удалён по сроку или лежит в хранилище, которое сейчас не подключено
        const m = /not found|404/i.test(msg || '') ? 'файла нет — удалена по сроку или лежит в отключённом хранилище (⚙ → Сеансы и записи)' : (msg || 'не прочитана');
        try { dotnet.invokeMethodAsync('OnGuacError', 'запись: ' + m); } catch { }
    };

    // паузы записи — для пропуска простоя
    fetch(url + (url.includes('?') ? '&' : '?') + 'kind=gaps', { credentials: 'same-origin', cache: 'no-store' })
        .then(r => r.ok ? r.json() : []).then(g => { p.gaps = Array.isArray(g) ? g : []; }).catch(() => { });

    let last = performance.now();
    p.tick = setInterval(() => {
        const now = performance.now();
        const dt = now - last;
        last = now;
        if (!p.playing || !p.loaded) return;
        const dur = rec.getDuration();
        let next = p.pos + dt * p.speed;
        if (p.skip) {
            const g = p.gaps.find(([a, b]) => next > a + 1000 && next < b);
            if (g) next = g[1];
        }
        if (next >= dur) { next = dur; p.playing = false; }
        p.pos = next;
        // seek не ждём: в 1.5 его обратный вызов не приходит, если кадр не сменился; новый seek отменяет недоделанный
        if (!p.seeking || now - p.seekAt > 300) {
            p.seeking = true;
            p.seekAt = now;
            rec.seek(next, () => { p.seeking = false; });
        }
        if (now - (p.reportAt || 0) > 250 || !p.playing) { p.reportAt = now; report(); }
    }, TICK);

    // клавиши, когда фокус в проигрывателе: пробел — пауза, F — на весь экран
    const wrap = document.getElementById(wrapId) || host;
    wrap.tabIndex = 0;
    p.onKey = e => {
        if (e.target && /^(INPUT|SELECT|TEXTAREA)$/.test(e.target.tagName)) return;
        if (e.code === 'Space') { e.preventDefault(); playPause(elId); }
        else if (e.code === 'KeyF') { e.preventDefault(); fullscreen(wrapId); }
    };
    wrap.addEventListener('keydown', p.onKey);
    p.wrap = wrap;
    players.set(elId, p);
    rec.connect();
}

// явная пауза (уход с вкладки): не переключатель — состояние в окне может отставать от плеера
export function pause(elId) {
    const p = players.get(elId);
    if (!p || !p.playing) return;
    p.playing = false;
    p.report();
}

export function playPause(elId) {
    const p = players.get(elId);
    if (!p || !p.loaded) return;
    // в конце — сначала
    if (!p.playing && p.pos >= p.rec.getDuration()) { p.pos = 0; p.rec.seek(0); }
    p.playing = !p.playing;
    p.report();
}

export function seek(elId, ms) {
    const p = players.get(elId);
    if (!p) return;
    p.pos = Number(ms) || 0;
    p.seeking = true;
    p.seekAt = performance.now();
    p.rec.seek(p.pos, () => { p.seeking = false; });
    p.report();
}

export function setSpeed(elId, speed) {
    const p = players.get(elId);
    if (!p) return;
    p.speed = Math.min(8, Math.max(1, Number(speed) || 1));
    p.report();
}

export function setSkip(elId, on) {
    const p = players.get(elId);
    if (!p) return;
    p.skip = !!on;
    try { localStorage.setItem(SKIP_KEY, p.skip ? '1' : '0'); } catch { }
    p.report();
}

/** Сохранённая галка «пропускать простой» — для отрисовки до загрузки записи. */
export function skipSaved() { try { return localStorage.getItem(SKIP_KEY) === '1'; } catch { return false; } }

// На весь экран: полноэкранный режим браузера; где он запрещён (встроенные окна, политика) — на всё окно браузера.
// Esc выходит из обоих.
export function fullscreen(wrapId) {
    const el = document.getElementById(wrapId);
    if (!el) return;
    if (document.fullscreenElement) { document.exitFullscreen().catch(() => { }); return; }
    if (el.classList.contains('rec-max')) { el.classList.remove('rec-max'); return; }
    const maximize = () => {
        el.classList.add('rec-max');
        el.focus();
        const esc = e => { if (e.key === 'Escape') { el.classList.remove('rec-max'); document.removeEventListener('keydown', esc); } };
        document.addEventListener('keydown', esc);
    };
    if (!document.fullscreenEnabled || !el.requestFullscreen) { maximize(); return; }
    // некоторые встроенные окна не отвечают на запрос вовсе — не включилось за 0,7 с, разворачиваем на окно
    let done = false;
    const fallback = setTimeout(() => { if (!done && !document.fullscreenElement) { done = true; maximize(); } }, 700);
    el.requestFullscreen().then(() => { done = true; clearTimeout(fallback); el.focus(); })
        .catch(() => { clearTimeout(fallback); if (!done) { done = true; maximize(); } });
}

export function stop(elId) {
    const p = players.get(elId);
    if (!p) return;
    players.delete(elId);
    clearInterval(p.tick);
    try { p.ro.disconnect(); } catch { }
    try { if (p.wrap && p.onKey) p.wrap.removeEventListener('keydown', p.onKey); } catch { }
    try { p.rec.pause(); p.rec.disconnect(); } catch { }
    if (document.fullscreenElement && p.wrap && p.wrap.contains(document.fullscreenElement) || document.fullscreenElement === p.wrap) document.exitFullscreen().catch(() => { });
    p.host.innerHTML = '';
}

// Кнопка «Весь экран» вызывает это прямо из щелчка (onclick, не через сервер Blazor):
// браузер пускает в полноэкранный режим только из действия пользователя.
window.loRecFullscreen = fullscreen;
