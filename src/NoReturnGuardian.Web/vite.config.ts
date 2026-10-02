import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import { viteSingleFile } from 'vite-plugin-singlefile';

// 产物是一份自包含的 dist/index.html，由 NoReturnGuardian.App 作为嵌入资源编进 exe。
// 不要把 outDir 指到 App 目录里：Windows 不分大小写，ui 与 Ui 是同一个目录，emptyOutDir 会清掉源码。
export default defineConfig({
  plugins: [react(), viteSingleFile()],
  build: {
    outDir: 'dist',
    emptyOutDir: true,
    target: 'chrome120',
    assetsInlineLimit: 100_000_000,
    cssCodeSplit: false,
    reportCompressedSize: false,
  },
  server: { port: 5178, strictPort: true },
});
