// Provenance: Risk #5 — context/foundation/test-plan.md §2
//   "Receipt stuck in 'pending' forever, or the poison path silently drops it — processing status never resolves"
//   cross-system: upload → queue → Function → Search → UI status update (test-plan §4 stack rationale)
// Seed: seed.spec.ts
//
// Risk protected: a receipt that starts 'pending' must transition to a terminal status (ready/error)
//   via the list component's polling mechanism. If polling is never scheduled, or the status chip
//   is not re-rendered after a poll, this test fails.

import { expect } from '@playwright/test';
import { test } from '../with-options';

test('pending receipt transitions to resolved status via polling — never stuck forever', async ({ page, apiBase }) => {
  let receiptCallCount = 0;

  // First GET /receipts → 'pending'; subsequent calls → 'ready'
  // Simulates async processing completing between the initial load and the first poll.
  await page.route(`${apiBase}/receipts*`, async (route, request) => {
    const hasQuery = new URL(request.url()).searchParams.has('q');
    if (hasQuery) {
      await route.fulfill({ status: 200, contentType: 'application/json', body: '[]' });
      return;
    }
    receiptCallCount++;
    const status = receiptCallCount === 1 ? 'pending' : 'ready';
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([{
        id: 'e2e-async-status-1',
        fileName: 'async-processing-receipt.jpg',
        fileSize: 15360,
        status,
        uploadedAt: new Date().toISOString(),
        storeName: status === 'ready' ? 'Test Store' : null,
        purchaseDate: null,
        tags: status === 'ready' ? ['elektronika'] : []
      }])
    });
  });

  // Register poll listener before navigation — per E2E rules: set up waitForResponse before the
  // action that triggers the request. Waits for the first response where the receipt is 'ready'.
  const pollDone = page.waitForResponse(async response => {
    const url = response.url();
    if (!url.startsWith(`${apiBase}/receipts`) || new URL(url).searchParams.has('q')) {
      return false;
    }
    const body = await response.json() as Array<{ status: string }>;
    return Array.isArray(body) && body.some(r => r.status === 'ready');
  });

  // Step 1 — navigate to the receipts list (auth via storageState)
  await page.goto('/home/receipts');

  // Step 2 — receipt appears with 'pending' status chip (processing not yet complete)
  await expect(page.getByRole('heading', { name: 'My Receipts' })).toBeVisible();
  await expect(page.getByText('pending', { exact: true })).toBeVisible();

  // Step 3 — wait for the poll to resolve
  //   The component calls schedulePollIfPending() after each successful fetch while hasPending() is true.
  //   POLL_INTERVAL_MS = 5 000 ms; the promise resolves when the backend returns a resolved status.
  await pollDone;

  // Step 4 — 'ready' chip is now visible; 'pending' is gone — status resolved, not stuck forever
  await expect(page.getByText('ready', { exact: true })).toBeVisible();
  await expect(page.getByText('pending', { exact: true })).not.toBeVisible();
});
