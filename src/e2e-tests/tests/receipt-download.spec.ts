// Provenance: context/changes/receipt-original-download/plan.md — Phase 3
// Seed: seed.spec.ts
//
// Risk protected: clicking the row's download action must call the
//   download-url endpoint and trigger a real browser download of the file at
//   the returned SAS URI — not merely navigate, and not silently swallow a
//   response that never arrives. If the anchor/target wiring in
//   `triggerDownload()` breaks, no `download` event fires.

import { expect } from '@playwright/test';
import { test } from '../with-options';

const RECEIPT_ID = `e2e-download-${Date.now()}`;
const FILE_NAME = `download-receipt-${Date.now()}.jpg`;

test('clicking the download action fetches the SAS URL and downloads the file', async ({ page, apiBase }) => {
  // Route on the context, not the page: the download opens in a new tab
  // (target="_blank"), and page-scoped routes don't apply to pages spawned later.
  const context = page.context();

  // Intercept GET /receipts (browse, no ?q) → one ready receipt. Search variant → empty.
  await context.route(`${apiBase}/receipts*`, async (route, request) => {
    const hasQuery = new URL(request.url()).searchParams.has('q');
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: hasQuery ? '[]' : JSON.stringify([{
        id: RECEIPT_ID,
        fileName: FILE_NAME,
        fileSize: 10240,
        status: 'ready',
        uploadedAt: new Date().toISOString(),
        storeName: 'Seed Store',
        purchaseDate: null,
        tags: []
      }])
    });
  });

  // Intercept the download-url endpoint — point at a routed mock-blob URL instead of real Azure.
  await context.route(`${apiBase}/receipts/${RECEIPT_ID}/download-url`, async route => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ downloadUri: `${apiBase}/mock-blob/${FILE_NAME}?sig=fake-sas` })
    });
  });

  // Intercept the blob itself with an attachment Content-Disposition, mirroring the real SAS
  // response, so navigating to it produces a genuine browser download.
  await context.route(`${apiBase}/mock-blob/*`, async route => {
    await route.fulfill({
      status: 200,
      contentType: 'image/jpeg',
      headers: { 'content-disposition': `attachment; filename="${FILE_NAME}"` },
      body: Buffer.from([0xff, 0xd8, 0xff, 0xd9])
    });
  });

  await page.goto('/home/receipts');
  await expect(page.getByRole('heading', { name: 'My Receipts' })).toBeVisible();
  await expect(page.getByText(FILE_NAME)).toBeVisible();

  // Register the listener on the context — the component opens the download URI in a new tab
  // (target="_blank"), so the download may land on a page other than the current one.
  const downloadPromise = context.waitForEvent('download');
  await page.getByRole('button', { name: `Download ${FILE_NAME}` }).click();
  const download = await downloadPromise;

  expect(download.suggestedFilename()).toBe(FILE_NAME);

  await page.waitForTimeout(500);
  await expect(page.locator('.receipt-row .file-name')).toBeVisible();
});
