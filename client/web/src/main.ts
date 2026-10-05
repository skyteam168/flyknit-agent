import { createApp } from 'vue'
import App from './App.vue'
import { i18n } from './i18n'
import './styles/base.css'

// WebView2 中禁止拖入文件时浏览器直接打开文件
window.addEventListener('dragover', (e) => e.preventDefault())
window.addEventListener('drop', (e) => e.preventDefault())

const app = createApp(App).use(i18n)
// 渲染异常写到页面上，方便在客户端里直接看到原因
app.config.errorHandler = (err) => {
  console.error(err)
  const el = document.getElementById('fatal') ?? document.body.appendChild(Object.assign(document.createElement('pre'), { id: 'fatal' }))
  el.setAttribute('style', 'position:fixed;left:12px;right:12px;bottom:12px;z-index:999;padding:12px;background:#fdeceb;color:#c4362d;font:12px Consolas,monospace;white-space:pre-wrap;border-radius:8px')
  el.textContent = String((err as Error)?.stack ?? err)
}
app.mount('#app')
