/*
  Bruno's Garden & Snacks — progressive enhancement.

  Everything here is optional polish: if the file never runs the site stays
  fully usable, and nothing is hidden until the reveal observer is armed.
  Re-runs itself after Blazor swaps the page so state survives navigation.
*/
(() => {
  'use strict';

  const root = document.documentElement;
  const reduced = window.matchMedia('(prefers-reduced-motion: reduce)');

  /* ---------------------------------------------------------------- reveal */

  const REVEAL_TARGETS = [
    '.club-spotlight',
    '.intro-grid > article',
    '.featured > div',
    '.featured__products > article',
    '.order-preview',
    '.menu-section',
    '.allergen-note',
    '.promotion-grid > article',
    '.promo-join',
    '.legal-callout',
    '.club-explainer > article',
    '.club-details > div',
    '.club-details > ul',
    '.club-details__action',
    '.visit-grid > article',
    '.member-kpis > article',
    '.member-panel'
  ].join(',');

  let observer = null;

  function armReveal() {
    if (reduced.matches || !('IntersectionObserver' in window)) return;

    root.classList.add('reveal-ready');

    if (!observer) {
      observer = new IntersectionObserver((entries) => {
        for (const entry of entries) {
          if (!entry.isIntersecting) continue;
          entry.target.classList.add('is-revealed');
          observer.unobserve(entry.target);
        }
      }, { rootMargin: '0px 0px -6% 0px', threshold: 0.05 });
    }

    for (const el of document.querySelectorAll(REVEAL_TARGETS)) {
      if (el.dataset.reveal !== undefined) continue;
      el.dataset.reveal = '';

      // Anything already on screen shows at once — no flash, no waiting.
      if (el.getBoundingClientRect().top < window.innerHeight * 0.94) {
        el.classList.add('is-revealed');
        continue;
      }

      const index = el.parentElement ? [...el.parentElement.children].indexOf(el) : 0;
      if (index > 0) el.style.setProperty('--reveal-delay', `${Math.min(index, 5) * 70}ms`);
      observer.observe(el);
    }
  }

  /* ------------------------------------------------------------ menu spy */

  let currentSection = '';

  function syncSectionJump() {
    const links = document.querySelectorAll('.section-jump a');
    const sections = document.querySelectorAll('.menu-section[id]');
    if (!links.length || !sections.length) return;

    // Measure the sticky stack rather than guessing: a section counts as
    // current once its heading clears the chip strip.
    const rem = parseFloat(getComputedStyle(root).fontSize) || 16;
    const strip = document.querySelector('.section-jump');
    const line = (strip ? strip.getBoundingClientRect().bottom : rem * 5) + rem * 1.5;

    let active = sections[0].id;
    for (const section of sections) {
      if (section.getBoundingClientRect().top <= line) active = section.id;
    }

    for (const link of links) {
      const href = link.getAttribute('href') || '';
      const hash = href.indexOf('#');
      const isCurrent = hash >= 0 && href.slice(hash + 1) === active;
      link.classList.toggle('is-current', isCurrent);

      // Keep the active chip visible in the horizontal strip.
      if (isCurrent && active !== currentSection) {
        const strip = link.parentElement;
        if (strip && strip.scrollWidth > strip.clientWidth) {
          const offset = link.offsetLeft - (strip.clientWidth - link.offsetWidth) / 2;
          strip.scrollTo({ left: Math.max(0, offset), behavior: reduced.matches ? 'auto' : 'smooth' });
        }
      }
    }

    currentSection = active;
  }

  /* ---------------------------------------------------------- chip clicks */

  /* The chips link to /menu#section so they still work with scripting off —
     which costs a full page load. Intercept them and scroll in place instead. */
  function onDocumentClick(event) {
    const link = event.target.closest?.('.section-jump a[href*="#"]');
    if (!link || event.metaKey || event.ctrlKey || event.shiftKey || event.button !== 0) return;

    const href = link.getAttribute('href') || '';
    const id = href.slice(href.indexOf('#') + 1);
    const target = id && document.getElementById(id);
    if (!target) return;

    event.preventDefault();
    target.scrollIntoView({ behavior: reduced.matches ? 'auto' : 'smooth', block: 'start' });
    history.replaceState(null, '', href);
    currentSection = '';
    syncSectionJump();
  }

  /* --------------------------------------------------------------- scroll */

  let frame = 0;

  function onScroll() {
    // Cancel-and-reschedule rather than a latch: a frame that never arrives
    // (hidden tab, bfcache) must not leave the handler permanently disarmed.
    cancelAnimationFrame(frame);
    frame = requestAnimationFrame(() => {
      root.classList.toggle('is-scrolled', window.scrollY > 8);
      syncSectionJump();
    });
  }

  /* ---------------------------------------------------------------- boot */

  function refresh() {
    armReveal();
    root.classList.toggle('is-scrolled', window.scrollY > 8);
    syncSectionJump();
  }

  document.addEventListener('click', onDocumentClick);
  window.addEventListener('scroll', onScroll, { passive: true });
  window.addEventListener('resize', onScroll, { passive: true });
  window.addEventListener('pageshow', refresh);
  document.addEventListener('visibilitychange', () => { if (!document.hidden) refresh(); });
  reduced.addEventListener?.('change', refresh);

  // Blazor re-renders the page in place; re-arm once the dust settles.
  let pending = 0;
  new MutationObserver(() => {
    cancelAnimationFrame(pending);
    pending = requestAnimationFrame(refresh);
  }).observe(document.body, { childList: true, subtree: true });

  refresh();
  // A deep link (/menu#postres) scrolls after load; re-sync once it has.
  setTimeout(refresh, 300);
})();
