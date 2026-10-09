import { createRouter, createWebHashHistory, type RouteRecordRaw } from 'vue-router'
import type { Component } from 'vue'
import { ChatDotRound, Coin, Collection, Connection, Document, Link, Lock, MagicStick, Microphone, Monitor, Odometer, Promotion, Tickets, Upload, UserFilled } from '@element-plus/icons-vue'
import { auth } from '@/store/auth'
import AdminLayout from '@/layout/AdminLayout.vue'

declare module 'vue-router' {
  interface RouteMeta {
    title?: string
    icon?: Component
    group?: string
    public?: boolean
  }
}

export const menuRoutes: RouteRecordRaw[] = [
  { path: 'dashboard', name: 'dashboard', component: () => import('@/views/Dashboard.vue'), meta: { title: '首页统计', icon: Odometer, group: '概览' } },
  { path: 'models', name: 'models', component: () => import('@/views/Models.vue'), meta: { title: '模型路由', icon: Connection, group: '配置' } },
  { path: 'quota', name: 'quota', component: () => import('@/views/Quota.vue'), meta: { title: '配额与用量', icon: Coin, group: '配置' } },
  { path: 'skills', name: 'skills', component: () => import('@/views/Skills.vue'), meta: { title: '技能库', icon: Collection, group: '配置' } },
  { path: 'mcp', name: 'mcp', component: () => import('@/views/Mcp.vue'), meta: { title: 'MCP 连接器', icon: Link, group: '配置' } },
  { path: 'speech', name: 'speech', component: () => import('@/views/Speech.vue'), meta: { title: '语音转文字', icon: Microphone, group: '配置' } },
  { path: 'devices', name: 'devices', component: () => import('@/views/Devices.vue'), meta: { title: '设备列表', icon: Monitor, group: '运维' } },
  { path: 'security', name: 'security', component: () => import('@/views/Security.vue'), meta: { title: '安全中心', icon: Lock, group: '运维' } },
  { path: 'tasks', name: 'tasks', component: () => import('@/views/Tasks.vue'), meta: { title: '任务中心', icon: Promotion, group: '运维' } },
  { path: 'instructions', name: 'instructions', component: () => import('@/views/Instructions.vue'), meta: { title: '指令中心', icon: MagicStick, group: '运维' } },
  { path: 'chats', name: 'chats', component: () => import('@/views/Chats.vue'), meta: { title: '聊天记录', icon: ChatDotRound, group: '运维' } },
  { path: 'audit', name: 'audit', component: () => import('@/views/Audit.vue'), meta: { title: '审计日志', icon: Tickets, group: '运维' } },
  { path: 'releases', name: 'releases', component: () => import('@/views/Releases.vue'), meta: { title: '员工端版本', icon: Upload, group: '运维' } },
  { path: 'legal', name: 'legal', component: () => import('@/views/Legal.vue'), meta: { title: '协议与隐私', icon: Document, group: '运维' } },
  { path: 'accounts', name: 'accounts', component: () => import('@/views/Accounts.vue'), meta: { title: '管理员账号', icon: UserFilled, group: '运维' } },
]

const routes: RouteRecordRaw[] = [
  { path: '/login', name: 'login', component: () => import('@/views/Login.vue'), meta: { title: '登录', public: true } },
  { path: '/', component: AdminLayout, redirect: '/dashboard', children: menuRoutes },
  { path: '/:pathMatch(.*)*', redirect: '/dashboard' },
]

// hash 路由：静态文件直接挂在 /admin 下就能用，服务端不用做页面回退
const router = createRouter({ history: createWebHashHistory(), routes })

router.beforeEach((to) => {
  if (!to.meta.public && !auth.token) {
    return { name: 'login', query: to.fullPath !== '/dashboard' ? { redirect: to.fullPath } : {} }
  }
  if (to.name === 'login' && auth.token) return { path: '/dashboard' }
})

router.afterEach((to) => {
  document.title = to.meta.title ? `${to.meta.title} · FlyknitBuddy 管理后台` : 'FlyknitBuddy 管理后台'
})

// 后台重新构建后，已打开的页面还引用着旧文件名的分包，点菜单会 404；整页刷新到目标地址就好
const RELOAD_KEY = 'flyknit-admin-chunk-reload'
router.onError((err, to) => {
  if (!/dynamically imported module|Importing a module script failed|Failed to fetch/i.test(String(err?.message ?? err))) return
  if (sessionStorage.getItem(RELOAD_KEY) === to.fullPath) return
  sessionStorage.setItem(RELOAD_KEY, to.fullPath)
  location.hash = to.fullPath
  location.reload()
})
router.isReady().then(
  () => sessionStorage.removeItem(RELOAD_KEY),
  () => {},
)

export default router
