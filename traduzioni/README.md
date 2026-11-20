# Translate the app - no programming needed

You do not need to write code, install anything, or even have a computer.
If you can use GitHub in a browser, you can translate this app into your
language.

The app has **222 short strings** - buttons, menus, notifications. Today
they exist in English and Italian. Your language can be next.

## What you will do

There is one text file to fill in. Every line looks like this:

```
Nav_Chats = Chats
```

The left part is a name you never touch. The right part is the English
original: **write your language over it**, on the same line.

You can translate the whole file or just ten lines - whatever you skip
stays English inside the app until someone finishes it.

## The five steps

1. **Open the template.** It is [`MODELLO.txt`](MODELLO.txt) in this
   folder. Click the button that says *Raw*, then select everything and
   copy.

2. **Translate it somewhere comfortable.** A notes app, a text editor, a
   reply to this repository - anywhere you can type. Keep the
   `Key = ` part untouched and write your translation after it.

3. **Name the file after your language.** French is `fr.txt`, Spanish is
   `es.txt`, Brazilian Portuguese is `pt.txt`. If you are not sure which
   two letters to use, do not worry - say the language name in the next
   step and somebody will pick the code for you.

4. **Send it on GitHub.** Either way works:
   - In this folder press *Add file* then *Create new file* (or upload
     yours), name it like in step 3, press **Propose changes** and then
     **Create pull request**; or
   - open [a new issue of the "translation" kind](../../issues/new/choose),
     paste your lines in the box and send it.

5. **That is it.** One of the maintainers runs one command that turns your
   file into the app resource and tells us how much you translated.

## Things people ask

**What about `{0}` in some lines?**
Those are slots the app fills in later - `{0} unread messages` becomes
"5 unread messages". Keep every `{0}` and `{1}` exactly as it is in the
original; the rest is yours.

**What if I make a mistake?**
Nothing bad can happen. The app refuses to use a file with a broken line
or a missing `{0}`, and the tool tells the maintainer which line to look
at. A partial translation is always welcome.

**Should I translate the words in CAPS?**
Yes, they are menu labels. If your language has no capitals, write it the
natural way.

**What file do I open to see the app in my language later?**
None. The app follows the phone's language automatically.

**Can I help with something else?**
Yes - the [README](../../README.md) says what the project needs:
maintainers, reviews, and documentation.

---

*This file is a guide, not part of the app. The app's own strings live in
`WhatsappApp.Core/Strings/`.*