# E2E Testing Rules

- Use `getByRole`, `getByLabel`, `getByText` as primary locators.
  Fall back to `getByTestId` only when accessibility attributes are ambiguous.
- Never use CSS selectors, XPath, or DOM structure for locating elements.
- Each test must be independently runnable — no shared state between tests.
- Never use `page.waitForTimeout()`. Wait for specific conditions:
  `toBeVisible()`, `waitForURL()`, `waitForResponse()`.
- Assert the business outcome, not implementation details.
- Use `storageState` for authentication — never log in through UI in individual tests.
- Mock only the API layer (`page.route()`) for non-deterministic or infra-heavy calls.
  Internal boundaries (auth, routing, Angular) stay real.
- Set up `waitForResponse()` *before* the action that triggers the request to avoid
  race conditions where a fast response arrives before the listener is registered.
