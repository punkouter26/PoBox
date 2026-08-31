# AGENTS.md

Rules for coding agents working in this repository. These are binding.
See [CLAUDE.md](CLAUDE.md) for how the project itself is built and run.

## Git: master only

**This repository uses exactly one branch: `master`. Commit directly to it.**

- Do **not** create feature, topic, or work branches.
- Do **not** open pull requests against this repo.
- If any other branch exists, it is a leftover — delete it, locally and on the
  remote, once its work is on `master`.
- Push to `origin master`.

This overrides the usual "branch before committing to the default branch"
default. It is a solo repository; a branch here only adds a merge step and a
chance for work to sit unmerged.

### Merging a leftover branch safely

Checking out `master` reverts the working tree to master's contents, which can
leave files on disk that then block the merge as "untracked working tree files
would be overwritten". Move the branch pointer instead of moving the tree, or
verify the blocking file is byte-identical to the committed one before removing
it. Never delete an untracked file to unblock a merge without checking it is
already in the commit you are merging.
