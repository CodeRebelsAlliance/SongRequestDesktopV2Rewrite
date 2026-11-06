// Runtime checks for the shared karaoke renderer using a tiny DOM stub. This is
// deliberately dependency-free: it exercises the real karaoke.js code paths that
// matter for syllable rendering and the per-line wave confinement.
const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const root = path.join(__dirname, '..', '..');
const source = fs.readFileSync(
  path.join(root, 'SongRequestDesktopV2Rewrite', 'newui', 'karaoke.js'),
  'utf8');

class TextNode {
  constructor(text) {
    this.nodeType = 3;
    this._text = String(text);
    this.parentNode = null;
  }
  get textContent() { return this._text; }
}

function matches(element, selector) {
  selector = selector.trim();
  if (!selector) return false;

  const attribute = selector.match(/\[([^\]]+)\]/);
  const base = selector.replace(/\[[^\]]+\]/g, '');
  const classes = base.split('.').filter(Boolean);
  for (const name of classes) {
    if (!element.classList.contains(name)) return false;
  }
  if (attribute) {
    const name = attribute[1].split('=')[0];
    const key = name.startsWith('data-')
      ? name.slice(5).replace(/-([a-z])/g, (_, c) => c.toUpperCase())
      : name;
    if (!(key in element.dataset)) return false;
  }
  return classes.length > 0 || !!attribute;
}

class Element {
  constructor(tag) {
    this.nodeType = 1;
    this.tagName = String(tag).toUpperCase();
    this.children = [];
    this.parentNode = null;
    this.dataset = {};
    this.hidden = false;
    this.isConnected = true;
    this._classes = new Set();
    this._style = Object.create(null);
    this._styleWrites = 0;
    this._text = '';
    this._html = '';
    this._attributes = {};

    const classes = this._classes;
    this.classList = {
      add: (...names) => names.forEach((name) => classes.add(name)),
      remove: (...names) => names.forEach((name) => classes.delete(name)),
      contains: (name) => classes.has(name),
      toggle: (name, force) => {
        if (force === undefined) {
          if (classes.has(name)) classes.delete(name);
          else classes.add(name);
        } else if (force) classes.add(name);
        else classes.delete(name);
      }
    };
  }

  get className() { return Array.from(this._classes).join(' '); }
  set className(value) {
    this._classes.clear();
    String(value).split(/\s+/).filter(Boolean).forEach((name) => this._classes.add(name));
  }

  get textContent() {
    if (this.children.length) {
      return this.children.map((child) => child.textContent).join('');
    }
    return this._text;
  }
  set textContent(value) {
    this._text = String(value);
    this.children = [];
  }

  get innerHTML() { return this._html; }
  set innerHTML(value) {
    this._html = String(value);
    this.children = [];
    this._text = '';
  }

  get style() {
    const style = this._style;
    const element = this;
    return {
      setProperty(name, value) { element._styleWrites++; style[name] = String(value); },
      getPropertyValue(name) { return style[name] || ''; }
    };
  }

  setAttribute(name, value) { this._attributes[name] = String(value); }
  getAttribute(name) { return this._attributes[name]; }

  appendChild(child) {
    child.parentNode = this;
    this.children.push(child);
    return child;
  }

  insertBefore(child, reference) {
    child.parentNode = this;
    const index = this.children.indexOf(reference);
    if (index < 0) this.children.push(child);
    else this.children.splice(index, 0, child);
    return child;
  }

  remove() {
    if (!this.parentNode) return;
    const index = this.parentNode.children.indexOf(this);
    if (index >= 0) this.parentNode.children.splice(index, 1);
    this.parentNode = null;
  }

  querySelectorAll(selector) {
    const selectors = String(selector).split(',').map((part) => part.trim()).filter(Boolean);
    const result = [];
    const walk = (node) => {
      for (const child of node.children) {
        if (child.nodeType !== 1) continue;
        if (selectors.some((part) => matches(child, part))) result.push(child);
        walk(child);
      }
    };
    walk(this);
    return result;
  }

  querySelector(selector) {
    return this.querySelectorAll(selector)[0] || null;
  }
}

