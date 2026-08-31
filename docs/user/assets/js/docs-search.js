/* ============================================================================
   circuitRF — User Documentation search
   ----------------------------------------------------------------------------
   HAND-WRITTEN. The thing this file reads — assets/js/search-index.js — is the
   generated half, rebuilt by tools/DocGen from the Markdown sources.

   It drives every box on the page that carries `data-crf-search`: the small one
   in each page header, and the wide one on the landing page. Both are the same
   markup from the same emitter (HtmlEmitter.SearchBox), so there is one control
   here and not two.

   Three things about the environment shape the whole file:

     * No build step, no dependency, no module loader. The docs are static files
       that must open from a web host, from the loopback server the application
       starts for Help, and from a bare file:// path. Anything that needs
       bundling or fetching is out.
     * The index is a plain global (window.CRF_DOCS_SEARCH) for the same reason:
       a classic <script src> is the only fetch that works on file://.
     * If it is missing, the boxes stay hidden. They are rendered `hidden` and
       unhidden here, so a page whose JavaScript never ran shows no search field
       rather than one that silently eats what you type.

   Ranking, in one sentence: every query word must appear somewhere in a section,
   and a word is worth most in the page title, less in the heading, least in the
   body — with a bonus when the whole query appears as a phrase.
   ============================================================================ */
