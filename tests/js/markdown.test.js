// Tests for the changelog/release-note Markdown renderer (newui/markdown.js).
// Run with: node --test tests/js
const test = require('node:test');
const assert = require('node:assert');
const path = require('node:path');

const md = require(path.join(__dirname, '..', '..', 'SongRequestDesktopV2Rewrite', 'newui', 'markdown.js'));

test('renders the changelog shape the release workflow produces', () => {
    const changelog = ['## Changes', '- fix the announcement audio', '- add a device switcher', '', '- bump to 4.3'].join('\n');
    const html = md.render(changelog);

    assert.match(html, /<h2 class="md-h2">Changes<\/h2>/);
    assert.match(html, /<ul class="md-list">/);
    assert.equal((html.match(/<li/g) || []).length, 3);
    assert.match(html, /fix the announcement audio/);
    assert.ok(!html.includes('##'), 'the raw heading markers must be gone');
    // a blank line between items must not split the changelog into two lists
    assert.equal((html.match(/<ul/g) || []).length, 1);
});

test('headings of every level', () => {
    assert.match(md.render('# One'), /<h1 class="md-h1">One<\/h1>/);
    assert.match(md.render('### Three'), /<h3 class="md-h3">Three<\/h3>/);
    assert.match(md.render('###### Six'), /<h6 class="md-h6">Six<\/h6>/);
    assert.match(md.render('####### Seven'), /<p>####### Seven<\/p>/);
});

test('unordered and ordered lists, with nesting', () => {
    const html = md.render(['- one', '- two', '  - nested', '1. first', '2. second'].join('\n'));
    assert.match(html, /<ul class="md-list">/);
    assert.match(html, /<ol class="md-list">/);
    assert.equal((html.match(/<li/g) || []).length, 5);
    // the nested item closes and opens a list rather than flattening
    assert.ok(html.indexOf('<ul class="md-list">') !== html.lastIndexOf('<ul class="md-list">'));
});

test('task list items get a checkbox and drop the marker', () => {
    const html = md.render(['- [x] done', '- [ ] todo'].join('\n'));
    assert.match(html, /md-task is-done/);
    assert.match(html, /md-task"/);
    assert.ok(!html.includes('[x]'));
    assert.ok(!html.includes('[ ]'));
});

test('emphasis, inline code and strikethrough', () => {
    const html = md.render('**bold** and *italic* and `code` and ~~gone~~');
    assert.match(html, /<strong>bold<\/strong>/);
    assert.match(html, /<em>italic<\/em>/);
    assert.match(html, /<code>code<\/code>/);
    assert.match(html, /<del>gone<\/del>/);
});

test('inline code is not touched by emphasis rules', () => {
    const html = md.render('use `a * b` literally');
    assert.match(html, /<code>a \* b<\/code>/);
    assert.ok(!html.includes('<em>'), 'the asterisk inside code must stay literal');
});

test('links are rendered, unsafe schemes are dropped', () => {
    assert.match(md.render('[docs](https://example.com/x)'),
        /<a href="https:\/\/example.com\/x" target="_blank" rel="noreferrer noopener">docs<\/a>/);
    const unsafe = md.render('[bad](javascript:alert(1))');
    assert.ok(!unsafe.includes('<a'), 'javascript: links must not become anchors');
    assert.match(unsafe, /bad/);
});

test('html in the changelog is escaped, never executed', () => {
    const html = md.render('<script>alert(1)</script> & "quotes"');
    assert.ok(!html.includes('<script>'));
    assert.match(html, /&lt;script&gt;/);
    assert.match(html, /&amp;/);
});

test('fenced code blocks keep their content verbatim', () => {
    const html = md.render(['```bash', 'echo "hi" > file', '```'].join('\n'));
    assert.match(html, /<pre class="md-code"><code class="language-bash">/);
    assert.match(html, /echo &quot;hi&quot; &gt; file/);
    assert.ok(!html.includes('<strong>'), 'nothing inside a fence is formatted');
});

test('paragraphs, quotes and rules', () => {
    const html = md.render(['first line', 'second line', '', '> quoted', '', '---'].join('\n'));
    assert.match(html, /<p>first line<br>second line<\/p>/);
    assert.match(html, /<blockquote class="md-quote">quoted<\/blockquote>/);
    assert.match(html, /<hr class="md-hr">/);
});

test('tables render with a header row', () => {
    const html = md.render(['| Signal | Weight |', '|---|---|', '| artist | 30% |'].join('\n'));
    assert.match(html, /<table class="md-table">/);
    assert.match(html, /<th>Signal<\/th>/);
    assert.match(html, /<td>artist<\/td>/);
});

test('empty input renders nothing', () => {
    assert.strictEqual(md.render(''), '');
    assert.strictEqual(md.render(null), '');
    assert.strictEqual(md.render(undefined), '');
});

test('unclosed fences still render their content', () => {
    const html = md.render(['```', 'still code'].join('\n'));
    assert.match(html, /<pre class="md-code"><code>still code<\/code><\/pre>/);
});
