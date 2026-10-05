import { createApp } from 'vue'
import App from './App.vue'
import { i18n } from './i18n'
import './styles/base.css'

// WebView2 中禁止拖入文件时浏览器直接打开文件
window.addEventListener('dragover', (e) => e.preventDefault())
window.addEventListener('drop', (e) => e.preventDefault())

createApp(App).use(i18n).mount('#app')
