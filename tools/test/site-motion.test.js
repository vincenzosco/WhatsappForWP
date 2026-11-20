'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const SITE = path.join(__dirname, '..', '..', 'site', 'index.html');
const html = fs.readFileSync(SITE, 'utf8');

test('the page settles in and stands still for who asks for stillness', () => {
  // A reveal that never becomes visible is a blank page for anyone who set
  // "reduce motion": the guard is not only "no animation", it is "already there".
  assert.match(html, /@media \(prefers-reduced-motion: reduce\)/);
  assert.match(html, /@media \(prefers-reduced-motion: reduce\)[\s\S]*?\.reveal \{[^}]*opacity: 1/);
  assert.match(html, /@media \(prefers-reduced-motion: reduce\)[\s\S]*?\.track \.fill \{[^}]*scaleX\(1\)/);
});

test('a reveal is a fade and a lift, and nothing else moves', () => {
  // Opacity and transform only: a page that animates width or position reflows
  // while it moves and the text shakes.
  const reveal = /\.reveal \{[^}]*\}/.exec(html);
  assert.ok(reveal, 'the reveal rule exists');
  assert.match(reveal[0], /opacity: 0/);
  assert.match(reveal[0], /transform: translateY\(/);
  assert.match(reveal[0], /transition: opacity [^,]+, transform/);
});

test('the observer reveals what it sees, once', () => {
  assert.match(html, /IntersectionObserver/);
  assert.match(html, /unobserve/);
  // Without the observer the class never lands: everything must be shown at once.
  assert.match(html, /is-visible/);
});

test('the reading line follows the scroll', () => {
  assert.match(html, /id="read-progress"/);
  assert.match(html, /requestAnimationFrame/);
  const paint = /function paint\(\)[\s\S]*?\n  \}/.exec(html);
  assert.ok(paint, 'the line is painted from the scroll');
  assert.match(paint[0], /scaleX\(/);
});

test('the coverage tracks draw themselves in', () => {
  assert.match(html, /\.track \.fill \{[^}]*scaleX\(0\)/);
  assert.match(html, /\.track \.fill\.drawn \{[^}]*scaleX\(1\)/);
  assert.match(html, /\.drawn/);
});

test('an answer opens with a small rise', () => {
  assert.match(html, /details\[open\] \.answer \{[^}]*animation/);
  assert.match(html, /@keyframes answer-in/);
});

test('the motion leaves the language table alone', () => {
  // The table is rebuilt once and the switch only moves text: a guard that let
  // `lang.empty` overwrite its rows emptied the page once already.
  assert.match(html, /if \(key === 'lang\.empty' && nodes\[i\]\.children\.length > 0\) continue;/);
  assert.match(html, /renderLanguages\(\);/);
});