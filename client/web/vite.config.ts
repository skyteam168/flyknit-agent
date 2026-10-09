import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'
import { fileURLToPath } from 'node:url'

// 构建产物直接输出到 WPF 项目的 wwwroot，由 WebView2 加载
export default defineConfig({
  plugins: [vue()],
  base: './',
  build: {
    outDir: fileURLToPath(new URL('../src/Flyknit.Client/wwwroot', import.meta.url)),
    emptyOutDir: true,
    chunkSizeWarningLimit: 1500,
    // 登录窗口单独一个页面：没登录时宿主还没有完整的 AgentHost，主界面跑不起来
    rollupOptions: {
      input: {
        main: fileURLToPath(new URL('./index.html', import.meta.url)),
        login: fileURLToPath(new URL('./login.html', import.meta.url)),
      },
    },
  },
  server: { port: 5173 },
})
