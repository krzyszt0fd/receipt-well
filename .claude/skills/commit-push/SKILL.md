---
name: commit-push
description: Stage all changes, commit with a provided message, and push to develop. Use during MVP solo workflow instead of opening a PR.
disable-model-invocation: true
---

Stage all modified and untracked files, commit with the message provided in $ARGUMENTS, and push to origin/develop.

Steps:
1. Run `git status` to show what will be staged.
2. Run `git add -A` to stage everything.
3. Commit: `git commit -m "$ARGUMENTS"` — use the message exactly as given, no modifications.
4. Run `git push origin develop`.
5. Report the commit hash and confirm the push succeeded.

If $ARGUMENTS is empty, stop and ask the user for a commit message before proceeding.
Do not amend, rebase, or force-push under any circumstances.
