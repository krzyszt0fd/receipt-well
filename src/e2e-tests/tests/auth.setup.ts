import { expect } from '@playwright/test';
import { test as setup } from '../with-options';
import path from 'path';

const authFile = path.join(__dirname, '../playwright/.auth/user.json');

setup('authenticate', async ({ page, authority, username, password }) => {
  // Perform authentication steps. Replace these actions with your own.
  await page.goto('/');
  await page.getByRole('button', {name: 'Sign In'}).click();
  await page.waitForURL(`${authority}/oauth2/v2.0/authorize?*`)
  await page.getByPlaceholder('Email address').fill(username);
  await page.getByRole('button', {name: 'Next'}).click();
  await page.getByPlaceholder('Password').fill(password);
  await page.getByRole('button', { name: 'Sign in' }).click();
  await page.waitForURL(`${authority}/login`)
  await page.getByRole('button', {name: 'Yes'}).click();
  // Wait until the page receives the cookies.
  //
  // Sometimes login flow sets cookies in the process of several redirects.
  // Wait for the final URL to ensure that the cookies are actually set.
  await page.waitForURL('/home/upload');

  // Alternatively, you can wait until the page reaches a state where all cookies are set.
  await expect(page.getByRole('button', { name: 'View my receipts' })).toBeVisible();

  // End of authentication steps.

  await page.context().storageState({ path: authFile });
});