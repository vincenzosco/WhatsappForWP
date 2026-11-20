# Translation kit for non-programmers — design

## Goal

Let somebody who has never opened a code editor translate this app into
their language and submit the translation through GitHub alone. The whole
kit lives in this repository and is reachable from GitHub: a folder of
plain-text files, one command-line tool, an issue template, and a small
GitHub Pages site that explains the process.

The app side is already done: `Loc.Get` resolves strings with
`CultureInfo.CurrentUICulture`, so a new `Strings/Resources.<culture>.resx`
in `WhatsappApp.Core` is picked up automatically on a phone set to that
language. No application code changes at all.

## Non-goals

- No web form, no backend, no machine translation.
- No changes to `Loc.cs` or to any page.
- No automatic pull-request merging: the maintainer runs the build command
  and reviews the resulting `.resx`. A GitHub Action that does this for
  every incoming pull request is a possible follow-up, not part of this work.

## The translator's surface

One text file per language, in `traduzioni/`: every line is one string,

```
Nav_Chats = Chats
Nav_Status = Status
```

The left side is the key (never edited), the right side is the reference
English and is replaced by the translation. Translating only part of the
file is fine: untranslated keys stay English at runtime, because the
resource lookup falls back to the neutral catalog on its own. Empty lines
and lines starting with `#` are ignored.

`traduzioni/`:

- `README.md` (English) and `README.it.md` (Italian) — the guide, written
  for somebody who does not program, covering GitHub's web editor and the
  "propose changes" button.
- `MODELLO.txt` — the template: every key with its English value, generated
  by the tool so it can never drift from the real catalog.
- `ESEMPIO.txt` — a short example of a finished file.

## The tool

`tools/translation-kit.js`, run with plain `node`, no dependencies. It
exports its parsing and generation functions so the tests can drive them
directly.

- `export` — regenerates `traduzioni/MODELLO.txt` from the neutral catalog.
- `build <file> <culture> [--root <dir>]` — turns a translated file into
  `WhatsappApp.Core/Strings/Resources.<culture>.resx` and registers it in
  `WhatsappApp.Core/WhatsappApp.Core.csproj` exactly like the existing
  entry:

  ```xml
  <EmbeddedResource Include="Strings\Resources.<culture>.resx">
    <Culture><culture></Culture>
    <WithCulture>true</WithCulture>
  </EmbeddedResource>
  ```

  The registration is idempotent. `--root` points at a different checkout,
  which is how the tests avoid touching the repository. The culture must
  read like `fr` or `fr-FR`; the guide pushes the generic form, because one
  `fr.resx` serves every French phone while `fr-FR.resx` serves only France.

- `status [--root <dir>]` — writes `site/status.json`: one entry per
  language in the catalog, with the count of translated strings and the
  percentage, for the site's language table.

Validation, all with the offending line number:

- Unknown key — error.
- Duplicate key — error.
- Placeholder mismatch — error. `{0}` and friends are format slots
  (`{0} unread messages`): a translation that drops or renames one corrupts
  the runtime string. The tool compares the placeholder sets of source and
  translation and refuses to build when they differ.

Everything else is a warning at most: a translation cannot break the app
by being incomplete.

## The site

`site/`, served by GitHub Pages through a workflow that deploys that
folder. One page, bilingual (English/Italian switch), with the three
steps, the live language table from `site/status.json`, a short FAQ (what
to do with `{0}`, what happens to untranslated lines, how a language is
added), and links to the repository and to the issue page.

## The issue path

`.github/ISSUE_TEMPLATE/traduzione.md`: for translators who prefer to
paste their file into an issue instead of touching files at all.

## READMEs

Each `README*.md` gains a short "help translate" section pointing at
`traduzioni/` and the site.

## Testing

`tools/test/translation-kit.test.js` drives the exported functions:

- the template lists every key of the neutral catalog, in order;
- a build round-trips values, escapes XML in them, and keeps only the
  translated keys;
- untranslated keys are simply absent from the generated `.resx`;
- unknown and duplicate keys are refused with the line number;
- a placeholder mismatch is refused;
- the csproj registration is added once and stays idempotent;
- `status` counts the languages it finds.

The existing gates (`check-resw` extended to require every
`Strings/Resources*.resx` to be registered) keep the kit honest, and the
`node --test tools/test/` suite must stay green.