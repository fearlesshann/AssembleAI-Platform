import { mountReferenceLoginFx } from './login-fx.js';

const screen = document.getElementById('loginScreen');
const form = document.getElementById('loginForm');
const account = document.getElementById('loginAccount');
const password = document.getElementById('loginPassword');
const confirmation = document.getElementById('loginPasswordConfirm');
const submit = document.getElementById('loginSubmit');
const feedback = document.getElementById('loginFormError');
const toggle = document.getElementById('loginThemeToggle');
screen?.classList.add('is-enter');
requestAnimationFrame(() => screen?.classList.add('is-ready'));
let disposeEffects = screen ? mountReferenceLoginFx(screen) : () => {};
let submitted = false;

toggle?.addEventListener('click', () => {
  const theme = document.documentElement.dataset.theme === 'light' ? 'dark' : 'light';
  document.documentElement.dataset.theme = theme;
  try { localStorage.setItem('assembleai.theme', theme); } catch { /* optional preference */ }
});

function invalid(input, name, message) {
  if (!input) return;
  input.classList.add('is-invalid'); input.setAttribute('aria-invalid', 'true');
  const error = form?.querySelector(`[data-error-for="${name}"]`);
  if (error) { error.textContent = message; error.hidden = false; }
}
form?.addEventListener('input', event => {
  const input = event.target;
  if (!(input instanceof HTMLInputElement)) return;
  input.classList.remove('is-invalid'); input.removeAttribute('aria-invalid');
  const error = form.querySelector(`[data-error-for="${input === account ? 'account' : input === confirmation ? 'password-confirm' : 'password'}"]`);
  if (error) error.hidden = true;
  if (feedback) { feedback.hidden = true; feedback.textContent = ''; }
});
form?.addEventListener('submit', event => {
  if (event.submitter?.name === 'cancel-aia') return;
  if (submitted) { event.preventDefault(); return; }
  let valid = true;
  if (account && !account.value.trim()) { invalid(account, 'account', '请输入账号或邮箱'); valid = false; }
  if (password && !password.value) { invalid(password, 'password', '请输入密码'); valid = false; }
  if (confirmation && !confirmation.value) { invalid(confirmation, 'password-confirm', '请再次输入新密码'); valid = false; }
  if (!valid) {
    event.preventDefault();
    if (feedback) { feedback.textContent = '请完善登录信息'; feedback.hidden = false; }
    form.querySelector('.is-invalid')?.focus();
    return;
  }
  // Preserve native identity-provider POST. There is no fake auth flag, password
  // proxy, client-side success path, or credential persistence here.
  submitted = true;
  if (submit) submit.disabled = true;
});
window.addEventListener('pagehide', () => { if (password) password.value = ''; if (confirmation) confirmation.value = ''; disposeEffects(); });
window.addEventListener('pageshow', event => {
  if (!event.persisted) return;
  submitted = false; if (submit) submit.disabled = false;
  if (screen) disposeEffects = mountReferenceLoginFx(screen);
});
