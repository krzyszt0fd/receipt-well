# Lessons Learned

> Append-only register of recurring rules and patterns. Re-read at start by /10x-frame, /10x-research, /10x-plan, /10x-plan-review, /10x-implement, /10x-impl-review.

## Use GUID-based ciamlogin.com authority for Entra External ID

- **Context**: MSAL Angular auth config — any phase that sets `authority` / `knownAuthority` in environment files or `app.config.ts`
- **Problem**: CIAM consumer users can't sign in; internal admin accounts still work, hiding the misconfiguration
- **Rule**: For Entra External ID (CIAM), always set `authority` to `https://{tenant-id}.ciamlogin.com/{tenant-id}/v2.0` and `knownAuthorities` to the matching host. The `login.microsoftonline.com` endpoint and the friendly-name CIAM form (e.g. `receiptwellb2c.ciamlogin.com`) work only for organizational accounts, not consumer users.
- **Applies to**: environment setup, provisioning
