// Loads the UI messages of Monaco in the app language (?lang=fr) before the editor itself: Monaco reads them while
// its modules are evaluated. English needs no file.
(() => {
  const lang = new URLSearchParams(location.search).get('lang');
  const load = (src, module) => new Promise((resolve, reject) => {
    const script = document.createElement('script');
    script.src = src;
    if (module) script.type = 'module';
    script.onload = resolve;
    script.onerror = reject;
    document.head.appendChild(script);
  });
  const messages = lang && lang !== 'en' ? load(`nls.${lang}.js`).catch(() => undefined) : Promise.resolve();
  messages.then(() => load('editor.js', true));
})();
