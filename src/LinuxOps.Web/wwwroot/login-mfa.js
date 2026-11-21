// Страница входа, шаг MK 2FA: опрос статуса; просят код — показать поле; подтвердили — на «/».
// Ушёл со страницы, не закончив, — запрос в MK 2FA отменяется (sendBeacon).
(() => {
  const root = document.getElementById('mfa');
  if (!root) return;
  const token = root.querySelector('input[name=__RequestVerificationToken]')?.value || '';
  const push = document.getElementById('mfa-push');
  const codeForm = document.getElementById('mfa-code');
  const msg = document.getElementById('mfa-msg');
  const again = document.getElementById('mfa-again');
  let leaving = false, timer = 0;

  const body = () => { const f = new FormData(); f.append('__RequestVerificationToken', token); return f; };
  const say = (t) => { msg.textContent = t || ''; msg.hidden = !t; };

  async function poll() {
    let d;
    try {
      const r = await fetch('/auth/mfa/poll', { method: 'POST', body: body(), credentials: 'same-origin' });
      d = await r.json();
    } catch { d = { state: 'push', message: 'Нет связи с сервером, повторяю…', poll: 3000 }; }
    if (d.state === 'done') { leaving = true; location.replace('/'); return; }
    if (d.state === 'fail') {
      leaving = true;
      push.hidden = true; codeForm.hidden = true; again.hidden = false;
      document.getElementById('mfa-cancel').hidden = true;
      say(d.message);
      return;
    }
    if (d.state === 'code' && codeForm.hidden) {
      codeForm.hidden = false; push.hidden = true;
      codeForm.querySelector('input[name=code]')?.focus();
    }
    if (d.message || !msg.dataset.keep) say(d.message);
    timer = setTimeout(poll, d.poll || 2000);
  }

  root.querySelectorAll('form').forEach(f => f.addEventListener('submit', () => { leaving = true; clearTimeout(timer); }));
  addEventListener('pagehide', () => { if (!leaving) navigator.sendBeacon('/auth/mfa/cancel', body()); });
  timer = setTimeout(poll, +root.dataset.poll || 2000);
})();
