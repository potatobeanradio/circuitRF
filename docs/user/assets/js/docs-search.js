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
   and a word is worth most in the page's declared keywords, then its title, then
   the heading, least in the body — with a bonus when the whole query appears as a
   phrase, and with WHERE IN A WORD the match landed counting as much as which
   field it landed in.

   That last clause is the part worth explaining, because it is what three real
   reported failures had in common:

     * "CLI" returned sections about CLICKS. A bare indexOf makes every three-letter
       query a substring query, and short strings are inside long words everywhere.
     * "command line" returned the Quick Start's "Headless / command line" heading
       instead of the chapter literally titled "The Command Line" — the exact-title
       bonus was a string EQUALITY that a leading "The" defeated, and a phrase in a
       heading outscored a phrase in a page title.
     * "CL" returned nothing useful, because no title or heading contains it and
       body matching for a two-letter term is noise by definition.

   So a match is classified as WHOLE-word, word-PREFIX or INSIDE-a-word, and a term
   shorter than four characters scores only against names — keywords, title,
   heading — never against prose. A short query is an acronym or something half
   typed; against forty pages of prose it can only ever be noise.
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

  /* p: [slug, title, docKind, lede, keywords]   s: [pageIndex, anchor, heading, text]

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
      lcTitle:    page[1].toLowerCase(),
      lcHeading:  row[2].toLowerCase(),
      lcText:     row[3].toLowerCase(),
      /* Written in the page's front matter, rendered nowhere, weighted above the
         title. It is the only place a synonym the chapter never uses can live. */
      lcKeywords: (page[4] || '').toLowerCase(),
      /* "The Command Line" and a query of "command line" name the same thing. */
      nameTitle:   bareName(page[1]),
      nameHeading: bareName(row[2]),
      /* "Design of experiments" → "doe": a section is named by its initials too. */
      initHeading: initials(row[2]),
      /* "Hierarchy: putting one schematic inside another", "Loadpull / Sourcepull":
         each clause of a heading is a name of the section too. */
      nameClauses: row[2].split(/[:\/\u2014(]/).map(bareName),
      /* The initials of the page title and of every multi-word keyword phrase, so a
         reader who types the abbreviation they use out loud finds the chapter: "CL"
         and "DD" are how people refer to the command line and the Data Display, and
         neither string occurs anywhere in either chapter. */
      acronyms: initials(page[1]) + ' ' + (page[4] || '').split(',').map(initials).join(' ')
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

  /* Where in a word a match landed. This is the whole substance of the ranking
     change: `indexOf` alone cannot tell "CLI" the acronym from "CLI" the first
     three letters of "clicks", and against forty pages of prose the second is
     everywhere. */
  var WHOLE = 3, PREFIX = 2, INSIDE = 1, NONE = 0;

  function isBoundary(ch) { return ch === '' || /[^a-z0-9]/.test(ch); }

  /* The best match kind for `term` in `hay`, and how many times it occurred. */
  function findTerm(hay, term) {
    var best = NONE, n = 0, at = hay.indexOf(term);
    while (at !== -1) {
      n++;
      var startsWord = isBoundary(at === 0 ? '' : hay.charAt(at - 1));
      var endsWord   = isBoundary(hay.charAt(at + term.length) || '');
      var kind = !startsWord ? INSIDE : (endsWord ? WHOLE : PREFIX);
      if (kind > best) best = kind;
      at = hay.indexOf(term, at + 1);
    }
    return { kind: best, n: n };
  }

  /* A name with its leading article and punctuation removed, for comparing a query
     against a title. "The Command Line" and "command line" name the same chapter,
     and an equality test says they do not — which is exactly how the chapter called
     "The Command Line" lost a search for "command line" to a heading elsewhere. */
  function bareName(s) {
    return String(s).toLowerCase()
      .replace(/^(the|a|an)\s+/, '')
      .replace(/[^a-z0-9]+/g, ' ')
      .trim();
  }

  /* The initials of a multi-word name: "The Command Line" → "cl". A single word has
     no acronym worth matching — "clay" would become "c", which every query of one
     letter would then hit — so those return nothing. */
  function initials(s) {
    var words = bareName(s).split(' ').filter(function (w) { return w.length > 0; });
    return words.length > 1 ? words.map(function (w) { return w.charAt(0); }).join('') : '';
  }

  /* A term shorter than this scores only against NAMES — keywords, page title,
     section heading — and never against body prose. Four is where it stops being a
     guess: "ESR", "DRC", "SDD", "CLI", "DBU", "npy" are all three, and every one of
     them appears inside unrelated longer words somewhere in forty pages of text. */
  var SHORT_TERM = 4;

  /* Field weight × match kind. Keywords lead because they are curated: a page's
     front matter lists the words a reader actually types for it, so a hit there is
     a deliberate answer where a hit in prose is a coincidence. */
  function fieldScore(kind, whole, prefix, inside) {
    return kind === WHOLE ? whole : kind === PREFIX ? prefix : kind === INSIDE ? inside : 0;
  }

  /* A section the page's keywords tie to the query. The page declares BOTH the
     query and a name of this section — its heading, or a clause of it — and that
     name either contains the query as a word ("MCP" and "MCP server" →
     "serve — the MCP server") or is what its initials spell ("DOE" and "design of
     experiments" → Design of experiments). Without the declaration this is far
     too loose: "convert — layout interchange" also spells "cli", and a dozen
     headings contain "dielectric". */
  function declaredSection(section, bare) {
    if (bare.length < 2 || findTerm(section.lcKeywords, bare).kind !== WHOLE) return false;
    var names = [section.nameHeading].concat(section.nameClauses);
    for (var i = 0; i < names.length; i++) {
      var n = names[i];
      if (n.length === 0 || n === bare || findTerm(section.lcKeywords, n).kind !== WHOLE) continue;
      if (findTerm(n, bare).kind === WHOLE) return true;
      if (bare.indexOf(' ') < 0 && initials(n) === bare) return true;
    }
    return false;
  }

  function score(section, ts, phrase) {
    var total = 0;

    for (var i = 0; i < ts.length; i++) {
      var t     = ts[i];
      var short = t.length < SHORT_TERM;

      var k = findTerm(section.lcKeywords, t);
      var h = findTerm(section.lcHeading,  t);
      var d = findTerm(section.lcTitle,    t);
      var x = short ? { kind: NONE, n: 0 } : findTerm(section.lcText, t);
      /* Acronyms are a short-query affair only. Matching a long term against
         initials would let any word starting with the right letters claim a page. */
      var a = short ? findTerm(section.acronyms, t) : { kind: NONE, n: 0 };

      /* A short term found only mid-word is not found. Dropping it here rather than
         scoring it zero is what makes "CLI" fail to match a section about clicks at
         all, instead of matching it weakly and still filling the panel. */
      if (short) {
        if (k.kind === INSIDE) k = { kind: NONE, n: 0 };
        if (h.kind === INSIDE) h = { kind: NONE, n: 0 };
        if (d.kind === INSIDE) d = { kind: NONE, n: 0 };
        if (a.kind === INSIDE) a = { kind: NONE, n: 0 };
      }

      /* Every term must appear somewhere. An OR search over a 40-page manual
         returns the whole manual. */
      if (k.kind + h.kind + d.kind + x.kind + a.kind === 0) return 0;

      total += fieldScore(k.kind, 30, 20, 0);
      total += fieldScore(d.kind, 18, 12, 4);
      total += fieldScore(h.kind, 13,  8, 3);
      total += fieldScore(x.kind,  5,  3, 1) * Math.min(x.n, 4);
      /* Below an explicit keyword, above a title prefix: an acronym the reader had
         to know is a strong signal, but a page that DECLARED the term still wins. */
      total += fieldScore(a.kind, 26, 12, 0);
    }

    /* The whole query, in order, is a much stronger signal than its words apart —
       but only where it lands on a word boundary, and for a short query only where
       it is a whole word. Without that, a three-letter phrase collected the full
       heading bonus from any longer word that happened to start with it. */
    if (phrase.length > 2) {
      var need = phrase.length < SHORT_TERM ? WHOLE : PREFIX;
      var pk = findTerm(section.lcKeywords, phrase).kind;
      var ph = findTerm(section.lcHeading,  phrase).kind;
      var pd = findTerm(section.lcTitle,    phrase).kind;
      var px = phrase.length < SHORT_TERM ? NONE : findTerm(section.lcText, phrase).kind;

      if      (pk >= need) total += 45;
      else if (ph >= need) total += 40;
      /* Equal to the heading bonus, not below it. A query that names a whole chapter
         is asking for the chapter, and ranking a section heading above the page
         title is what sent "command line" to a Quick Start subsection. */
      else if (pd >= need) total += 40;
      else if (px >= need) total += 12;
    }

    /* A name that IS the query is the answer, not a hit inside a longer one.
       Compared with articles and punctuation stripped from both sides. */
    var bare = bareName(phrase);
    if (bare.length > 0 && (section.nameHeading === bare || section.nameTitle === bare)) total += 60;
    else if (declaredSection(section, bare)) total += 60;

    /* Reading order as a tie-break with a soft edge. Two sections genuinely can
       answer a query equally well — "Hierarchy" heads a section in both editors —
       and when they do, the one the documentation puts FIRST is the one to send a
       reader to. The spread is 15%, so it only ever decides a near-tie; it cannot
       lift a weak match over a strong one. */
    return total * section.prior;
  }

  /* A query that NAMES a page — its title, or one of the phrases its keywords
     declare — opens that page at the TOP, not at whichever subsection happened to
     repeat the word most. Keywords and title belong to the page, so every section
     of it collects them equally, and the tie was broken by the section's own
     prose: "sensitivity" opened its chapter halfway down, at the section that
     compares it with Tuning, and "monte carlo" at the Yield chapter's list of modes.

     Where on the page: its top, unless a subsection's heading IS the query
     ("corners" → the Corners section of the Yield chapter; a clause counts, so
     "hierarchy" → "Hierarchy: putting one schematic inside another") or the page's
     keywords tie the two together (see declaredSection). A heading that
     merely CONTAINS the word does not count — "dielectric" opens the Stackup
     chapter, not its section on the capacitor dielectric.

     Which page: a page that declares the query outranks every page that does not,
     by lifting all the declaring pages by one common amount, so among themselves
     they keep the order their own sections earned. Without that, a two-line
     "Optimizer" section that only points to the Optimization chapter outranked
     the chapter, because its heading equalled the query. */
  function liftPageTops(hits, phrase) {
    var bare = bareName(phrase);
    if (bare.length === 0) return;
    var best = {}, top = {}, named = {};
    for (var i = 0; i < hits.length; i++) {
      var h = hits[i], slug = h.section.slug;
      if (!(slug in best) || h.score > best[slug]) best[slug] = h.score;
      if (h.section.anchor === '') top[slug] = h;
      else if (h.section.nameHeading === bare || h.section.nameClauses.indexOf(bare) >= 0
               || declaredSection(h.section, bare)) {
        if (!named[slug] || h.score > named[slug].score) named[slug] = h;
      }
    }

    var landing = {}, maxDeclared = -1, maxOther = 0;
    for (var key in top) {
      var t = top[key].section;
      if (t.nameTitle === bare || findTerm(t.lcKeywords, phrase).kind === WHOLE) {
        landing[key] = t.nameTitle === bare ? top[key] : (named[key] || top[key]);
        if (best[key] > maxDeclared) maxDeclared = best[key];
      }
    }
    if (maxDeclared < 0) return;
    for (var slug2 in best) if (!(slug2 in landing) && best[slug2] > maxOther) maxOther = best[slug2];

    var lift = Math.max(0, maxOther - maxDeclared) + 0.01;
    for (var k2 in landing) landing[k2].score = best[k2] + lift;
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

    liftPageTops(hits, phrase);
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

  /* The ranking, exposed so it can be GATED. tests/Ui.Tests/DocsSearchRankingTests.cs
     drives this exact function against the shipped index — the alternative was a
     second implementation of the scoring in C#, which would only ever prove that the
     copy agrees with itself. One property on window, written once, read by nothing
     else in the page. */
  window.CRF_DOCS_SEARCH_RANK = function (query) {
    return search(query).map(function (h) {
      return { slug: h.section.slug, anchor: h.section.anchor,
               heading: h.section.heading, title: h.section.pageTitle, score: h.score };
    });
  };

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
