import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'
import { fileURLToPath } from 'node:url'
import { cpSync } from 'node:fs'

const outDir = fileURLToPath(new URL('../src/Flyknit.Client/wwwroot', import.meta.url))

/** PDF 预览要的中文字符映射表和标准字体：拷进产物，断网也能显示没嵌字体的 PDF */
function pdfAssets() {
  return {
    name: 'flyknit-pdf-assets',
    apply: 'build' as const,
    closeBundle() {
      for (const dir of ['cmaps', 'standard_fonts']) {
        cpSync(fileURLToPath(new URL(`./node_modules/pdfjs-dist/${dir}`, import.meta.url)), `${outDir}/pdfjs/${dir}`, { recursive: true })
      }
    },
  }
}

// 构建产物直接输出到 WPF 项目的 wwwroot，由 WebView2 加载
export default defineConfig({
  plugins: [vue(), pdfAssets()],
  base: './',
  build: {
    outDir,
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
  worker: { format: 'es' },
  server: { port: 5173 },
})
