import { expect } from '@playwright/test';

/** Interact with the original UI's button/listbox DOM, never a hidden native select. */
export function referenceSelect(page, label) {
  return page.getByRole('combobox', { name: label, exact: true }).locator('..');
}

export async function chooseReferenceOption(page, label, value) {
  if (label === '当前租户') await closeReferenceDrawer(page);
  const control = referenceSelect(page, label);
  const trigger = control.getByRole('combobox');
  await expect(trigger).toBeEnabled();
  if (await control.getAttribute('data-value') === value) return;
  await trigger.click();
  const options = await control.locator('[role="option"]').all();
  for (const option of options) {
    if (await option.getAttribute('data-value') !== value) continue;
    await option.click();
    return;
  }
  throw new Error(`No original UI option ${JSON.stringify(value)} for ${JSON.stringify(label)}`);
}

export async function expectReferenceValue(page, label, value) {
  await expect(referenceSelect(page, label)).toHaveAttribute('data-value', value);
}

export async function referenceOptionValues(page, label) {
  return referenceSelect(page, label).locator('[role="option"]').evaluateAll(nodes => nodes.map(node => node.dataset.value));
}

export async function logoutFromUserMenu(page) {
  await closeReferenceDrawer(page);
  await page.getByRole('button', { name: '用户菜单', exact: true }).click();
  await page.getByRole('menuitem', { name: '退出登录', exact: true }).click();
}

export async function closeReferenceDrawer(page) {
  const drawer = page.locator('.reference-drawer-overlay[open]');
  if (!await drawer.count()) return;
  await drawer.getByRole('button', { name: '关闭', exact: true }).click();
  await expect(drawer).toHaveCount(0);
}

export async function toggleReferenceTheme(page) {
  const returnUrl = page.url();
  const reopen = await page.locator('.reference-drawer-overlay[open]').count();
  await closeReferenceDrawer(page);
  await page.getByRole('button', { name: /^切换(暗色|浅色)$/, exact: true }).click();
  if (reopen) {
    await page.goto(returnUrl);
    await expect(page.locator('.reference-drawer-overlay[open]')).toBeVisible();
  }
}

export async function expectOidcAccountPicker(page) {
  await expect(page).toHaveURL(/\/authorize\?/);
  await expect(page.getByRole('heading', { name: '选择验收账号', exact: true })).toBeVisible();
  expect(new URL(page.url()).searchParams.get('prompt')).toBe('login');
}

// A navigation destination for mocked browser tests only. It intentionally has no
// application bundle/session: a 401 must leave the protected document completely.
export function fulfillFixtureLogin(route) {
  return route.fulfill({ contentType: 'text/html; charset=utf-8', body: '<!doctype html><html lang="zh-CN"><head><meta charset="utf-8"><title>OIDC 登录测试目的地</title></head><body><main><h1>身份服务登录</h1><p>此文档仅用于验证门户已离开受保护页面。</p></main></body></html>' });
}

export async function expectFixtureLogin(page) {
  await expect(page).toHaveURL(/\/bff\/login\?returnUrl=/);
  await expect(page.getByRole('heading', { name: '身份服务登录', exact: true })).toBeVisible();
}

/** Calendar navigation and the original hour/minute wheels, by real UI input. */
export async function chooseReferenceDateTime(page, label, localValue) {
  const [date, time] = localValue.split('T');
  const [year, month] = date.split('-').map(Number);
  const [hour, minute] = time.split(':').map(Number);
  const trigger = page.getByRole('button', { name: label, exact: true });
  const control = trigger.locator('..');
  await trigger.click();
  const panel = control.locator('.ui-datetime__panel');
  await panel.locator('.ui-cal__title').click();
  await panel.locator('.ui-cal__title').click();
  for (let step = 0; step < 50; step++) {
    const years = panel.locator('[data-pick-year]');
    const values = await years.evaluateAll(nodes => nodes.map(node => Number(node.dataset.pickYear)));
    if (values.includes(year)) { await panel.locator(`[data-pick-year="${year}"]`).click(); break; }
    await panel.locator(year < values[0] ? '[data-cal-prev]' : '[data-cal-next]').click();
    if (step === 49) throw new Error('Date outside the supported calendar navigation range');
  }
  await panel.locator(`[data-pick-month="${month - 1}"]`).click();
  await panel.locator(`[data-day="${date}"]`).click();
  for (const [kind, value] of [['hour', hour], ['minute', minute]]) {
    const wheel = panel.locator(`[data-wheel="${kind}"] [data-wheel-scroll]`);
    await wheel.focus();
    await wheel.press('Home');
    for (let i = 0; i < value; i++) await wheel.press('ArrowDown');
  }
  await panel.press('Escape');
  await expect(control).toHaveAttribute('data-value', `${date}T${time.slice(0, 5)}`);
}
