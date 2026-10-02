# The Team of Agents Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give this project a roster of four agents - one investigates, one writes the code, one pushes and updates the docs, one coordinates them - so that every prompt runs through the same protocol and the roster itself lives on GitHub.

**Architecture:** The roster is four project skills under `.agents/skills/`, in the same shape as `maintain-the-app` and `test-the-app`. The coordinator is the entry point: it splits a prompt into roles, hands the investigator's facts to the coder, the coder's change to the publisher, and does not finish until the publisher has pushed. Each role delegates the gate, the message rules and the mirror rule to the authoritative skills instead of copying them, so there is one place to change them.

**Tech Stack:** Agent Skills (a `SKILL.md` per folder with YAML frontmatter), Markdown only; the guards in `tools/` verify the documents.

**Spec:** the request of 2026-10-02 - "degli agenti che ogni volta che ti do un prompt fanno uno il codice, un altro investiga, l'altro pusha e aggiorna i documenti sui repo e un altro coordina tutti e riceve/manda le informazioni e pusha pure gli agents su github". The shape was chosen by the user: project skills in `.agents/skills`, not an external process. This harness has no separate autonomous processes, so a role is a skill the coordinator loads and follows, in order, on every prompt.

## Global Constraints

- Agent Skills format: the folder name equals the frontmatter `name`; the `description` says what the skill does and when to use it. The four new folders are `agent-coordinator`, `agent-investigator`, `agent-coder`, `agent-publisher`.
- No emoji in any Markdown except the warning sign U+26A0 - `tools/check-docs.js` walks every `.md` file and fails otherwise.
- The gate, the ARM build command, the commit-message rules and the Docker mirror rule are **not** repeated in these skills: `maintain-the-app` is the authority for all four, `test-the-app` for the gate matrix. A role that needs them says "run the gate from `test-the-app`".
- Every file LF, no BOM.
- Push is the rule, not a question: each task ends with `git push origin master`. These tasks touch no `WhatsappBridge/` file, so no mirror sync is due for them.
- No apostrophes in commit messages; documents in English like the other plans and skills.
- There is no test harness for Markdown. The test of a document task is `node tools/check-docs.js`, `node --test "tools/test/**/*.test.js"`, and reading the files back.

## Review Focus

- A prompt that needs no code (a question, a log to read): the coordinator answers through the investigator and stops, and does not run the coder or the publisher for nothing. Task 1 Step 3 owns it.
- A prompt where the cause cannot be found: the investigator says "not found" with what it ruled out, and invents nothing. Task 2 Step 3 owns it.
- A role file that is missing or renamed: the coordinator says so and stops, instead of silently skipping a role. Task 1 Step 4 owns it.
- The full gate or the ARM build fails under the publisher: nothing is committed, and the failure is reported with its output. Task 3 Step 4 owns it.
- The prompt changes `WhatsappBridge/`: the publisher syncs and pushes the Docker mirror, not only `origin/master`. Task 3 Step 5 owns it.
- The roster itself changed: the publisher commits and pushes `.agents/skills/agent-*`, because "push the agents on GitHub" is part of the job. Task 3 Step 6 owns it.

---

### Task 1: The coordinator and its protocol

**Files:**
- Create: `.agents/skills/agent-coordinator/SKILL.md`
- Test: `node tools/check-docs.js` (no emoji) and reading it back

**Interfaces:**
- Consumes: the three role skills by name - `agent-investigator`, `agent-coder`, `agent-publisher`.
- Produces: the protocol every prompt follows: investigate, then code, then publish, then report.

- [ ] **Step 1: Write the frontmatter**

Create `.agents/skills/agent-coordinator/SKILL.md` starting with:

```markdown
---
name: agent-coordinator
description: Use at the start of any prompt that should be carried out by the team of agents instead of one pass. It decides which roles run, in what order, and what each hands to the next, and it does not finish until the publisher has pushed. Loads agent-investigator, agent-coder and agent-publisher by name.
---
```

- [ ] **Step 2: Write the body, section by section**

The body must have exactly these sections, in this order:

