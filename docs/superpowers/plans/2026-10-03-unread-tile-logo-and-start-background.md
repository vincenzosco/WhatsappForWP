# Unread, tile logo and Start background Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A read chat stops showing an unread number, the live tile draws the app mark at a sane size instead of zoomed/cut, and the tile lets the user's WP8.1 Start background show through.

**Architecture:** Three independent faults. (1) The `read` frame is sent once, on a socket that can already be dead, and the number is also kept in the on-disk chat cache: the fix moves the wire half of "read" into `DataService`, queues it while there is no live socket, flushes it on `ConnectionEstablished`, and stops persisting the count. (2) `make-brand-assets.js` draws the tile mark at 100 % of the tile, so its own 5 % margin is all there is: the mark gets the same safe inner size the manifest logos already use, and the two `TileIcon` PNGs are regenerated. (3) The tile background is the manifest's opaque `#075E54`, which hides the Start background: it becomes `transparent`, which is what WP8.1 reads as "show the Start background through this tile".

**Tech Stack:** C# 5 / XAML on Windows Phone 8.1 (WinRT), Node.js guards in `tools/` (`node:test`), ImageMagick 7 for the artwork.

**Spec:** This plan. The three requirements come from the user report: unread numbers remain after every chat is read; the tile logo is cut in half as if zoomed; the tile must integrate with the user's Start background when one is set.

## Global Constraints

- Windows Phone 8.1 **XAML (WinRT)** app. **C# 5 only** - no `nameof`, no string interpolation, no expression-bodied members, no auto-property initializers, no `out int x`. Guard: `node tools/check-csharp5.js`.
- Every call fired without `await` goes through `Guarded.RunGuardedAsync` (guard: `node tools/check-fire-and-forget.js`).
- Every user-visible string lives in **both** `.resw` files (guard: `node tools/check-resw.js --strict`). This plan adds no string.
- Docs come in pairs (`README.md` + `README.it.md`, `WhatsappBridge/README.md` + `WhatsappBridge/README.it.md`), same headings, same order, `## Disclosure` last in the root pair. Guard: `node tools/check-docs.js`.
- Tile assets: `.png`, **<= 1024x1024 px and <= 200 KB**, the file itself **at least 200x200**. Guard: `node tools/check-tile.js`.
- The adapter (`WhatsappBridge/`) is unchanged by this plan: no Docker mirror is needed.
- Fast gate, green before every commit:

  ```bash
  node tools/check-csharp5.js && node tools/check-icons.js \
    && node tools/check-resw.js --strict && node tools/check-docs.js \
    && node tools/check-framing.js && node tools/check-tile.js \
    && node tools/check-memory.js && node tools/check-actions.js \
    && node tools/check-fire-and-forget.js && node tools/check-project-files.js \
    && node tools/check-chat-list-source.js \
    && node --test "tools/test/**/*.test.js"
  cd WhatsappBridge && npm test
  ```

  `download.test.js` already fails on this machine for a missing `zip` binary (4 failures, unrelated to this plan).
- The C# build gate runs on the Windows/Parallels VM, not here.
- Commit messages are English, `type: short imperative`, no apostrophe. One commit per task.

---

### Task 1: A chat that was read stops showing its number

The count lives in two places: the adapter's `unreadByChat` (the truth, cleared by the `read` frame) and `ChatCache` on the phone. Two holes let a cleared number come back.

- **The `read` frame is fired once, into a socket that may be dead.** `ChatPage.MarkRead` guards on `CommunicationService.Instance.IsConnected`, and that flag stays `true` on a socket the OS already closed (see the watchdog gotcha). The frame is lost, the adapter keeps counting, and the next `chats` reply puts the number back. Nothing re-sends it.
- **The count is persisted and re-applied.** `ChatCache.Slim` writes `UnreadCount` and `DataService.LoadCachedChatsAsync` feeds it back through `ApplyChat`, so a number the user already cleared can reappear from disk before (or without) the server's answer.

**Files:**
- Modify: `WhatsappApp/Services/DataService.cs` (`Start`, `ClearUnread`, new `SendRead` / `OnConnectionEstablished`, `ApplyChat`)
- Modify: `WhatsappApp/Services/ChatCache.cs` (`Slim`)
- Modify: `WhatsappApp/Pages/ChatPage.xaml.cs` (`MarkRead`)
- Test: the fast gate plus device checklist item 20/21 in `.agents/skills/test-the-app/SKILL.md`

**Interfaces:**
- Consumes: `CommunicationService.Instance.IsConnected` (bool), `CommunicationService.Instance.ConnectionEstablished` (`EventHandler`), `CommunicationService.Instance.SendControlAsync(string, string)`, `Guarded.RunGuardedAsync(string, Task)`.
- Produces: `DataService.ClearUnread(string)` now owns the wire half of "read"; no new public type.

- [ ] **Step 1: Give `DataService` the pending reads**

In `WhatsappApp/Services/DataService.cs`, next to `_historyRequested`, add:

