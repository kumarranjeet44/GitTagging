# GitTagging Strategy

Tagging Rules
1. Pre-release Tagging
  Branches: develop, release, hotfix
  Tag Format:
  Alpha:
  For develop branch
  Example: v0.1.1-alpha.X
  Beta:
  For release and hotfix branches
  Example: v0.1.1-beta.Y
  X / Y: Represents the number of commits since the last version tag (automatically incremented).
2. Stable Release Tagging
  Branch: master
  Tag Format:
  Example: v0.1.1
  Description:
  Every commit to the master branch is considered a stable release and is tagged without any pre-release suffix.
