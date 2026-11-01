# Working in this repository

## Commits

- **Short, human, one line.** The subject says what changed, in the imperative,
  and stops at about 60 characters: `Search sixteen padding tails`, not a
  paragraph about why. Detail belongs in the code and in the pull request, not in
  the git log.
- **No AI attribution of any kind.** No `Generated with ...` line, no
  `Co-Authored-By: ...` trailer, no emoji footer. If a tool offers to add one,
  the answer is no.
- **No conventional-commit prefixes invented after the fact.** The history mixes
  them; a commit either follows its neighbour's style or has no prefix at all.
- One commit per change that stands on its own. A build bump is its own commit,
  a fix and its test are one.

## The rest

The rules for the code itself live in `.agents/skills/maintain-the-app/SKILL.md`
and the gates in `.agents/skills/test-the-app/SKILL.md`. They are the contract
this file does not repeat.
