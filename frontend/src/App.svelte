<script>
  import FlytChat from './lib/FlytChat.svelte';
  import './lib/FlytChat.css';

  let accent = '#DD66FE';
  let position = 'right'; // 'left' er også gyldig
  let title = 'Widget ChatBot';
  let placeholder = 'Spør oss...!';
  let botName = 'Widget ChatBot';
</script>

<FlytChat
  backendUrl="/api/chat"
  {accent}
  {position}
  {title}
  {placeholder}
  {botName}
/>
