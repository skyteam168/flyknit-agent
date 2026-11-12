import { createApp } from 'vue'
import LoginApp from './LoginApp.vue'
import { setLanguage } from './i18n'
import { host } from './host'
import { installDragFallback } from '../appRegion'

setLanguage(new URLSearchParams(location.search).get('lang') ?? 'zh-CN')
// 老 WebView2 运行时上页面拖动区域不起作用：改由宿主拖动窗口（登录窗不能最大化，没有双击）
installDragFallback(() => host.window('drag').catch(() => undefined))
createApp(LoginApp).mount('#app')
