import { expect, test, type Page } from '@playwright/test';

async function enterDemoPortal(page: Page, remember = false) {
  await page.getByLabel('账号 / 邮箱').fill('portal-demo@example.test');
  await page.getByLabel('密码').fill('synthetic-demo-password');
  if (remember) await page.getByLabel('记住我').check();
  await page.getByRole('button', { name: '进入工作台', exact: true }).click();
  await expect(page.locator('#loginScreen')).toBeHidden();
  await expect(page.locator('#appShell')).toBeVisible();
}

test.beforeEach(async ({ page }) => {
  await page.emulateMedia({ colorScheme: 'light', reducedMotion: 'reduce' });
  await page.goto('/?motion=reduced');
});

test('template portal opens on demo login, validates fields and switches sidebar views', async ({ page }) => {
  await expect(page.getByRole('heading', { name: '登录工作台', exact: true })).toBeVisible();
  await expect(page.getByRole('heading', { name: '让 AI 服务工厂的每个角落' })).toBeVisible();
  await expect(page.locator('#appShell')).toBeHidden();

  await page.getByRole('button', { name: '进入工作台', exact: true }).click();
  await expect(page.getByText('请输入账号或邮箱', { exact: true })).toBeVisible();
  await expect(page.getByText('请输入密码', { exact: true })).toBeVisible();
  await expect(page.getByRole('alert')).toHaveText('请完善登录信息');
  await expect(page.locator('#appShell')).toBeHidden();

  await enterDemoPortal(page);
  await expect(page.getByRole('heading', { name: 'A2 回流焊温漂排查', exact: true })).toBeVisible();
  const navigation = page.getByRole('navigation', { name: '主导航', exact: true });
  await navigation.getByRole('button', { name: '应用中心', exact: true }).click();
  await expect(page.getByRole('heading', { name: '应用中心', exact: true })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'A2 回流焊温漂排查', exact: true })).toBeHidden();
  await navigation.getByRole('button', { name: '管理中心', exact: true }).click();
  await expect(page.getByRole('heading', { name: '管理中心', exact: true })).toBeVisible();
  await expect(page.getByPlaceholder('搜索成员、角色、产线')).toBeVisible();
  await expect(page.getByRole('heading', { name: '应用中心', exact: true })).toBeHidden();
  await navigation.getByRole('button', { name: '工作台', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'A2 回流焊温漂排查', exact: true })).toBeVisible();
  await expect(page.getByRole('heading', { name: '管理中心', exact: true })).toBeHidden();
});

test('template application search combines with status filters and clears the empty state', async ({ page }) => {
  await enterDemoPortal(page);
  await page.getByRole('navigation', { name: '主导航', exact: true })
    .getByRole('button', { name: '应用中心', exact: true }).click();
  const applications = page.locator('#view-apps');
  const visibleRows = applications.locator('.app-row:visible');
  const search = applications.getByPlaceholder('搜索应用…');
  await expect(visibleRows).toHaveCount(8);

  await applications.getByRole('button', { name: '运行中', exact: true }).click();
  await expect(visibleRows).toHaveCount(5);
  await applications.getByRole('button', { name: '试运行', exact: true }).click();
  await expect(visibleRows).toHaveCount(2);
  await search.fill('回流焊');
  await expect(visibleRows).toHaveCount(1);
  await expect(visibleRows).toContainText('回流焊工艺助手');
  await applications.getByRole('button', { name: '草稿', exact: true }).click();
  await expect(visibleRows).toHaveCount(0);
  await expect(applications.getByText('没有匹配的应用', { exact: true })).toBeVisible();

  await applications.getByRole('button', { name: '清除筛选', exact: true }).click();
  await expect(search).toHaveValue('');
  await expect(visibleRows).toHaveCount(8);
  await expect(applications.getByText('没有匹配的应用', { exact: true })).toBeHidden();
  await search.fill('APP-1024');
  await expect(visibleRows).toHaveCount(1);
  await expect(visibleRows).toContainText('视觉质检助手');
});

test('template theme survives reload and remembered demo login can be logged out', async ({ page }) => {
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
  await page.getByRole('button', { name: '切换到暗色主题', exact: true }).click();
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
  await page.reload();
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');

  await enterDemoPortal(page, true);
  await page.reload();
  await expect(page.locator('#appShell')).toBeVisible();
  await expect(page.locator('#loginScreen')).toBeHidden();
  await page.getByRole('button', { name: '切换到亮色主题', exact: true }).click();
  await page.reload();
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
  await page.getByRole('button', { name: '叶 叶寒', exact: true }).click();
  await page.getByRole('menuitem', { name: '退出登录', exact: true }).click();
  await expect(page.getByRole('heading', { name: '登录工作台', exact: true })).toBeVisible();
  await expect(page.getByLabel('密码')).toHaveValue('');
  await expect(page.locator('#appShell')).toBeHidden();
  await page.reload();
  await expect(page.getByRole('heading', { name: '登录工作台', exact: true })).toBeVisible();
  await expect(page.locator('#appShell')).toBeHidden();
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
});
