// Minimal, dependency-free Markdown renderer for release notes / changelogs.
//
// GitHub release bodies are Markdown; the app used to dump them into a
// `white-space: pre-wrap` block, so `## Changes` and `- item` showed up literally.
// This renders the common subset (headings, lists incl. nesting and task lists,
// emphasis, code, links, quotes, rules, tables) to safe HTML.
//
// Everything is HTML-escaped first: raw HTML in the changelog is shown as text, never
// executed. Kept DOM-free so `node --test tests/js` covers it.
(function (global) {
  'use strict';

  function escapeHtml(text) {
    return String(text)
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;')
      .replace(/'/g, '&#39;');
  }

  /** Only http(s) links are allowed; anything else (javascript:, data:) is dropped. */
  function safeUrl(url) {
    var trimmed = String(url || '').trim();
    if (/^https?:\/\//i.test(trimmed)) return trimmed;
    if (/^mailto:/i.test(trimmed)) return trimmed;
    if (/^#[-\w]+$/.test(trimmed)) return trimmed;
    return null;
  }

  /** Inline spans: code, links, bold, italic, strikethrough. Input must be escaped. */
  function inline(escaped) {
    var text = escaped;

    // `code` first so its content is not touched by the emphasis rules
    var codes = [];
    text = text.replace(/`([^`]+)`/g, function (_, code) {
      codes.push('<code>' + code + '</code>');
      return '\u0000' + (codes.length - 1) + '\u0000';
    });

    // [label](url)
    text = text.replace(/\[([^\]]+)\]\(([^)\s]+)\)/g, function (match, label, url) {
      var href = safeUrl(url);
      if (!href) return label;
      return '<a href="' + href + '" target="_blank" rel="noreferrer noopener">' + label + '</a>';
    });

    text = text.replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>');
    text = text.replace(/__([^_]+)__/g, '<strong>$1</strong>');
    text = text.replace(/(^|[^*\w])\*([^*\n]+)\*/g, '$1<em>$2</em>');
    text = text.replace(/(^|[^_\w])_([^_\n]+)_/g, '$1<em>$2</em>');
    text = text.replace(/~~([^~]+)~~/g, '<del>$1</del>');

    text = text.replace(/\u0000(\d+)\u0000/g, function (_, index) { return codes[Number(index)]; });
    return text;
  }

  var LIST_ITEM = /^(\s*)([-*+]|\d+[.)])\s+(.*)$/;
  var TASK_ITEM = /^\[([ xX])\]\s+(.*)$/;
  var HEADING = /^(#{1,6})\s+(.*)$/;
  var FENCE = /^\s*(```|~~~)\s*([\w+-]*)\s*$/;
  var HR = /^\s*([-*_])(\s*\1){2,}\s*$/;
  var QUOTE = /^\s*>\s?(.*)$/;
  var TABLE_ROW = /^\s*\|(.+)\|\s*$/;
  var TABLE_DIVIDER = /^\s*\|?[\s:|-]+\|?\s*$/;

  function renderListItem(content, ordered) {
    var task = content.match(TASK_ITEM);
    var checkbox = '';
    if (task) {
      var done = task[1].toLowerCase() === 'x';
      checkbox = '<span class="md-task' + (done ? ' is-done' : '') + '">' +
        '<i class="fas ' + (done ? 'fa-square-check' : 'fa-square') + '"></i></span>';
      content = task[2];
    }
    return '<li' + (ordered ? '' : ' class="md-bullet"') + '>' + checkbox + inline(content) + '</li>';
  }

  /**
   * Renders a Markdown string to HTML.
   * Lists are emitted as <ul>/<ol> with nesting up to 3 levels via indentation.
   */
  function render(markdown) {
    if (markdown == null) return '';
    var lines = String(markdown).replace(/\r\n?/g, '\n').split('\n');
    var html = [];
    var paragraph = [];
    var listStack = [];      // { ordered, indent }
    var inCode = false;
    var codeLines = [];
    var codeLang = '';
    var looseList = false;   // a blank line inside a list: a following item continues it

    function flushParagraph() {
      if (paragraph.length === 0) return;
      html.push('<p>' + inline(paragraph.join('<br>')) + '</p>');
      paragraph = [];
    }

    function closeLists(toIndent) {
      while (listStack.length > 0 && listStack[listStack.length - 1].indent > toIndent) {
        html.push(listStack.pop().ordered ? '</ol>' : '</ul>');
      }
    }

    function closeAllLists() {
      while (listStack.length > 0) html.push(listStack.pop().ordered ? '</ol>' : '</ul>');
    }

    for (var i = 0; i < lines.length; i++) {
      var line = lines[i];

      // fenced code blocks
      var fence = line.match(FENCE);
      if (fence) {
        if (inCode) {
          html.push('<pre class="md-code"><code' + (codeLang ? ' class="language-' + escapeHtml(codeLang) + '"' : '') + '>' +
            escapeHtml(codeLines.join('\n')) + '</code></pre>');
          inCode = false;
          codeLines = [];
          codeLang = '';
        } else {
          flushParagraph();
          closeAllLists();
          inCode = true;
          codeLang = fence[2] || '';
        }
        continue;
      }
      if (inCode) { codeLines.push(line); continue; }

      if (line.trim() === '') {
        flushParagraph();
        if (listStack.length === 0) closeAllLists();
        looseList = listStack.length > 0;
        continue;
      }

      // anything that is not another list item ends a list started before a blank line
      var continuesList = LIST_ITEM.test(line);
      if (looseList && !continuesList) closeAllLists();
      looseList = false;

      // tables (only when the next line is a divider)
      var row = line.match(TABLE_ROW);
      if (row && i + 1 < lines.length && TABLE_DIVIDER.test(lines[i + 1]) && lines[i + 1].indexOf('-') >= 0) {
        flushParagraph();
        closeAllLists();
        var header = row[1].split('|').map(function (c) { return c.trim(); });
        html.push('<table class="md-table"><thead><tr>' +
          header.map(function (c) { return '<th>' + inline(escapeHtml(c)) + '</th>'; }).join('') +
          '</tr></thead><tbody>');
        i += 2;
        while (i < lines.length) {
          var bodyRow = lines[i].match(TABLE_ROW);
          if (!bodyRow) { i--; break; }
          var cells = bodyRow[1].split('|').map(function (c) { return c.trim(); });
          html.push('<tr>' + cells.map(function (c) { return '<td>' + inline(escapeHtml(c)) + '</td>'; }).join('') + '</tr>');
          i++;
        }
        html.push('</tbody></table>');
        continue;
      }

      var heading = line.match(HEADING);
      if (heading) {
        flushParagraph();
        closeAllLists();
        var level = Math.min(6, heading[1].length);
        html.push('<h' + level + ' class="md-h' + level + '">' + inline(escapeHtml(heading[2].trim())) + '</h' + level + '>');
        continue;
      }

      if (HR.test(line) && !LIST_ITEM.test(line)) {
        flushParagraph();
        closeAllLists();
        html.push('<hr class="md-hr">');
        continue;
      }

      var quote = line.match(QUOTE);
      if (quote) {
        flushParagraph();
        closeAllLists();
        html.push('<blockquote class="md-quote">' + inline(escapeHtml(quote[1])) + '</blockquote>');
        continue;
      }

      var item = line.match(LIST_ITEM);
      if (item) {
        flushParagraph();
        var indent = Math.floor(item[1].replace(/\t/g, '    ').length / 2);
        var ordered = /^\d/.test(item[2]);
        while (listStack.length > 0 && listStack[listStack.length - 1].indent > indent) {
          html.push(listStack.pop().ordered ? '</ol>' : '</ul>');
        }
        var top = listStack[listStack.length - 1];
        if (!top || top.indent < indent) {
          listStack.push({ ordered: ordered, indent: indent });
          html.push(ordered ? '<ol class="md-list">' : '<ul class="md-list">');
        } else if (top.ordered !== ordered) {
          html.push(top.ordered ? '</ol>' : '</ul>');
          listStack.pop();
          listStack.push({ ordered: ordered, indent: indent });
          html.push(ordered ? '<ol class="md-list">' : '<ul class="md-list">');
        }
        html.push(renderListItem(escapeHtml(item[3]), ordered));
        continue;
      }

      closeLists(0);
      paragraph.push(escapeHtml(line.trim()));
    }

    if (inCode && codeLines.length > 0) {
      html.push('<pre class="md-code"><code>' + escapeHtml(codeLines.join('\n')) + '</code></pre>');
    }
    flushParagraph();
    closeAllLists();
    return html.join('\n');
  }

  var api = { render: render, inline: inline, escapeHtml: escapeHtml };
  global.SongRequestMarkdown = api;
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
})(typeof window !== 'undefined' ? window : globalThis);
