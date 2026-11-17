'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const source = fs.readFileSync(
  path.join(ROOT, 'WhatsappApp/Protocol/Transport/CallMediaSocket.cs'), 'utf8');

const method = source.slice(source.indexOf('public async Task<int> ProbeAsync('));
const body = method.slice(0, method.indexOf('private bool AnswerProbe('));

test('the registration key is the probe\'s own transaction id', () => {
  // In-flight probes were keyed by host:port with a silent overwrite, so a second
  // probe of the same relay orphaned the first waiter and the first probe's
  // cleanup removed the second probe's registration - a reply that then matched
  // nothing and a relay measured as dead though it answered. A transaction id is
  // unique per probe, which is exactly what the answer is matched by: two probes
  // of one relay then keep their own entries and every started probe completes.
  const register = body.indexOf('_probes[');
  assert.ok(register >= 0, 'the probe is registered somewhere');
  assert.ok(!/string key = host \+ ":" \+ port/.test(body),
    'the target is not the registry key');
  assert.match(body, /key = [^\n;]*transactionId/,
    'the registry key comes from the transaction id');
  assert.match(body, /_probes\[key\] = probe/,
    'the probe is registered under that key');
});

test('a probe cleans up only its own registration', () => {
  // The finally of a timed-out probe used to remove whatever entry sat under the
  // shared key - which after an overlap was the newer probe's. The removal has to
  // name the entry this probe registered, and nothing else.
  const tail = body.slice(body.indexOf('finally'));
  assert.match(tail, /_probes\.Remove\(key\)/,
    'the cleanup removes the key this probe registered');
});

test('every started probe completes', () => {
  // A waiter nobody completes is a caller that hangs past its own deadline. Each
  // probe waits on its own completion source against a timeout, the answer
  // completes the probe it matched, and closing the socket completes the rest.
  assert.match(body, /Task\.WhenAny\(probe\.Answer\.Task/,
    'the wait races the answer against a deadline');

  const answer = source.slice(source.indexOf('private bool AnswerProbe('));
  assert.match(answer, /TrySetResult\(/,
    'a matched answer completes its probe');

  const dispose = source.slice(source.indexOf('public void Dispose()'));
  assert.match(dispose, /TrySetResult\(-1\)/,
    'a closing socket completes the probes it can no longer answer');
});
