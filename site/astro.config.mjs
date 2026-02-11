import { defineConfig } from 'astro/config';

export default defineConfig({
  site: 'https://jeremiahjordanisaacson.github.io',
  base: '/authosy',
  output: 'static',
  build: {
    format: 'directory',
  },
  content: {
    collections: {
      stories: {
        schema: 'src/content/stories',
      },
    },
  },
});