(function () {
  'use strict';

  var MAX_RESULTS      = 24;   /* the panel scrolls; past this nobody reads     */
  var MAX_PER_PAGE     = 3;    /* one long page must not crowd out every other  */
  var SNIPPET_CHARS    = 170;
  /* Two, not one. A single character cannot be answered precisely here: no page
     title and no section heading in the whole set is one or two characters long,
     so a one-character query can only ever match body text — and every page's body
     contains every letter. It would return 24 arbitrary sections, which is worse
     than returning nothing. */
  var MIN_QUERY_CHARS  = 2;

  /* ---------------------------------------------------------------- index --- */

  var DATA = window.CRF_DOCS_SEARCH;
  if (!DATA || !DATA.s || !DATA.p) return;   /* boxes stay hidden */

  /* p: [slug, title, docKind, lede]      s: [pageIndex, anchor, heading, text]

     DATA.p is in READING ORDER — the order src/_nav.txt declares, which the
     generator sorted it into — so a page's index in it IS how early the docs
     themselves place it. That is the whole input to the prior below. */
  var LAST_PAGE = Math.max(1, DATA.p.length - 1);

  var SECTIONS = DATA.s.map(function (row) {
    var page = DATA.p[row[0]];
    return {
      /* 1.15 for the landing page down to 1.00 for the last reference page. */
      prior:    1 + 0.15 * (1 - row[0] / LAST_PAGE),
      slug:     page[0],
      pageTitle: page[1],
      docKind:  page[2],
      anchor:   row[1],
      heading:  row[2],
      text:     row[3],
      /* Lowercased once, here, rather than per keystroke per section. */
      lcTitle:   page[1].toLowerCase(),
      lcHeading: row[2].toLowerCase(),
      lcText:    row[3].toLowerCase()
    };
  });

  /* ------------------------------------------------------------- matching --- */

  /* Words keep '.', '-', '_' and '+' so ".cnl", "S-parameter", "loadpull-pursuit"
     and "C++" survive tokenising as one term instead of shattering into three.

     A lone letter is dropped from a MULTI-word query: "how do I create a cell" would
     otherwise require "i" and "a", which every page satisfies and which contribute
     nothing but noise to the ranking. The fallback keeps them when a query is
     nothing BUT single letters, so such a query still searches for something. */
  function terms(query) {
    var all = query.toLowerCase().split(/[^a-z0-9._+-]+/).filter(function (t) { return t.length > 0; });
    if (all.length < 2) return all;
    var long = all.filter(function (t) { return t.length > 1; });
    return long.length > 0 ? long : all;
  }

  /* A match that starts a word beats one buried mid-word: searching "port" should
     rank "Ports" above "Import". Both still count — substring matching is what
     makes a search for "param" find "parameters" — the boundary one just scores
     higher. */
  function isWordStart(hay, at) {
    return at === 0 || /[^a-z0-9]/.test(hay.charAt(at - 1));
  }

  function countHits(hay, term) {
    var n = 0, boundary = 0, at = hay.indexOf(term);
    while (at !== -1) {
      n++;
      if (isWordStart(hay, at)) boundary++;
      at = hay.indexOf(term, at + term.length);
    }

    return { n: n, boundary: boundary };
  }

  function score(section, ts, phrase) {
    var total = 0;

    for (var i = 0; i < ts.length; i++) {
      var t = ts[i];
      var inTitle   = countHits(section.lcTitle,   t);
      var inHeading = countHits(section.lcHeading, t);
      var inText    = countHits(section.lcText,    t);

      /* Every term must appear somewhere. An OR search over a 40-page manual
         returns the whole manual. */
      if (inTitle.n + inHeading.n + inText.n === 0) return 0;

      total += inTitle.n   ? 12 + (inTitle.boundary   ? 6 : 0) : 0;
      total += inHeading.n ?  8 + (inHeading.boundary ? 5 : 0) : 0;
      total += Math.min(inText.n, 5) * 2 + (inText.boundary ? 3 : 0);
    }

    /* The whole query, in order, is a much stronger signal than its words apart. */
    if (phrase.length > 2) {
      if (section.lcHeading.indexOf(phrase) !== -1) total += 40;
      else if (section.lcTitle.indexOf(phrase) !== -1) total += 25;
      else if (section.lcText.indexOf(phrase) !== -1) total += 12;
    }

    /* A heading that IS the query is the answer, not a hit inside a longer one. */
    if (section.lcHeading === phrase || section.lcTitle === phrase) total += 60;

    /* Reading order as a tie-break with a soft edge. Two sections genuinely can
       answer a query equally well — "Hierarchy" heads a section in both editors —
       and when they do, the one the documentation puts FIRST is the one to send a
       reader to. The spread is 15%, so it only ever decides a near-tie; it cannot
       lift a weak match over a strong one. */
    return total * section.prior;
  }

  function search(query) {
    var ts = terms(query);
    if (ts.length === 0) return [];
    var phrase = query.toLowerCase().trim();

    var hits = [];
    for (var i = 0; i < SECTIONS.length; i++) {
      var s = score(SECTIONS[i], ts, phrase);
      if (s > 0) hits.push({ section: SECTIONS[i], score: s, order: i });
    }

    hits.sort(function (a, b) { return b.score - a.score || a.order - b.order; });

    /* Cap per page AFTER ranking, so a page's best section is always the one kept. */
    var perPage = {}, out = [];
    for (var j = 0; j < hits.length && out.length < MAX_RESULTS; j++) {
      var slug = hits[j].section.slug;
      perPage[slug] = (perPage[slug] || 0) + 1;
      if (perPage[slug] > MAX_PER_PAGE) continue;
      out.push(hits[j]);
    }
    return out;
  }

  /* ------------------------------------------------------------- snippets --- */

  function escapeHtml(s) {
    return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
  }

  /* Wrap every occurrence of every term. Built from the ESCAPED text and matched
     on the escaped text, so a section containing "<h2>" cannot inject markup and
     an entity cannot be cut in half. */
  function mark(text, ts) {
    var escaped = escapeHtml(text);
    if (ts.length === 0) return escaped;

    var pattern = ts.map(function (t) {
      return escapeHtml(t).replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    }).sort(function (a, b) { return b.length - a.length; }).join('|');

    return escaped.replace(new RegExp('(' + pattern + ')', 'gi'), '<mark>$1</mark>');
  }

  /* A window of the section text around the first term that appears in it — the
     part of the page that actually answers the query, rather than its first line. */
  function snippet(section, ts) {
    var text = section.text;
    if (!text) return '';

    var at = -1;
    for (var i = 0; i < ts.length; i++) {
      var found = section.lcText.indexOf(ts[i]);
      if (found !== -1 && (at === -1 || found < at)) at = found;
    }
    if (at === -1) at = 0;

    var start = Math.max(0, at - Math.floor(SNIPPET_CHARS / 3));
    var end   = Math.min(text.length, start + SNIPPET_CHARS);

    /* Snap to word boundaries so the snippet never opens or closes mid-word. */
    if (start > 0) {
      var space = text.indexOf(' ', start);
      if (space !== -1 && space < at) start = space + 1;
    }
    if (end < text.length) {
      var back = text.lastIndexOf(' ', end);
      if (back > start) end = back;
    }

    return (start > 0 ? '…' : '') + mark(text.slice(start, end), ts)
         + (end < text.length ? '…' : '');
  }

  /* ------------------------------------------------------------------ box --- */

  function SearchBox(root) {
    var input = root.querySelector('.search-input');
    var panel = root.querySelector('.search-panel');
    var base  = root.getAttribute('data-root') || '';
    var active = -1;
    var results = [];

    function href(section) {
      return base + section.slug + (section.anchor ? '#' + section.anchor : '');
    }

    function close() {
      panel.hidden = true;
      panel.innerHTML = '';
      input.setAttribute('aria-expanded', 'false');
      active = -1;
      results = [];
    }

    function render(query) {
      var ts = terms(query);
      results = search(query);

      if (results.length === 0) {
        panel.innerHTML = '<p class="search-empty">No match for <strong>'
                        + escapeHtml(query) + '</strong>.</p>';
        panel.hidden = false;
        input.setAttribute('aria-expanded', 'true');
        active = -1;
        return;
      }

      var html = '';
      for (var i = 0; i < results.length; i++) {
        var s = results[i].section;
        /* The heading is the answer; the page is where it lives. A lead section
           has no heading of its own, so the page title becomes the answer and the
           crumb falls back to the guide it is in. */
        var title = s.heading || s.pageTitle;
        var crumb = s.heading ? s.pageTitle : (s.docKind || 'Documentation');

        html += '<a class="search-hit" role="option" href="' + href(s) + '">'
             +    '<span class="hit-title">' + mark(title, ts) + '</span>'
             +    '<span class="hit-crumb">' + escapeHtml(crumb) + '</span>'
             +    '<span class="hit-text">' + snippet(s, ts) + '</span>'
             +  '</a>';
      }

      panel.innerHTML = html;
      panel.hidden = false;
      input.setAttribute('aria-expanded', 'true');
      setActive(0);
    }

    function setActive(i) {
      var hits = panel.querySelectorAll('.search-hit');
      if (hits.length === 0) return;
      if (active >= 0 && hits[active]) hits[active].classList.remove('is-active');
      active = (i + hits.length) % hits.length;
      hits[active].classList.add('is-active');
      for (var k = 0; k < hits.length; k++) hits[k].setAttribute('aria-selected', k === active ? 'true' : 'false');
      /* Keep the keyboard cursor inside the scrolling panel. */
      var hit = hits[active];
      if (hit.offsetTop < panel.scrollTop) panel.scrollTop = hit.offsetTop;
      else if (hit.offsetTop + hit.offsetHeight > panel.scrollTop + panel.clientHeight)
        panel.scrollTop = hit.offsetTop + hit.offsetHeight - panel.clientHeight;
    }

    function update() {
      var q = input.value.trim();
      if (q.length < MIN_QUERY_CHARS) { close(); return; }
      render(q);
    }

    input.addEventListener('input', update);
    input.addEventListener('focus', function () { if (input.value.trim().length >= MIN_QUERY_CHARS) update(); });

    input.addEventListener('keydown', function (e) {
      if (e.key === 'ArrowDown')      { e.preventDefault(); setActive(active + 1); }
      else if (e.key === 'ArrowUp')   { e.preventDefault(); setActive(active - 1); }
      else if (e.key === 'Enter') {
        var hits = panel.querySelectorAll('.search-hit');
        if (!panel.hidden && active >= 0 && hits[active]) {
          e.preventDefault();
          window.location.href = hits[active].getAttribute('href');
        }
      }
      else if (e.key === 'Escape') {
        /* First Escape clears a query, a second gives the page back the keyboard —
           so an accidental focus is never a trap. */
        if (input.value) { input.value = ''; close(); }
        else input.blur();
      }
    });

    /* A click inside the panel is a navigation; a click anywhere else closes it.
       Guarded on the panel so selecting text in a snippet does not dismiss it. */
    document.addEventListener('click', function (e) {
      if (!root.contains(e.target)) close();
    });

    root.hidden = false;
    return { focus: function () { input.focus(); input.select(); } };
  }

  /* ----------------------------------------------------------------- wire --- */

  var boxes = [];
  var nodes = document.querySelectorAll('[data-crf-search]');
  for (var i = 0; i < nodes.length; i++) boxes.push(SearchBox(nodes[i]));
  if (boxes.length === 0) return;

  /* "/" and Ctrl/Cmd+K focus the search, the two conventions a reader is likely to
     already have. Never while they are typing somewhere else. */
  document.addEventListener('keydown', function (e) {
    var el = document.activeElement;
    var typing = el && (el.tagName === 'INPUT' || el.tagName === 'TEXTAREA' || el.isContentEditable);

    if ((e.key === 'k' || e.key === 'K') && (e.metaKey || e.ctrlKey)) {
      e.preventDefault();
      boxes[boxes.length - 1].focus();   /* the landing page's own box, when there is one */
      return;
    }
    if (e.key === '/' && !typing && !e.metaKey && !e.ctrlKey && !e.altKey) {
      e.preventDefault();
      boxes[boxes.length - 1].focus();
    }
  });
})();
