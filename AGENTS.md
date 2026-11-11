# Working in this repository

## Commits

- **Always commit.** A change that builds, passes the gates and stands on its
  own is committed before the work is called done. It is not left in the working
  tree for the user to stage, and it is not carried along into the next task.
- **Short, human, one line.** The subject says what changed, in the imperative,
  and stops at about 60 characters: `Search sixteen padding tails`, not a
  paragraph about why. Detail belongs in the code and in the pull request, not in
  the git log.
- **One line is the whole message.** If a second line cannot be avoided, it is
  one sentence. Three paragraphs of context under a subject is not a commit
  message, it is a postmortem filed in the wrong place.
- **No AI attribution of any kind.** No `Generated with ...` line, no
  `Co-Authored-By: ...` trailer, no emoji footer. If a tool offers to add one,
  the answer is no.
- **Who authors what.** A commit the owner makes carries the owner's identity
  (`vincenzosco <131459463+vincenzosco@users.noreply.github.com>`); a commit made in
  this cloud workspace stays under the workspace identity
  (`SouSand <sousand@proton.me>`). Never commit as the owner from here.
- **No conventional-commit prefixes invented after the fact.** The history mixes
  them; a commit either follows its neighbour's style or has no prefix at all.
- One commit per change that stands on its own. A build bump is its own commit,
  a fix and its test are one.

## Skills and agents

- **Global skills** (`C:/Users/Vincenzo/.agents/skills`, tracked by
  `.skill-lock.json`) are refreshed with `npx skills update -g -y`, and
  `npx skills ls` says what is installed. Run it when a skill looks stale, not on
  a timer, and say what changed.
- **This repository's skills** (`.agents/skills/`, committed) are not updated by
  any tool: they describe this app, and no upstream has them. They change when
  the work changes them, on request, with the same review as code.
- An agent that edits a project skill commits it like any other change, and
  names what it changed and why.

## Comments in the code

- **Every comment is in English.** C#, C, JavaScript, the batch files. The app's own
  strings stay Italian, because they are what a person reads on the phone, and a
  comment that quotes one quotes it verbatim in quotes or backticks.
- **A comment explains the code, not the session that produced it.** No dates, no
  "the phone answered 0x8007007F yesterday", no account of what was tried and
  dropped. That belongs in the commit message and in `docs/`.
- **No rhetorical scaffolding.** `Why it exists:`, `What it is not:` and the like
  open most blocks like a template; say the thing instead.
- **No emoji**, in comments or in commit messages.
- `node tools/check-comments.js` enforces the mechanical half: Italian prose, dates,
  emoji and those template openers fail the gate. It is strict for `WhatsappApp`,
  `WhatsappApp.Tasks` and `ProtocolTests`; the scripts in `tools/` still carry Italian
  headers written before this rule, are counted as debt, and are translated when touched.

## The rest

The rules for the code itself live in `.agents/skills/maintain-the-app/SKILL.md`
and the gates in `.agents/skills/test-the-app/SKILL.md`. They are the contract
this file does not repeat.
