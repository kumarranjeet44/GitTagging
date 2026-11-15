# GitTagging

## Production release notes

Production notes include Jira keys from commits reachable from the current `vX.Y.Z`
tag, excluding history already covered by other production (`vX.Y.Z`) tags.
Alpha, beta, feature, and bugfix tags do not exclude commits from production notes.
The current tag is not excluded, so rerunning the same release produces the same
ticket range. With no earlier production tag, the full tagged history is scanned.
There is no 50-commit limit for production notes; non-production notes retain
their existing last-50-commits behavior.

The workflow must fetch full history and tags (`fetch-depth: 0`). Git failures
stop production note generation rather than publishing an incomplete ticket list.
Keys are deduplicated within the release; a previously released key mentioned
again in a new commit can still appear.
