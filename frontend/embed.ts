import ChatWidget from './lib/FlytChat.svelte';

(function () {
  // Finn <script data-flytit-chatbot ...>
  const scriptEl = document.querySelector('script[data-flytit-chatbot]') as HTMLScriptElement;
  if (!scriptEl) return;

  const cfg = {
    apiBase: scriptEl.dataset.apiBase!,
    siteId: scriptEl.dataset.siteId!,
    theme: scriptEl.dataset.theme ?? 'auto',
    position: scriptEl.dataset.position ?? 'bottom-right',
    language: scriptEl.dataset.language ?? 'nb',
  };

  const container = document.createElement('div');
  container.id = 'flytit-chatbot-root';
  document.body.appendChild(container);

  new ChatWidget({
    target: container,
    props: { cfg }
  });
})();
