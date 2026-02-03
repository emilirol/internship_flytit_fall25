<script>
  import FlytChat from './lib/FlytChat.svelte';
  import './lib/FlytChat.css';

  let accent = '#ed6a6ae4';
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
