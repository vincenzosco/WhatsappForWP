---
name: agent-coordinator
description: Use at the start of any prompt that should be carried out by the team of agents instead of one pass. It decides which roles run, in what order, and what each hands to the next, and it does not finish until the publisher has pushed. Loads agent-investigator, agent-coder and agent-publisher by name.
---

# The coordinator

## What this is

The roster is four skills, one per role. This one coordinates: it reads the
prompt, decides which roles run, hands the facts from each role to the next, and
reports at the end. It changes no file of its own - the coder changes the code
and the publisher changes the repository and the documents.

It exists because one pass that investigates and edits and pushes at the same
time mixes three jobs, and the repository pays for it: a fix with no evidence, a
commit with no gate, a push with no document. The roles are separate so each has
one job and one stopping point.

The roles are skills, not processes: this harness runs the work in this same
session, so "load" means load the role's skill and follow it, in order.

## The roster

| Skill | Receives | Hands back |
| --- | --- | --- |
| [`agent-investigator`](../agent-investigator/SKILL.md) | the prompt, the log, the state of the tree | a report: the evidence with `file:line`, the likely cause, what it ruled out |
| [`agent-coder`](../agent-coder/SKILL.md) | the investigator's report | the changed tree and the fast gate output |
| [`agent-publisher`](../agent-publisher/SKILL.md) | the changed files and the gate result | the commits, the pushes, the updated documents |
| `agent-coordinator` | the prompt | the protocol below and the final report |

## The protocol, per prompt

1. Read the prompt and the state of the tree (`git status --short`, the files
   the prompt names). If the prompt names a log, it is evidence, not decoration.
2. Load `agent-investigator` and produce its report. That report is the shared
   truth for the rest of the run; nothing after this step re-decides the facts.
3. Decide whether a change is due. If the prompt asks a question, or wants the
   log read, or wants the cause named, answer from the report and **stop**: no
   coder, no publisher.
4. If a change is due, load `agent-coder` and carry out its rules on the report.
   The result is the changed tree plus the fast gate output.
5. Load `agent-publisher` and carry out its rules. The result is the commits,
   the pushes and the updated documents.
6. Report to the user: what was investigated, what changed, the commit hashes,
   what was pushed, and what still needs a phone run.

## Handoffs

What travels between roles is fixed, so a role never guesses:

- investigator to coder: the report, unmodified. The coder does not re-investigate.
- coder to publisher: the list of changed files and the fast gate result.
- publisher to coordinator: the commit hashes, the branch, and whether the Docker
  mirror was due and pushed.
- coordinator to the user: the six lines of step 6.

## When a role is skipped

A prompt that only needs reading stops after step 3. A change already committed
and pushed goes straight to the publisher's verification. The coder and the
publisher are never run "just in case": a role that runs for nothing produces a
commit for nothing.

## The roster is code too

A change to any `.agents/skills/agent-*` file is itself a change: it goes through
the coder if it is written here, and it is committed and pushed through the
publisher like any other file. "Push the agents on GitHub" is this rule.

## Stop conditions

Stop and say so, without substituting a guess for a role:

- a role's `SKILL.md` is missing or renamed: name it and stop, do not skip it;
- the investigator answers "not found": report what it ruled out and stop;
- the publisher's gate, test or ARM build fails: report the output and commit
  nothing.