1. `## What this is` - the roster is four skills, one per role; this one coordinates and changes no file itself.
2. `## The roster` - a four-row table: `agent-investigator` (facts, before any change), `agent-coder` (the smallest change that follows `maintain-the-app`), `agent-publisher` (gate, build, commit, push, docs), `agent-coordinator` (this file). Each row says what it receives and what it hands back.
3. `## The protocol, per prompt` - the ordered steps: read the prompt and the repository; load `agent-investigator` and get its facts; decide whether a change is due; if yes, load `agent-coder`, get the change, then load `agent-publisher`; if no, answer from the investigator's report and stop; report at the end with the commit hashes and what was pushed.
4. `## Handoffs` - what travels between roles: the investigator's report goes to the coder unchanged; the coder's list of changed files and the gate result go to the publisher; the publisher's hashes go back to the coordinator.
5. `## When a role is skipped` - a prompt that only needs reading stops after the investigator; a prompt whose change is already in the tree goes straight to the publisher. State that the coder and publisher are never run "just in case".
6. `## The roster is code too` - a change to any `agent-*` file is committed and pushed like any other, through the publisher.
7. `## Stop conditions` - stop and say so when a role's file is missing, when the investigator returns "not found", or when the publisher's gate fails; never substitute a guess for a role.

- [ ] **Step 3: Check the protocol against a prompt that needs no code**

Re-read section `## The protocol, per prompt` with this input: "explain why the chat is empty". Confirm the text answers it through the investigator and stops, with no coder and no publisher. If it does not, fix the text before moving on.

- [ ] **Step 4: Check the missing-role rule**

Confirm `## Stop conditions` names the case of a role file that is missing or renamed, and that the coordinator reports it instead of skipping the role.

- [ ] **Step 5: Verify and commit**

Run: `node tools/check-docs.js` then `node --test "tools/test/**/*.test.js"`
Expected: `OK: 2 doc pair(s) in step ... no emoji in N .md file(s).` and the same pass count as before.

```bash
git add .agents/skills/agent-coordinator/SKILL.md
git commit -m "Add the coordinator that runs the roster on every prompt"
git push origin master
```

---

### Task 2: The three roles

**Files:**
- Create: `.agents/skills/agent-investigator/SKILL.md`
- Create: `.agents/skills/agent-coder/SKILL.md`
- Create: `.agents/skills/agent-publisher/SKILL.md`
- Test: `node tools/check-docs.js` and reading them back

**Interfaces:**
- Consumes: `maintain-the-app` (constraints, project map, push rule, mirror rule) and `test-the-app` (the gate matrix). Neither is copied, only referenced.
- Produces: the three roles the coordinator loads by name.

- [ ] **Step 1: Write the investigator**

Frontmatter:

```markdown
---
name: agent-investigator
description: Use before changing code, to turn a prompt, a log or a bug report into facts: which files, which lines, which guard, what the adapter sends, what the phone printed. Produces a report with file:line evidence and changes nothing.
---
```

