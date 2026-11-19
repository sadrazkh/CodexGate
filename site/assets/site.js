'use strict';
// CodexGate site: theme switch, copy buttons, the menu on phones, the screenshot tabs and the lightbox.
(() => {
  const root = document.documentElement;
  const fa = root.lang === 'fa';

  // ---------- theme: follows the system until the visitor picks one ----------
  const stored = (() => { try { return localStorage.getItem('cg-theme'); } catch (e) { return null; } })();
  if (stored === 'light' || stored === 'dark') root.dataset.theme = stored;
  const themeBtn = document.querySelector('[data-theme-toggle]');
  if (themeBtn) {
    themeBtn.addEventListener('click', () => {
      const dark = root.dataset.theme ? root.dataset.theme === 'dark' : matchMedia('(prefers-color-scheme: dark)').matches;
      root.dataset.theme = dark ? 'light' : 'dark';
      try { localStorage.setItem('cg-theme', root.dataset.theme); } catch (e) { /* not kept */ }
    });
  }

  // ---------- copy ----------
  const copied = fa ? 'کپی شد' : 'Copied';
  document.querySelectorAll('[data-copy]').forEach((btn) => {
    const label = btn.querySelector('span');
    const original = label ? label.textContent : '';
    btn.addEventListener('click', async () => {
      const source = document.getElementById(btn.dataset.copy);
      const text = source ? source.innerText.trim() : '';
      try {
        await navigator.clipboard.writeText(text);
      } catch (e) {
        const area = document.createElement('textarea');
        area.value = text;
        area.style.position = 'fixed';
        area.style.opacity = '0';
        document.body.append(area);
        area.select();
        document.execCommand('copy');
        area.remove();
      }
      btn.classList.add('done');
      if (label) label.textContent = copied;
      setTimeout(() => { btn.classList.remove('done'); if (label) label.textContent = original; }, 1600);
    });
  });

  // ---------- menu on phones ----------
  const nav = document.querySelector('.nav');
  const menuBtn = document.querySelector('.menu-btn');
  if (nav && menuBtn) {
    menuBtn.addEventListener('click', () => {
      const open = nav.classList.toggle('open');
      menuBtn.setAttribute('aria-expanded', String(open));
    });
    nav.querySelectorAll('a').forEach((a) => a.addEventListener('click', () => {
      nav.classList.remove('open');
      menuBtn.setAttribute('aria-expanded', 'false');
    }));
  }

  // ---------- screenshot tabs ----------
  const tabs = [...document.querySelectorAll('.tab')];
  const shots = [...document.querySelectorAll('.shot')];
  const show = (id) => {
    tabs.forEach((t) => t.setAttribute('aria-selected', String(t.dataset.shot === id)));
    shots.forEach((s) => s.classList.toggle('active', s.id === id));
  };
  tabs.forEach((t) => t.addEventListener('click', () => show(t.dataset.shot)));

  // ---------- lightbox: a plain overlay, so closing is always just removing it ----------
  let box = null;
  const close = () => { if (box) { box.remove(); box = null; } };
  const open = (src, alt) => {
    close();
    box = document.createElement('div');
    box.className = 'lightbox';
    box.setAttribute('role', 'dialog');
    box.setAttribute('aria-label', alt || '');
    const img = document.createElement('img');
    img.src = src;
    img.alt = alt || '';
    box.append(img);
    box.addEventListener('click', close);
    document.body.append(box);
  };
  document.addEventListener('keydown', (e) => { if (e.key === 'Escape') close(); });
  document.querySelectorAll('[data-zoom]').forEach((el) => {
    el.addEventListener('click', () => {
      const img = el.querySelector('img');
      if (img) open(img.currentSrc || img.src, img.alt);
    });
  });

  // ---------- reveal on scroll ----------
  const items = document.querySelectorAll('.reveal');
  if ('IntersectionObserver' in window) {
    const io = new IntersectionObserver((entries) => {
      entries.forEach((e) => { if (e.isIntersecting) { e.target.classList.add('in'); io.unobserve(e.target); } });
    }, { rootMargin: '0px 0px -8% 0px' });
    items.forEach((el) => io.observe(el));
  } else {
    items.forEach((el) => el.classList.add('in'));
  }
})();
