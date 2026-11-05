import { expect, test } from '@playwright/test';

test('V2/V19: one release runs independently and inside qiankun, with cleanup', async ({ page }) => {
  const errors: string[] = [];
  page.on('pageerror', (error) => errors.push(error.message));
  await page.addInitScript(() => {
    const listeners = new Set<EventListenerOrEventListenerObject>();
    const add = window.addEventListener.bind(window);
    const remove = window.removeEventListener.bind(window);
    window.addEventListener = ((type: string, listener: EventListenerOrEventListenerObject, options?: boolean | AddEventListenerOptions) => {
      if (type === 'assembleai:sample-ping') listeners.add(listener);
      add(type, listener, options);
    }) as typeof window.addEventListener;
    window.removeEventListener = ((type: string, listener: EventListenerOrEventListenerObject, options?: boolean | EventListenerOptions) => {
      if (type === 'assembleai:sample-ping') listeners.delete(listener);
      remove(type, listener, options);
    }) as typeof window.removeEventListener;
    Object.defineProperty(window, '__sampleListenerCount', { get: () => listeners.size });
  });
  const listenerCount = () => page.evaluate(() => (window as unknown as { __sampleListenerCount: number }).__sampleListenerCount);
  await page.goto('/apps/sample/0.1.0/');
  await expect(page.getByText('独立运行', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: '增加计数' }).click();
  await expect(page.getByTestId('sample-count')).toHaveText('1');
  await page.goto('/workspace/integration');
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
  for (let cycle = 0; cycle < 3; cycle++) {
    await page.getByRole('button', { name: '打开应用', exact: true }).click();
    await expect(page.getByText('平台内运行', { exact: true })).toBeVisible();
    await expect(page.getByTestId('sample-count')).toHaveText('0');
    expect(await listenerCount()).toBe(1);
    await page.evaluate(() => window.dispatchEvent(new Event('assembleai:sample-ping')));
    await expect(page.getByTestId('sample-signals')).toHaveText('1');
    await page.getByRole('button', { name: '增加计数' }).click();
    await expect(page.getByTestId('sample-count')).toHaveText('1');
    await page.getByRole('button', { name: '返回应用列表' }).click();
    await expect(page.getByTestId('sample-app')).toHaveCount(0);
    expect(await listenerCount()).toBe(0);
  }
  await page.goto('/workspace/sample/details');
  await expect(page.getByText('平台内运行', { exact: true })).toBeVisible();
  await page.reload();
  await expect(page.getByTestId('sample-app')).toHaveCount(1);
  await page.getByRole('button', { name: '切换暗色' }).click();
  await expect(page.getByTestId('sample-app')).toHaveAttribute('data-theme', 'dark');
  await page.screenshot({ path: '.artifacts/embedded-dark.png', fullPage: true });
  expect(errors).toEqual([]);
});

test('V19: retry reloads the document and clears rejected asset caches', async ({ page }) => {
  let attempts = 0;
  page.on('request', (request) => {
    if (new URL(request.url()).pathname === '/apps/sample/0.1.0/sample.umd.js') attempts++;
  });
  await page.route('**/apps/sample/0.1.0/sample.umd.js', (route) => route.abort());
  await page.goto('/workspace/sample');
  await expect(page.getByRole('alert')).toContainText('应用加载失败');
  await page.unroute('**/apps/sample/0.1.0/sample.umd.js');
  await Promise.all([
    page.waitForEvent('load'),
    page.getByRole('button', { name: '重新加载页面' }).click(),
  ]);
  await expect(page.getByTestId('sample-app')).toBeVisible();
  expect(attempts).toBeGreaterThanOrEqual(2);
  await expect(page).toHaveURL(/\/workspace\/sample$/);
});

test('V10/V11: default light, search, compact viewport and reduced motion', async ({ page }) => {
  await page.emulateMedia({ colorScheme: 'dark', reducedMotion: 'reduce' });
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto('/workspace/integration');
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
  await page.getByRole('textbox', { name: '搜索应用' }).fill('不存在');
  await expect(page.getByText('没有匹配的应用')).toBeVisible();
  await page.getByRole('button', { name: '清除搜索' }).click();
  await expect(page.getByRole('button', { name: '打开应用', exact: true })).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.screenshot({ path: '.artifacts/portal-mobile.png', fullPage: true });
});

