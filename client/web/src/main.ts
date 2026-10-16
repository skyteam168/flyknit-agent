import { createApp } from 'vue'
import App from './App.vue'
import { i18n } from './i18n'
import './styles/base.css'
import { fail } from './store'

// WebView2 中禁止拖入文件时浏览器直接打开文件
window.addEventListener('dragover', (e) => e.preventDefault())
window.addEventListener('drop', (e) => e.preventDefault())

// 记下最近一次是用键盘还是鼠标操作的：焦点框只给键盘用户看（base.css）
const root = document.documentElement
window.addEventListener('keydown', (e) => {
  if (e.key === 'Tab' || e.key.startsWith('Arrow')) root.dataset.input = 'keyboard'
}, true)
window.addEventListener('pointerdown', () => { root.dataset.input = 'pointer' }, true)

const app = createApp(App).use(i18n)

/**
 * 只有渲染、组件初始化这类失败才算「界面坏了」，需要把原因直接写到页面上。
 * 生产包里 info 是 https://vuejs.org/error-reference/#runtime-1 这样的链接，开发时是英文描述，两种都认。
 */
const FatalCodes = new Set(['0', '1', '13', '14', '15'])
const FatalNames = ['setup function', 'render function', 'async component loader', 'scheduler flush', 'component update']
function isFatal(info: string): boolean {
  const code = /#runtime-(\w+)$/.exec(info)?.[1]
  if (code !== undefined) return FatalCodes.has(code)
  return FatalNames.includes(info)
}

app.config.errorHandler = (err, _instance, info) => {
  console.error(err, info)
  // 点按钮、监听回调、生命周期里的异步操作失败（多半是宿主调用出错）：提示一下就好，界面还能接着用
  if (!isFatal(info)) {
    fail(err)
    return
  }
  // 渲染异常写到页面上，方便在客户端里直接看到原因
  const el = document.getElementById('fatal') ?? document.body.appendChild(Object.assign(document.createElement('pre'), { id: 'fatal' }))
  el.setAttribute('style', 'position:fixed;left:12px;right:12px;bottom:12px;z-index:999;padding:12px;background:#fdeceb;color:#c4362d;font:12px Consolas,monospace;white-space:pre-wrap;border-radius:8px')
  el.textContent = String((err as Error)?.stack ?? err)
  // 界面可能已经坏了一块：给一个按钮直接重新加载，不用退出整个程序（对话和任务都在宿主里，不会丢）
  const btn = document.createElement('button')
  btn.textContent = i18n.global.t('ui.render.reload')
  btn.setAttribute('style', 'display:block;margin-top:8px;padding:4px 12px;border:1px solid #c4362d;border-radius:6px;background:#fff;color:#c4362d;cursor:pointer')
  btn.onclick = () => location.reload()
  el.append(btn)
}
app.mount('#app')
