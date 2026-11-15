# GitTagging

## Release notes

All releases include Jira keys from commits reachable from the current release
tag, excluding history already covered by other production (`vX.Y.Z`) tags.
Alpha, beta, feature, and bugfix tags do not exclude commits from release notes.
The current tag is not excluded, so rerunning the same release produces the same
ticket range. With no earlier production tag, the full tagged history is scanned.
There is no 50-commit limit. Prereleases retain all tickets not yet shipped in
production, even when those tickets appeared in an earlier prerelease.

The workflow must fetch full history and tags (`fetch-depth: 0`). Git failures
stop note generation rather than publishing an incomplete ticket list.
Keys are deduplicated within the release; a previously released key mentioned
again in a new commit can still appear.
