// Provenance: Risk #6 — context/foundation/test-plan.md §2
//   "tag-search no-match panel rendering, distinct from 'No receipts yet' empty state"
//   browser render is the only verification (test-plan §4 stack rationale)
// Seed: seed.spec.ts
//
// Risk protected: when GET /receipts?q=<term> returns [], the component must render
//   the no-match panel (showing the term + clear action) — NOT the zero-state panel.
//   If the two @if branches are swapped or the condition collapses, this test fails.

import { test, expect } from '@playwright/test';

const apiBase = process.env['LOCAL_HOST'] ?? 'https://localhost:7028';

const SEED_RECEIPT = {
  id: 'e2e-no-match-seed-1',
  fileName: 'no-match-seed-receipt.jpg',
  fileSize: 10240,
  status: 'ready',
  uploadedAt: new Date(Date.now() - 86_400_000).toISOString(),
  storeName: 'Seed Store',
  purchaseDate: null,
  tags: ['rower', 'sport']
};

test('no-match panel renders for a non-matching tag search and is distinct from the zero-state', async ({ page }) => {
  // Intercept GET /receipts (browse, no ?q) → return a known receipt.
  // Intercept GET /receipts?q=* (search) → return empty array.
  // Scoped to the backend host so the Angular page navigation is not intercepted.
  await page.route(`${apiBase}/receipts*`, async (route, request) => {
    const hasQuery = new URL(request.url()).searchParams.has('q');
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: hasQuery ? '[]' : JSON.stringify([SEED_RECEIPT])
    });
  });

  // Step 1 — navigate to the receipts list (auth via storageState)
  await page.goto('/home/receipts');

  // Step 2 — initial load: the page is NOT in the zero-state (receipts exist)
  await expect(page.getByRole('heading', { name: 'My Receipts' })).toBeVisible();
  await expect(page.getByText('no-match-seed-receipt.jpg')).toBeVisible();

  // Step 3 — type a term that the mock makes return nothing (300ms debounce fires)
  const searchTerm = 'xyzzy-nonexistent';
  await page.getByRole('searchbox', { name: 'Search receipts by tag' }).fill(searchTerm);

  // Step 4 — wait for the debounced search request to complete
  await page.waitForResponse(response => {
    const url = response.url();
    return url.startsWith(`${apiBase}/receipts`) &&
      new URL(url).searchParams.get('q') === searchTerm;
  });

  // Step 5 — no-match panel renders with the search term embedded
  await expect(page.getByText(`No receipts match "${searchTerm}"`)).toBeVisible();

  // Step 6 — the "No receipts yet" zero-state must NOT appear (the critical distinction)
  await expect(page.getByText('No receipts yet')).not.toBeVisible();

  // Step 7 — a "Clear search" action is accessible
  await expect(page.getByRole('button', { name: 'Clear search' }).first()).toBeVisible();

  // Step 8 — clicking "Clear search" triggers a bare-list request; set up listener first
  const clearResponsePromise = page.waitForResponse(response => {
    const url = response.url();
    return url.startsWith(`${apiBase}/receipts`) &&
      !new URL(url).searchParams.has('q');
  });
  await page.getByRole('button', { name: 'Clear search' }).first().click();
  await clearResponsePromise;

  // Step 9 — no-match panel is gone; the receipt list is restored
  await expect(page.getByText(`No receipts match "${searchTerm}"`)).not.toBeVisible();
  await expect(page.getByText('no-match-seed-receipt.jpg')).toBeVisible();
});
