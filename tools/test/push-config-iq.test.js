// The push registration the phone owes the server at login: the channel the
// platform gives this device, and the `urn:xmpp:whatsapp:push` config iq that
// tells WhatsApp where to push. Both shapes are the client reference's own
// (`MpnsForeground.cs`, `AppState.SendPushConfig` -> `FunXMPP.SendClientConfig`,
// `Mpns.ClientConfig`), so the contract pins them here before any code exists.
"use strict";

const test = require("node:test");
const assert = require("node:assert");
const fs = require("node:fs");
const path = require("node:path");

const ROOT = path.resolve(__dirname, "..", "..");
const CLIENT = path.join(ROOT, "WhatsappApp", "Protocol", "Client", "ClientState.cs");
const CHANNEL = path.join(ROOT, "WhatsappApp", "Services", "PushChannel.cs");
const CONN = path.join(ROOT, "WhatsappApp", "Services", "NativeConnection.cs");

function read(p) { return fs.readFileSync(p, "utf8"); }

test("the push config iq carries the channel uri and the client's config", () => {
  const src = read(CLIENT);

  // The namespace is the reference's, verbatim: `urn:xmpp:whatsapp:push`
  // (`FunXMPP.SendClientConfig`).
  assert.ok(/PushNamespace\s*=\s*"urn:xmpp:whatsapp:push"/.test(src),
    "ClientState must name the push namespace `urn:xmpp:whatsapp:push`");

  const at = src.indexOf("PushConfigIq(");
  assert.ok(at >= 0, "ClientState must build the push config iq");
  const slice = src.slice(at, src.indexOf("\n        }", at));

  // The envelope: an `iq` set to the server on the push namespace.
  assert.ok(/Attrs\.Add\("to",\s*PairingSession\.ServerJid\)/.test(slice), "the iq goes to the server jid");
  assert.ok(/Attrs\.Add\("type",\s*"set"\)/.test(slice), "the iq is a set");
  assert.ok(/Attrs\.Add\("xmlns",\s*PushNamespace\)/.test(slice), "the iq travels on the push namespace");

  // The `config` child: the channel uri as `id`, and the attribute pairs the
  // reference sends (`Mpns.ClientConfig`): platform, lg, lc, clear, preview,
  // default, groups, version.
  assert.ok(/new BinaryNode\("config"\)/.test(slice), "the child is a config node");
  assert.ok(/Attrs\.Add\("id",\s*channelUri\)/.test(slice), "the config id is the channel uri");
  assert.ok(/Attrs\.Add\("platform",\s*"microsoft"\)/.test(slice), "the platform is microsoft");
  assert.ok(/Attrs\.Add\("lg",/.test(slice), "the language goes as lg");
  assert.ok(/Attrs\.Add\("lc",/.test(slice), "the locale goes as lc");
  assert.ok(/Attrs\.Add\("clear",\s*"0"\)/.test(slice), "clear is 0");
  assert.ok(/Attrs\.Add\("preview",/.test(slice), "the preview flag goes as preview");
  assert.ok(/Attrs\.Add\("default",\s*"1"\)/.test(slice), "default is 1");
  assert.ok(/Attrs\.Add\("groups",\s*"1"\)/.test(slice), "groups is 1");
  assert.ok(/Attrs\.Add\("version",\s*"2"\)/.test(slice), "version is 2 (no voip task is scheduled here)");
});

test("the channel is the platform's own, named like the reference's", () => {
  const src = read(CHANNEL);

  // `HttpNotificationChannel("message", "mpns.whatsapp.net")` — the name and the
  // service of the client reference (`MpnsForeground`).
  assert.ok(/ChannelName\s*=\s*"message"/.test(src), "the channel is named `message`");
  assert.ok(/PushService\s*=\s*"mpns.whatsapp.net"/.test(src), "the service is `mpns.whatsapp.net`");

  // The channel is bound to the system's toast and tile, and its uri is observed
  // (`BindPush`, `OnChannelUriChanged`).
  assert.ok(src.includes("BindToShellToast"), "the channel is bound to the shell toast");
  assert.ok(src.includes("ChannelUriUpdated"), "the uri is observed");

  // A URI exists for the login to send: the channel exposes it and says when it
  // changed, so the registration can be repeated without a new login.
  assert.ok(/UriChanged/.test(src), "the channel says when its uri changes");
});

test("a new push channel is opened before it is bound to shell notifications", () => {
  const src = read(CHANNEL);
  const start = src.slice(src.indexOf("public static void Start()"), src.indexOf("private static void OnChannelUriUpdated"));
  const open = start.indexOf("channel.Open()");
  const toast = start.indexOf("channel.BindToShellToast()");
  const tile = start.indexOf("channel.BindToShellTile()");

  assert.ok(open >= 0, "a new channel is opened");
  assert.ok(toast >= 0 && tile >= 0, "the channel is bound to toast and tile notifications");
  assert.ok(open < toast && open < tile,
    "WP8.1 requires Open before BindToShellToast and BindToShellTile on a new channel");
});

test("the registration goes out at login, when the phone has a channel uri", () => {
  const src = read(CONN);

  const at = src.indexOf("private async Task AnnounceClientStateAsync(");
  assert.ok(at >= 0, "the login's sentences are sent by AnnounceClientStateAsync");
  const slice = src.slice(at, src.indexOf("\n        }", at));

  // Third sentence: the push config, after the client state the login already
  // owes. It is only owed when the platform gave this device a channel uri.
  assert.ok(slice.includes("ClientState.PushConfigIq"), "AnnounceClientStateAsync sends the push config");
  assert.ok(slice.includes("PushChannel.ChannelUri"), "the config goes only with a channel uri in hand");
});