## Overall concept

- GHA workflow run for every new pull request to master
- composite action for the review itself so that main workflow is easy to reason about - use packages/code-reviewer

## Input parameters

- pull request title
- pull request description (?? cost tradeoff)
- git diff

## Code Review Criteria

- check packages\code-reviewer\src\review-schema.ts
- project rules from Claude.md files and readme

{{CR_CRITERIA}}

## Parked for later

- business alignment (require broader context)
- architectural fit (require broader context)

## Expected side-effects

- PR comment with summary
- labels: `ai-review:failed` (red) OR `ai-review:passed` (green)

## Expected behavior

- on-demand retry when label `ai-review:review` is added