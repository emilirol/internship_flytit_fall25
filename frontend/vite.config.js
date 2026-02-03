import { defineConfig } from 'vite';
import { svelte } from '@sveltejs/vite-plugin-svelte';

export default defineConfig({
  plugins: [svelte()],
  server: {
    proxy: {
      // Alt som begynner på /api videresendes til backend på :5000
      '/api': {
        target: 'http://localhost:5000',
        changeOrigin: true,
        // Fjerner /api-prefikset når vi treffer backend
        rewrite: (path) => path.replace(/^\/api/, '')
      }
    }
  }
});
