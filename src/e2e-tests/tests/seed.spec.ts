import { test, expect } from '@playwright/test';

test('has title', async ({ page }) => {
  await page.goto('/home/upload');

  // Expect a title "to contain" a substring.
  await expect(page).toHaveTitle('Frontend');
});

test('Navigate to receipts', async ({ page }) => {
  await page.goto('/home/upload');

  // Click the get started link.
  await page.getByRole('button', { name: 'View my receipts' }).click();

  page.waitForURL('/home/receipts');

  // Expects page to have a heading with the name of Installation.
  await expect(page.getByRole('heading', { name: 'My Receipts' })).toBeVisible();
});
