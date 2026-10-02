---
name: agent-publisher
description: Use to finish a change: run the full gate and the ARM build, commit with the project's message rules, push origin/master, sync and push the Docker mirror when WhatsappBridge changed, update the bilingual docs and the plan, and keep the agent skills themselves pushed.
---

# The publisher

## What it receives

The coder's changed files and its fast gate result. The publisher does not edit
the change: it verifies it, records it, and puts it on the remote.

## The full gate and the build

Run the gate and the ARM build command from
[`test-the-app`](../test-the-app/SKILL.md). ARM for the phone, x86 for the
emulator, never Any CPU. After the build, check out **only**
`WhatsappApp/Package.appxmanifest`; a checkout of `WhatsappApp.csproj` drops the
`Compile Include` entries, because the build runs on a copy on the VM. If the
gate, a test or the build fails, stop here (see below).

## The commit

One commit per decision. The subject says *why*, in English, with no apostrophe;
the recent history is the style. Multiple independent decisions are multiple
commits, not one large one.

## The push

`git push origin master`. Push is the rule, not a question: a change is finished
only when it is on the remote. Confirm the remote and the branch before pushing.

## The mirror

Only when the change touches `WhatsappBridge/`. From
`/Users/vincenzo/Documents/docker-whatsappforwp`:

```bash
node tools/sync.js --from /Users/vincenzo/Documents/WhatsappForWP
node tools/sync.js --check --from /Users/vincenzo/Documents/WhatsappForWP
```

until it prints `OK: server/ matches the adapter (28 file(s))`, then
`cd server && npm test`, commit, and `git push origin main`.

## The documents

- the two READMEs of a pair, English and Italian, in the same commit
  (`check-docs.js` fails a pair whose headings drift apart);
- the invariant in [`maintain-the-app`](../maintain-the-app/SKILL.md) when the
  change makes an old rule false;
- the plan in `docs/superpowers/plans/`, which was written before the work and is
  the record of it.

## The roster

A change to any `.agents/skills/agent-*` file is pushed like any other file: this
is the "push the agents on GitHub" step. Confirm with
`git status --short .agents/skills` that nothing is left untracked or modified,
and with the remote log that the commits are on `origin/master`.

## When it stops

A guard, a test or the build fails: report the output and commit nothing. The
publisher never commits a tree the gate refused, and never pushes one the remote
would have to be told about afterwards.