function createHarness() {
  const documentElement = new Element('html');
  const byId = new Map();
  let now = 0;
  const frames = [];
  const document = {
    documentElement,
    createElement: (tag) => new Element(tag),
    createTextNode: (text) => new TextNode(text),
    getElementById: (id) => byId.get(id) || null,
    dispatchEvent: () => { }
  };
  const sandbox = {
    document,
    console,
    performance: { now: () => now },
    requestAnimationFrame: (fn) => { frames.push(fn); return frames.length; },
    cancelAnimationFrame: () => { },
    CustomEvent: function (name, options) {
      this.type = name;
      this.detail = options && options.detail;
    }
  };
  sandbox.window = sandbox;
  sandbox.globalThis = sandbox;
  vm.createContext(sandbox);
  vm.runInContext(source, sandbox);
  return {
    karaoke: sandbox.SongRequestKaraoke,
    document,
    byId,
    setNow: (ms) => { now = ms; },
    runFrame: () => {
      const frame = frames.shift();
      if (frame) frame();
    }
  };
}

function addLine(karaoke, container, id, syllables, time, end) {
  const line = new Element('div');
  line.className = 'lyrics-line';
  line.dataset.index = id;
  container.appendChild(line);
  karaoke.renderLine(line, { time, end, text: '', syllables });
  return line;
}

function spans(line) {
  return line.querySelectorAll('.karaoke-word');
}

test('renderLine renders syllable spans and spaces only between words', () => {
  const { karaoke } = createHarness();

  const container = new Element('div');
  const line = addLine(karaoke, container, 0, [
    { start: 1, end: 1.4, text: 'hel', wordIndex: 0 },
    { start: 1.4, end: 1.7, text: 'lo', wordIndex: 0 },
    { start: 1.7, end: 2.0, text: 'world', wordIndex: 1 }
  ], 1, 2);

  const rendered = spans(line);
  assert.deepEqual(rendered.map((span) => span.textContent), ['hel', 'lo', 'world']);
  assert.equal(line.textContent, 'hello world');
  assert.equal(Number(line.dataset.karaokeStart), 1);
  assert.equal(Number(line.dataset.karaokeEnd), 2);
  assert.equal(rendered[0].className, 'karaoke-word karaoke-syllable');
  assert.match(rendered[0].style.getPropertyValue('--karaoke-smooth'), /^\d+\.\d+s$/);
  assert.match(rendered[0].style.getPropertyValue('--karaoke-lift-time'), /^\d+\.\d+s$/);
  assert.match(rendered[0].style.getPropertyValue('--karaoke-color-time'), /^\d+\.\d+s$/);
});

test('the lift wave is confined to the active lyric line', () => {
  const { karaoke } = createHarness();
  karaoke.setEnabled(true);

  const container = new Element('div');
  const first = addLine(karaoke, container, 0, [
    { start: 1.0, end: 1.4, text: 'hel', wordIndex: 0 },
    { start: 1.4, end: 1.7, text: 'lo', wordIndex: 0 },
    { start: 1.7, end: 2.0, text: 'world', wordIndex: 1 }
  ], 1, 2);
  const second = addLine(karaoke, container, 1, [
    { start: 3.0, end: 3.5, text: 'next', wordIndex: 0 },
    { start: 3.5, end: 4.0, text: 'line', wordIndex: 1 }
  ], 3, 4);

  karaoke.update(container, 1.9);

  const firstSpans = spans(first);
  const secondSpans = spans(second);
  assert.equal(firstSpans[2].style.getPropertyValue('--karaoke-progress'), '67%');
  assert.equal(firstSpans[2].style.getPropertyValue('--karaoke-lift'), '-6.50px');
  assert.equal(firstSpans[1].style.getPropertyValue('--karaoke-lift'), '-3.77px');
  assert.equal(firstSpans[0].style.getPropertyValue('--karaoke-lift'), '-1.56px');
  assert.equal(firstSpans[0].classList.contains('is-sung'), true);
  assert.equal(firstSpans[2].classList.contains('is-active'), true);

  // The next line must never inherit the wave from the previous line.
  for (const span of secondSpans) {
    assert.equal(span.style.getPropertyValue('--karaoke-lift'), '0.00px');
    assert.equal(span.style.getPropertyValue('--karaoke-progress'), '0%');
  }

  // In the instrumental gap neither line lifts.
  karaoke.update(container, 2.5);
  for (const span of [...spans(first), ...spans(second)]) {
    assert.equal(span.style.getPropertyValue('--karaoke-lift'), '0.00px');
  }

  // Once the next line is active the previous line is fully at rest.
  karaoke.update(container, 3.2);
  assert.equal(secondSpans[0].style.getPropertyValue('--karaoke-lift'), '-6.50px');
  assert.equal(secondSpans[1].style.getPropertyValue('--karaoke-lift'), '-3.77px');
  for (const span of spans(first)) {
    assert.equal(span.style.getPropertyValue('--karaoke-lift'), '0.00px');
  }
});

