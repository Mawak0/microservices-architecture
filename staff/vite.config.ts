import { defineConfig } from 'vitest/config';

export default defineConfig({
  test: {
    environment: 'jsdom',
    environmentOptions: { jsdom: { url: 'http://localhost/' } },
    coverage: {
      provider: 'v8',
      include: ['src/**/*.{ts,tsx}', 'server/*.mjs'],
      reporter: ['text', 'json-summary'],
      thresholds: { lines: 85 },
    },
  },
});