```csharp
        // The chats the reader has cleared while the socket was not usable. The
        // `read` frame is re-sent for each of them as soon as a connection is
        // established: a frame written into a socket the OS already closed is
        // lost, and without this the adapter kept counting a chat that had been
        // read and put the number back at the next list.
        private readonly HashSet<string> _pendingReads = new HashSet<string>();
```

In `Start()`, after the two `MessageReceived`/`ControlMessageReceived` subscriptions, add:

```csharp
            CommunicationService.Instance.ConnectionEstablished += OnConnectionEstablished;
```

- [ ] **Step 2: Send the read, or queue it**

Replace the body of `ClearUnread` so it owns the wire half, and add the two helpers next to it:

```csharp
        public void ClearUnread(string chatId)
        {
            var contact = FindContact(chatId);
            if (contact != null) contact.UnreadCount = 0;

            SendRead(chatId);
            NotificationService.SetUnread(TotalUnread());
        }

        /// <summary>
        /// Tells the adapter that a conversation has been read, or remembers to
        /// tell it. The check is on the socket, not on `IsConnected` alone: that
        /// flag is true on a connection the OS already closed, and the frame
        /// would be written into nothing. A failed send is queued and retried
        /// when a connection is established, so a read is never lost.
        /// </summary>
        private void SendRead(string chatId)
        {
            if (string.IsNullOrEmpty(chatId)) return;

            if (!CommunicationService.Instance.IsConnected)
            {
                _pendingReads.Add(chatId);
                return;
            }

            _pendingReads.Remove(chatId);
#pragma warning disable 4014
            Guarded.RunGuardedAsync("DataService/read",
                CommunicationService.Instance.SendControlAsync("read", chatId));
#pragma warning restore 4014
        }

        /// <summary>
        /// A live connection exists: every read that could not be sent goes out
        /// now, in the order it was made.
        /// </summary>
        private void OnConnectionEstablished(object sender, EventArgs e)
        {
            if (_pendingReads.Count == 0) return;

            var chats = new List<string>(_pendingReads);
            _pendingReads.Clear();
            for (int i = 0; i < chats.Count; i++) SendRead(chats[i]);
        }
```

- [ ] **Step 3: Stop `ChatPage` from sending the frame itself**

In `WhatsappApp/Pages/ChatPage.xaml.cs`, replace `MarkRead` with:

```csharp
        /// <summary>
        /// This conversation has been shown: the number is cleared here and on the
        /// server. DataService owns both halves now: it clears the row and sends
        /// the `read` frame, or queues it when the socket is not usable, so the
        /// call cannot be lost on a connection the OS already closed.
        /// </summary>
        private void MarkRead()
        {
            DataService.Instance.ClearUnread(_contact.Id);
        }
```

- [ ] **Step 4: Stop resurrecting the count from disk**

In `WhatsappApp/Services/ChatCache.cs`, remove `UnreadCount` from `Slim` so the snapshot no longer carries it, and update the class comment to say why:

```csharp
        /// <summary>
        /// The copy of a row with only the fields the list draws.
        ///
        /// The unread count is not here on purpose. It is a live number the
        /// adapter owns and clears with the `read` frame; a copy of it on disk is
        /// a number nobody has verified since, and re-applying it brought back a
        /// count the reader had already cleared.
        /// </summary>
        private static ChatMessage Slim(ChatMessage row)
        {
            return new ChatMessage
            {
                ChatId = row.ChatId,
                SenderName = row.SenderName,
                Text = row.Text,
                IsGroup = row.IsGroup,
                Timestamp = row.Timestamp
            };
        }
```

- [ ] **Step 5: The open chat never shows a number**

In `DataService.ApplyChat`, just before the line that assigns the count from the server row, add the guard:

```csharp
            // The open chat is being read: a server row that still counts it
            // (the `read` frame has not been processed yet) must not put a number
            // on a row the reader is looking at. It is the same rule the toast
            // follows, applied to the row.
            contact.UnreadCount = message.ChatId == _activeChatId ? 0 : message.UnreadCount;
```

and delete the old `contact.UnreadCount = message.UnreadCount;` line.

- [ ] **Step 6: Run the fast gate**

Run: `node tools/check-csharp5.js && node tools/check-fire-and-forget.js && node tools/check-memory.js`
Expected: all three print their OK line and exit 0.

- [ ] **Step 7: Commit**

```bash
git add WhatsappApp/Services/DataService.cs WhatsappApp/Services/ChatCache.cs WhatsappApp/Pages/ChatPage.xaml.cs
git commit -m "fix: a read chat cannot get its number back"
```

---

### Task 2: The tile mark is drawn at a safe size

`make-brand-assets.js` resizes the mark to the full tile (`TileIcon.png` = 200, `TileIcon.scale-240.png` = 480). The mark's own margin inside the glyph is about 5 %, so on a 150 px tile it is nearly edge to edge and reads as zoomed and cut. Every other asset already uses a `0.82-0.88` factor through `compose`; the tile icon is the one that does not.