test('instrumental pauses render as a large bar, not a panel', () => {
  const { karaoke } = createHarness();
  karaoke.setEnabled(true);

  const container = new Element('div');
  addLine(karaoke, container, 0, [
    { start: 1.0, end: 1.5, text: 'last', wordIndex: 0 }
  ], 1, 1.5);
  const next = addLine(karaoke, container, 1, [
    { start: 4.0, end: 4.5, text: 'next', wordIndex: 0 }
  ], 4, 4.5);

  karaoke.attachPauses(container, [{ start: 1.5, end: 4, duration: 2.5 }]);
  const pause = container.querySelector('.karaoke-pause');
  assert.ok(pause);
  assert.equal(pause.hidden, true);
  assert.match(pause.innerHTML, /karaoke-pause-track/);
  assert.match(pause.innerHTML, /karaoke-pause-fill/);
  assert.match(pause.innerHTML, /karaoke-pause-caption/);
  assert.doesNotMatch(pause.innerHTML, /karaoke-pause-copy/);
  assert.equal(pause.parentNode, container);
  assert.equal(container.children.indexOf(pause), container.children.indexOf(next) - 1);

  karaoke.update(container, 2.0);
  assert.equal(pause.hidden, false);
  assert.equal(pause.classList.contains('is-active'), true);

  karaoke.update(container, 4.2);
  assert.equal(pause.hidden, true);
});

test('only the active lyric line is repainted as time advances', () => {
  const { karaoke } = createHarness();
  karaoke.setEnabled(true);

  const container = new Element('div');
  const first = addLine(karaoke, container, 0, [
    { start: 1.0, end: 1.5, text: 'one', wordIndex: 0 },
    { start: 1.5, end: 2.0, text: 'two', wordIndex: 1 }
  ], 1, 2);
  const second = addLine(karaoke, container, 1, [
    { start: 3.0, end: 3.5, text: 'three', wordIndex: 0 },
    { start: 3.5, end: 4.0, text: 'four', wordIndex: 1 }
  ], 3, 4);

  karaoke.update(container, 1.2);
  const firstSpans = spans(first);
  const secondSpans = spans(second);
  firstSpans.forEach((span) => { span._styleWrites = 0; });
  secondSpans.forEach((span) => { span._styleWrites = 0; });

  karaoke.update(container, 1.7);

  assert.ok(firstSpans.some((span) => span._styleWrites > 0), 'active line must update');
  assert.equal(secondSpans.reduce((sum, span) => sum + span._styleWrites, 0), 0,
    'inactive lines must not be touched between host ticks');
});

test('the shared clock interpolates the wipe between host position updates', () => {
  const harness = createHarness();
  const karaoke = harness.karaoke;
  karaoke.setEnabled(true);

  const container = new Element('div');
  const line = addLine(karaoke, container, 0, [
    { start: 0, end: 2, text: 'hold', wordIndex: 0 }
  ], 0, 2);
  const span = spans(line)[0];

  harness.setNow(0);
  karaoke.update(container, 1.0, true);
  assert.equal(span.style.getPropertyValue('--karaoke-progress'), '50%');
  assert.equal(span.classList.contains('is-active'), true);

  harness.setNow(500);
  harness.runFrame();
  assert.equal(span.style.getPropertyValue('--karaoke-progress'), '75%');

  harness.setNow(1000);
  harness.runFrame();
  assert.equal(span.style.getPropertyValue('--karaoke-progress'), '100%');
  assert.equal(span.classList.contains('is-sung'), true);

  karaoke.setPlaying(false);
});
