import { reactive } from 'vue'
import type { AdminUser } from '@/api/types'

const KEY = 'flyknit-admin-session'

interface Saved {
  token: string
  displayName: string
  username: string
}

function load(): Saved | null {
  try {
    return JSON.parse(sessionStorage.getItem(KEY) ?? localStorage.getItem(KEY) ?? 'null')
  } catch {
    return null
  }
}

const saved = load()

// 令牌默认只放 sessionStorage（关掉浏览器就失效）；勾了「记住我」才放 localStorage。
// 服务端会话本身 12 小时过期，这里不另外计时
export const auth = reactive({
  token: saved?.token ?? '',
  username: saved?.username ?? '',
  displayName: saved?.displayName ?? '',
  user: null as AdminUser | null,
})

export function setSession(token: string, username: string, displayName: string, remember: boolean) {
  auth.token = token
  auth.username = username
  auth.displayName = displayName
  const value = JSON.stringify({ token, username, displayName })
  sessionStorage.removeItem(KEY)
  localStorage.removeItem(KEY)
  ;(remember ? localStorage : sessionStorage).setItem(KEY, value)
}

export function clearSession() {
  auth.token = ''
  auth.user = null
  sessionStorage.removeItem(KEY)
  localStorage.removeItem(KEY)
}