Body sections: `## What it produces` (a report: the question, the evidence with `file:line`, the likely cause with the evidence for it, what it ruled out, what it could not determine); `## Where to look, in order` (the phone log's `DIAG` lines, then the guard suite and its tests, then the C# at the named site, then the adapter and its tests, then the plans in `docs/superpowers/plans/`); `## Evidence rules` (quote the line, name the file and the number; a claim without a line is not evidence; say "not found" with what was ruled out rather than inventing a cause; a guessed cause is labelled as a guess); `## What it must not do` (no edit, no commit, no build).

- [ ] **Step 2: Write the coder**

Frontmatter:

```markdown
---
name: agent-coder
description: Use to write the change the investigator described. The smallest edit that follows maintain-the-app, with the guard or test that pins it; runs the fast gate; never pushes.
---
```

Body sections: `## What it receives` (the investigator's report and the exact files); `## The rules it follows` (load `maintain-the-app`; C# 5; a new `.cs` is a project file; a new guard comes with its test; the change is the smallest that answers the report); `## The test it leaves behind` (a rule that can regress gets a guard, in `tools/` with a test in `tools/test/`, added to the gate in both READMEs and both skills); `## The gate it runs` (the fast gate from `test-the-app`, no ARM build - that is the publisher's); `## What it must not do` (no commit, no push, no ARM build, no doc edit beyond the invariant it changed).

- [ ] **Step 3: Write the publisher**

Frontmatter:

```markdown
---
name: agent-publisher
description: Use to finish a change: run the full gate and the ARM build, commit with the project's message rules, push origin/master, sync and push the Docker mirror when WhatsappBridge changed, update the bilingual docs and the plan, and keep the agent skills themselves pushed.
---
```

Body sections: `## What it receives` (the coder's changed files and the gate result); `## The full gate and the build` (the gate and the ARM build command from `test-the-app`, never Any CPU; after the build check out only `Package.appxmanifest`); `## The commit` (no apostrophes; the what plus the why; the project's recent messages as the style); `## The push` (verify remote and branch first, then `git push origin master`; push is the rule, not a question); `## The mirror` (only when `WhatsappBridge/` changed: sync with `tools/sync.js`, `--check` until `OK: server/ matches the adapter`, `cd server && npm test`, commit and `git push origin main`); `## The documents` (the bilingual READMEs together, the invariant in `maintain-the-app`, the plan in `docs/superpowers/plans/`); `## The roster` (a change to `.agents/skills/agent-*` is pushed too - this is the "push the agents on GitHub" step); `## When it stops` (a gate or build failure is reported with its output and nothing is committed).

- [ ] **Step 4: Verify the three files**

Run: `node tools/check-docs.js` then `node --test "tools/test/**/*.test.js"`
Expected: no emoji, doc pairs in step, pass count unchanged. Then re-read each file and confirm its frontmatter `name` equals its folder name.

- [ ] **Step 5: Commit and push**

```bash
git add .agents/skills/agent-investigator/SKILL.md .agents/skills/agent-coder/SKILL.md .agents/skills/agent-publisher/SKILL.md
git commit -m "Add the investigator, coder and publisher roles"
git push origin master
```

---

### Task 3: The index, and the roster on GitHub

**Files:**
- Modify: `.agents/skills/README.md` (the skills index)
- Test: `node tools/check-docs.js` and `git status` on a clean tree

**Interfaces:**
- Consumes: the four `agent-*` folders from Tasks 1 and 2.
- Produces: the index entry that makes the roster discoverable, and a tree whose agent skills are on `origin/master`.

- [ ] **Step 1: Index the roster**

In `.agents/skills/README.md`, after the project-skill table and before the community-skills paragraph, add a short paragraph and a table for the roster: `agent-coordinator`, `agent-investigator`, `agent-coder`, `agent-publisher`, one line each on when to use it. Say that they are project skills, not community ones, so they are not in `skills-lock.json`.

- [ ] **Step 2: Verify**

Run: `node tools/check-docs.js` then `node --test "tools/test/**/*.test.js"`
Expected: `OK: 2 doc pair(s) in step ... no emoji in N .md file(s).` and the pass count unchanged.

- [ ] **Step 3: Check every role is present**

List `.agents/skills/agent-*/SKILL.md` and confirm exactly four files, each with a frontmatter `name` equal to its folder. This is the "push the agents on GitHub" check: nothing may be left untracked.

- [ ] **Step 4: Commit and push**

```bash
git add .agents/skills/README.md
git commit -m "Index the team of agents in the skills README"
git push origin master
```

- [ ] **Step 5: Confirm the roster is on the remote**

Run: `git status --short .agents/skills` then `git log --oneline -4 -- .agents/skills/agent-coordinator .agents/skills/agent-investigator .agents/skills/agent-coder .agents/skills/agent-publisher`
Expected: no untracked or modified `agent-*` file, and the three commits from Tasks 1 to 3 in the log. The branches this pushes to are `origin/master`.

---

## Self-Review

**1. Spec coverage.** The request names four roles and one of them "pusha pure gli agents su github": the investigator and coder are Task 2, the publisher with its roster step is Task 2 and Task 3, and the coordinator - the one who "coordina tutti e riceve/manda le informazioni" - is Task 1, with its handoff section carrying the information between roles.

**2. Step scan.** Each step is one action with one checkable result: write the frontmatter, write the named sections, reason through one input, verify, commit. The plan pins the frontmatter `name` and `description` because they are what the harness matches on, and the section titles because they are the interface between the coordinator and the roles. It does not write the prose, which the implementer writes.

**3. Type consistency.** The four folder names are `agent-coordinator`, `agent-investigator`, `agent-coder`, `agent-publisher`, and every reference in Tasks 1 to 3 uses those exact names - the coordinator's description, its roster table, the index table and the `git log` paths all agree.

**4. Review Focus.** Each line names its owner: the no-code prompt and the missing role (Task 1 Steps 3 and 4), the not-found report (Task 2 Step 1), the failing gate (Task 2 Step 3), the mirror and the roster push (Task 2 Step 3 and Task 3 Step 3).

**5. Proportion.** The plan is shorter than the four files it describes and does not print their bodies, because the body's prose is the implementer's; what the plan fixes is the names, the frontmatter and the sections, which are the parts the harness and the coordinator see.
