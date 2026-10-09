import { createApp } from 'vue'
import LoginApp from './LoginApp.vue'
import { setLanguage } from './i18n'

setLanguage(new URLSearchParams(location.search).get('lang') ?? 'zh-CN')
createApp(LoginApp).mount('#app')