test('V19: child styles apply inside the app without changing the host across mount and unmount', async ({ page }) => {
  // Exercise isolation with a realistic unscoped child rule. Removing qiankun's
  // style isolation must change host buttons and fail this test.
  await page.route('**/apps/sample/0.1.0/sample.css', async (route) => {
    const response = await route.fetch();
    await route.fulfill({
      response,
      body: `${await response.text()}\nbutton { border-top: 9px solid red !important; }`,
    });
  });
  await page.goto('/workspace/integration');
  await expect(page.getByRole('button', { name: '打开应用', exact: true })).toBeVisible();
  const hostStyles = () => page.evaluate(() => ['.sidebar', '.nav-item', '.topbar', '.topbar button'].map((selector) => {
    const element = document.querySelector(selector);
    if (!element) throw new Error(`Missing host element: ${selector}`);
    const style = getComputedStyle(element);
    return {
      selector,
      color: style.color,
      background: style.backgroundColor,
      font: style.fontFamily,
      fontSize: style.fontSize,
      borderTopWidth: style.borderTopWidth,
      borderColor: style.borderTopColor,
      padding: style.padding,
    };
  }));
  const beforeMount = await hostStyles();

  await page.getByRole('button', { name: '打开应用', exact: true }).click();
  const sample = page.getByTestId('sample-app');
  await expect(sample).toBeVisible();
  await expect(sample.locator('h2')).toHaveCSS('font-size', '16px');
  await expect(sample.getByRole('button', { name: '增加计数' })).toHaveCSS('border-top-width', '9px');
  await expect(page.getByRole('button', { name: '返回应用列表' })).toBeEnabled();
  await expect.poll(hostStyles).toEqual(beforeMount);

  await page.getByRole('button', { name: '返回应用列表' }).click();
  await expect(sample).toHaveCount(0);
  await expect(page.getByRole('button', { name: '打开应用', exact: true })).toBeVisible();
  await expect.poll(hostStyles).toEqual(beforeMount);
});

test('V19: browser history during a delayed mount settles on one app and cleans up on back navigation', async ({ page }) => {
  const errors: string[] = [];
  page.on('pageerror', (error) => errors.push(error.message));
  await page.addInitScript(() => {
    const listeners = new Set<EventListenerOrEventListenerObject>();
    const add = window.addEventListener.bind(window);
    const remove = window.removeEventListener.bind(window);
    window.addEventListener = ((type: string, listener: EventListenerOrEventListenerObject, options?: boolean | AddEventListenerOptions) => {
      if (type === 'assembleai:sample-ping') listeners.add(listener);
      add(type, listener, options);
    }) as typeof window.addEventListener;
    window.removeEventListener = ((type: string, listener: EventListenerOrEventListenerObject, options?: boolean | EventListenerOptions) => {
      if (type === 'assembleai:sample-ping') listeners.delete(listener);
      remove(type, listener, options);
    }) as typeof window.removeEventListener;
    Object.defineProperty(window, '__sampleListenerCount', { get: () => listeners.size });
    Object.defineProperty(window, '__documentMarker', { value: crypto.randomUUID() });
  });
  const listenerCount = () => page.evaluate(() => (window as unknown as { __sampleListenerCount: number }).__sampleListenerCount);
  const documentMarker = () => page.evaluate(() => (window as unknown as { __documentMarker: string }).__documentMarker);
  let scriptRequested = false;
  let releaseScript!: () => void;
  const scriptGate = new Promise<void>((resolve) => { releaseScript = resolve; });
  await page.route('**/apps/sample/0.1.0/sample.umd.js', async (route) => {
    scriptRequested = true;
    await scriptGate;
    await route.continue();
  });

  try {
    await page.goto('/workspace/integration');
    const initialDocument = await documentMarker();
    await page.getByRole('button', { name: '打开应用', exact: true }).click();
    await expect.poll(() => scriptRequested).toBe(true);
    await expect(page).toHaveURL(/\/workspace\/sample$/);
    await page.goBack();
    await expect(page).toHaveURL(/\/workspace\/integration$/);
    await page.goForward();
    await expect(page).toHaveURL(/\/workspace\/sample$/);
    releaseScript();

    await expect(page.getByTestId('sample-app')).toHaveCount(1);
    await expect(page.getByTestId('sample-count')).toHaveText('0');
    await expect(page.getByRole('button', { name: '返回应用列表' })).toBeEnabled();
    await expect.poll(listenerCount).toBe(1);
    await page.evaluate(() => window.dispatchEvent(new Event('assembleai:sample-ping')));
    await expect(page.getByTestId('sample-signals')).toHaveText('1');
    await page.getByRole('button', { name: '增加计数' }).click();
    await expect(page.getByTestId('sample-count')).toHaveText('1');

    await page.goBack();
    await expect(page).toHaveURL(/\/workspace\/integration$/);
    await expect(page.getByTestId('sample-app')).toHaveCount(0);
    await expect.poll(listenerCount).toBe(0);
    await page.goForward();
    await expect(page).toHaveURL(/\/workspace\/sample$/);
    await expect(page.getByTestId('sample-app')).toHaveCount(1);
    await expect(page.getByTestId('sample-count')).toHaveText('0');
    await expect(page.getByTestId('sample-signals')).toHaveText('0');
    await expect.poll(listenerCount).toBe(1);
    expect(await documentMarker()).toBe(initialDocument);
    expect(errors).toEqual([]);
  } finally {
    releaseScript();
  }
});
