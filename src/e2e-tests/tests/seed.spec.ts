// seed.spec.ts — exemplar for /10x-e2e skill.
// Every generated test models these patterns:
//   getByRole locators, page.route() for API determinism,
//   waitForResponse/toBeVisible (never waitForTimeout), auth via storageState.

import { expect } from '@playwright/test';
import { test } from '../with-options';

test('receipts list page renders heading and receipt row after navigation', async ({ page, apiBase }) => {
  await page.route(`${apiBase}/receipts*`, async route => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([{
        id: 'seed-1',
        fileName: 'seed-receipt.jpg',
        fileSize: 10240,
        status: 'ready',
        uploadedAt: new Date(Date.now() - 86_400_000).toISOString(),
        storeName: 'Seed Store',
        purchaseDate: null,
        tags: ['rower']
      }])
    });
  });

  await page.goto('/home/receipts');

  // Wait for state — not time; assertions retry automatically
  await expect(page.getByRole('heading', { name: 'My Receipts' })).toBeVisible();
  await expect(page.getByText('seed-receipt.jpg')).toBeVisible();
});