**Files:**
- Modify: `tools/make-brand-assets.js` (the tile-icon block)
- Regenerate: `WhatsappApp/Assets/TileIcon.png`, `WhatsappApp/Assets/TileIcon.scale-240.png`

**Interfaces:**
- Consumes: the existing `buildGlyph()` and `magick(args)` helpers.
- Produces: the same two paths, with the mark at the safe inner size.

- [ ] **Step 1: Draw the mark inside the tile, on transparency**

In `tools/make-brand-assets.js`, replace the tile-icon loop with a transparent compose at the same factor the manifest logos use:

```js
  // L'icona della tile (modello IconWithBadge): il marchio bianco su sfondo
  // trasparente. La misura interna e' la stessa proporzione dei logo del
  // manifest: a tutta tile il margine del glifo (circa il 5%) e' tutto quello
  // che resta, e su una tile da 150 px il disegno sembra tagliato e ingrandito.
  const TILE_MARK = 0.74;
  const tileIcons = [['TileIcon.png', 200], ['TileIcon.scale-240.png', 480]];
  for (const [name, size] of tileIcons) {
    magick(['-size', `${size}x${size}`, 'xc:none',
      '(', glyph, '-resize', `${Math.round(size * TILE_MARK)}x${Math.round(size * TILE_MARK)}`, ')',
      '-gravity', 'center', '-composite', '-depth', '8',
      path.join(OUT_DIR, name)]);
  }
```

- [ ] **Step 2: Regenerate the two assets**

Run: `node tools/make-brand-assets.js`
Expected: the file list prints `TileIcon.png` and `TileIcon.scale-240.png` with their byte sizes; both stay well under 200 KB.

- [ ] **Step 3: Check the tile guard and the new alpha bounds**

Run: `node tools/check-tile.js`
Expected: `OK: ms-appx:///Assets/TileIcon.png (200x200, N KB), src on every binding.`

Then confirm the mark is inset, not full-bleed: the opaque alpha bounds of `TileIcon.scale-240.png` must start well inside the frame (roughly 60..420 of 480) rather than 26..439.

- [ ] **Step 4: Commit**

```bash
git add tools/make-brand-assets.js WhatsappApp/Assets/TileIcon.png WhatsappApp/Assets/TileIcon.scale-240.png
git commit -m "fix: draw the tile mark inside the tile, not edge to edge"
```

---

### Task 3: The tile lets the Start background through

On WP8.1 the tile background is the manifest's `BackgroundColor`, and it is opaque `#075E54`, so the Start photo can never show through. A transparent background colour is what the platform reads as "this tile shows the Start background"; the tile payload image (`TileIcon`, already transparent) is drawn on top of it. Nothing else changes: the shell decides what is behind the tile.

**Files:**
- Modify: `WhatsappApp/Package.appxmanifest` (`m3:VisualElements/@BackgroundColor`)
- Modify: `README.md`, `README.it.md` (the tile paragraph)

**Interfaces:**
- Consumes: nothing.
- Produces: a tile whose background is transparent, so the user's Start background shows through it.

- [ ] **Step 1: Make the tile background transparent**

In `WhatsappApp/Package.appxmanifest`, change:

```xml
<m3:VisualElements ... ForegroundText="light" BackgroundColor="#075E54">
```

to:

```xml
<m3:VisualElements ... ForegroundText="light" BackgroundColor="transparent">
```

`transparent` is the value WP8.1 reads as "show the Start background behind this tile". With no Start background set, the shell shows the theme's Start colour instead, which is what every other transparent tile on the phone does.

- [ ] **Step 2: Say it in the docs**

In `README.md`, in the tile paragraph that describes `TileIcon.png`, add one sentence: the manifest background is `transparent` so the user's Start background shows through the tile, and the tile asset is transparent for the same reason. Add the same sentence in the same place in `README.it.md`.

- [ ] **Step 3: Run the docs guard**

Run: `node tools/check-docs.js`
Expected: OK.

- [ ] **Step 4: Commit**

```bash
git add WhatsappApp/Package.appxmanifest README.md README.it.md
git commit -m "feat: let the Start background show through the tile"
```

---

## What execution changed about this plan

- **Task 1** also guards `ApplyChat`: a server row that still counts the open
  chat now writes `0` for it. Without this, the adapter's reply (which is sent
  before it processes the `read` frame) put the number back on the row the
  reader was looking at.
- **Task 2** was executed without ImageMagick, which is not installed on this
  machine: the two PNGs were regenerated with a one-off Node resample of the
  committed asset (`/tmp/inset-tile.js`), and `make-brand-assets.js` was changed
  to the same `0.82` factor so a later `magick` run reproduces them byte for
  byte in spirit. The generator change is therefore unverified by an actual
  `magick` run.
- **Task 3** also updated `.agents/skills/release-the-app/SKILL.md`, whose
  manifest table still said `#075E54`.
