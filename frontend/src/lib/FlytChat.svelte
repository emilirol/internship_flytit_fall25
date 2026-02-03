<script>
  import { onMount, tick } from 'svelte';
  import chatSvg from '../svgs/speech-bubble-15-svgrepo-com.svg?raw';

  // PROPS (uten typer)
  export let backendUrl;
  export let title = 'Widget ChatBot';
  export let position = 'right'; // 'right' | 'left'
  export let placeholder = 'Spør oss...!';
  export let accent = '#ed6a6ae4';
  export let botName = 'Widget ChatBot';
  export let site; // valgfritt filter som sendes til backend
  export let historyMode = 'local'; // 'local' | 'session' | 'none'
  export let historyTtlMs = 0;      // 0 = ingen utløp
  export let storageNamespace = ''; // f.eks. "prod" eller site

  // STATE
  let isOpen = false;
  let input = '';
  let messages = [];
  let loading = false;
  let messagesEl = null;

  // Velg lagring (reaktivt hvis historyMode endres)
  let store = null;
  $: store =
    historyMode === 'session'
      ? (typeof sessionStorage !== 'undefined' ? sessionStorage : null)
      : historyMode === 'local'
      ? (typeof localStorage !== 'undefined' ? localStorage : null)
      : null;

  const baseKey = 'flyt-chatbot-history' + (storageNamespace ? `:${storageNamespace}` : '');
  const msgKey  = `${baseKey}:messages`;
  const metaKey = `${baseKey}:meta`;

  let meta = { savedAt: 0 };

  const uid = () => Math.random().toString(36).slice(2);
  const now = () => Date.now();

  function expired(m) {
    if (!m) return false;
    if (!historyTtlMs || historyTtlMs <= 0) return false;
    return now() - (m.savedAt || 0) > historyTtlMs;
  }

  function loadFromStore() {
    if (!store) return;
    try {
      const rawMeta = store.getItem(metaKey);
      const rawMsgs = store.getItem(msgKey);
      const m = rawMeta ? JSON.parse(rawMeta) : null;
      if (!rawMsgs) return;
      if (expired(m)) {
        store.removeItem(msgKey);
        store.removeItem(metaKey);
        return;
      }
      const arr = JSON.parse(rawMsgs);
      if (Array.isArray(arr)) {
        messages = arr;
        meta = m || { savedAt: 0 };
      }
    } catch {}
  }

  function saveToStore() {
    if (!store || historyMode === 'none') return;
    try {
      store.setItem(msgKey, JSON.stringify(messages));
      store.setItem(metaKey, JSON.stringify({ savedAt: now() }));
    } catch {}
  }

  // last ved mount
  onMount(() => {
    loadFromStore();

    // 👉 Legg til velkomstmelding kun hvis ingen historikk finnes
    if (messages.length === 0) {
      messages = [
        ...messages,
        {
          id: uid(),
          role: 'assistant',
          text: '👋 Velkommen til ! Hva kan jeg hjelpe deg med i dag?'
        }
      ];
    }

    scrollToBottom();
  });

  // lagre når meldinger endres (enkelt og greit)
  $: saveToStore();

  function scrollToBottom() {
    if (messagesEl) messagesEl.scrollTop = messagesEl.scrollHeight;
  }

  function escapeHtml(s) {
    return s.replace(/[&<>"']/g, (ch) => (
      { '&':'&amp;', '<':'&lt;', '>':'&gt;', '"':'&quot;', "'":'&#39;' }[ch]
    ));
  }

  function autolink(s) {
    const safe = escapeHtml(s);
    const urlRe = /\bhttps?:\/\/[^\s)]+/g;
    return safe.replace(urlRe, (m) => `<a href="${m}" target="_blank" rel="noopener noreferrer nofollow">${m}</a>`);
  }

  function normalizeLinks(v) {
    if (!Array.isArray(v)) return [];
    const out = [];
    const seen = new Set();
    for (const it of v) {
      const title = String((it && it.title) ?? '').trim();
      const url = String((it && it.url) ?? '').trim();
      if (!url || (!url.startsWith('http://') && !url.startsWith('https://'))) continue;
      const key = url.toLowerCase();
      if (seen.has(key)) continue;
      seen.add(key);
      out.push({ title: title || url, url });
    }
    return out.slice(0, 5);
  }

  async function sendMessage() {
    const text = input.trim();
    if (!text || loading) return;

    loading = true;
    const userMsg = { id: uid(), role: 'user', text };
    messages = [...messages, userMsg];
    input = '';

    try {
      const res = await fetch(backendUrl, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          message: text,
          history: messages.map(m => ({ role: m.role, content: m.text })),
          site
        })
      });
      if (!res.ok) throw new Error('Feil fra server');
      const data = await res.json();
      const botText = String(data.reply ?? data.message ?? '');
      const sources = normalizeLinks(data.sources);
      messages = [...messages, { id: uid(), role: 'assistant', text: botText || '…', sources }];
    } catch (e) {
      messages = [...messages, { id: uid(), role: 'system', text: 'Kunne ikke sende melding. Prøv igjen.' }];
      console.error(e);
    } finally {
      loading = false;
      await tick();
      scrollToBottom();
    }
  }
</script>

<!-- Henter CSS for komponenten -->
<style src="./FlytChat.css"></style>

<div
  class="flyt-container"
  class:right={position === 'right'}
  class:left={position === 'left'}
  class:open={isOpen}
  style={`--accent:${accent}`}
>
  {#if isOpen}
    <div class="window" role="dialog" aria-label={title} id="flyt-chat">
      <div class="header">{title}</div>

      <div class="messages" bind:this={messagesEl}>
        {#each messages as m}
          <div class="row {m.role}">
            <div class="bubble {m.role}">
              {#if m.role === 'assistant'}
                <strong>{botName}:</strong>
                <span>{@html autolink(m.text)}</span>
              {:else}
                {m.text}
              {/if}

              {#if m.role === 'assistant' && m.sources && m.sources.length}
                <ul class="sources" aria-label="Kilder">
                  <li class="label">Kilder:</li>
                  {#each m.sources as s}
                    <li>🔗 <a href={s.url} target="_blank" rel="noopener noreferrer nofollow">{s.title}</a></li>
                  {/each}
                </ul>
              {/if}
            </div>
          </div>
        {/each}
        {#if loading}
          <div class="row assistant loading"><div class="bubble assistant">Skriver…</div></div>
        {/if}
      </div>

      <div class="inputRow">
        <input
          type="text"
          bind:value={input}
          placeholder={placeholder}
          on:keydown={(e) => e.key === 'Enter' && sendMessage()}
          aria-label="Skriv melding"
        />
        <button class="send" on:click={sendMessage} aria-label="Send">Send</button>
      </div>
    </div>
  {/if}

  <button
    class="toggle"
    on:click={() => (isOpen = !isOpen)}
    aria-expanded={isOpen}
    aria-controls="flyt-chat"
    aria-label={isOpen ? 'Lukk chat' : 'Åpne chat'}
  >
    {#if isOpen}
      X
    {:else}
      <span class="chatbubble" aria-hidden="true">{@html chatSvg}</span>
    {/if}
  </button>
</div>
