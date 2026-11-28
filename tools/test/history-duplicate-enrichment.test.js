'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const data = fs.readFileSync(path.join(ROOT, 'WhatsappApp/Services/DataService.cs'), 'utf8');
const start = data.indexOf('private void AddHistoryMessage');
const end = data.indexOf('private static int InOrderIndex', start);
const addHistory = data.slice(start, end);
const mergeStart = data.indexOf('private static void MergeHistoryMessage');
const mergeEnd = data.indexOf('private static int InOrderIndex', mergeStart);
const merge = mergeStart < 0 ? '' : data.slice(mergeStart, mergeEnd);

test('history enriches an existing cached row without resetting its delivery status', () => {
  assert.match(addHistory, /MergeHistoryMessage\(list\[i\],\s*message\)/,
    'the duplicate-id path merges new history fields into the existing row');
  assert.doesNotMatch(addHistory, /if\s*\(list\[i\][\s\S]{0,100}message\.Id\)\s*return;/,
    'the duplicate-id path does not discard the richer history row');
  assert.match(merge, /existing\.Media\s*=\s*incoming\.Media/,
    'the cached media row gets the protocol keys needed to fetch its bytes');
  assert.match(merge, /existing\.IsViewOnce\s*=\s*existing\.IsViewOnce\s*\|\|\s*incoming\.IsViewOnce/,
    'a view-once marker discovered in history is not lost');
  assert.doesNotMatch(merge, /existing\.Status\s*=/,
    'merging history never overwrites a receipt status already on the row');
});
