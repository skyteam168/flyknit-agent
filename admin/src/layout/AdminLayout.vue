<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter, type RouteLocationNormalizedLoaded } from 'vue-router'
import { ElMessageBox } from 'element-plus'
import { ArrowDown, Close, Expand, Fold, Lock, Moon, Refresh, Sunny, SwitchButton } from '@element-plus/icons-vue'
import logoUrl from '@/assets/logo.png'
import { menuRoutes } from '@/router'
import { api } from '@/api'
import { auth, clearSession } from '@/store/auth'
import { isDark, themeMode } from '@/store/theme'
import ChangePasswordDialog from '@/components/ChangePasswordDialog.vue'

const route = useRoute()
const router = useRouter()

const collapsed = ref(localStorage.getItem('flyknit-admin-collapsed') === '1')
watch(collapsed, (v) => localStorage.setItem('flyknit-admin-collapsed', v ? '1' : '0'))

/** 菜单按 meta.group 分组 */
const groups = computed(() => {
  const out: { name: string; items: typeof menuRoutes }[] = []
  for (const r of menuRoutes) {
    const name = r.meta?.group ?? ''
    let g = out.find((x) => x.name === name)
    if (!g) out.push((g = { name, items: [] }))
    g.items.push(r)
  }
  return out
})

const crumbs = computed(() => {
  const r = menuRoutes.find((m) => m.name === route.name)
  return r ? [r.meta?.group ?? '', r.meta?.title ?? ''].filter(Boolean) : []
})

// ---------- 顶部的已访问页签 ----------
interface Tab {
  name: string
  title: string
  path: string
}
const tabs = ref<Tab[]>([{ name: 'dashboard', title: '首页统计', path: '/dashboard' }])
function track(r: RouteLocationNormalizedLoaded) {
  if (!r.name || r.meta.public) return
  if (!tabs.value.some((t) => t.name === r.name)) {
    tabs.value.push({ name: String(r.name), title: r.meta.title ?? '', path: r.fullPath })
  }
}
watch(() => route.fullPath, () => track(route), { immediate: true })
function closeTab(name: string) {
  const i = tabs.value.findIndex((t) => t.name === name)
  if (i < 0 || name === 'dashboard') return
  tabs.value.splice(i, 1)
  if (route.name === name) router.push(tabs.value[Math.min(i, tabs.value.length - 1)].path)
}

/** 刷新当前页：换个 key 让 router-view 重新挂载 */
const viewKey = ref(0)

// ---------- 账号 ----------
const pwdOpen = ref(false)
const forcedPwd = ref(false)

onMounted(async () => {
  try {
    auth.user = await api.me()
    auth.displayName = auth.user.display_name || auth.user.username
    if (auth.user.must_change_password) {
      forcedPwd.value = true
      pwdOpen.value = true
    }
  } catch {
    // 失效时 http 层已经跳回登录页
  }
})

const initial = computed(() => (auth.displayName || auth.username || '管').slice(0, 1).toUpperCase())

async function logout() {
  try {
    await ElMessageBox.confirm('确定要退出登录吗？', '退出登录', { confirmButtonText: '退出', cancelButtonText: '取消', type: 'warning' })
  } catch {
    return
  }
  await api.logout().catch(() => {})
  clearSession()
  router.replace({ name: 'login' })
}

function onCommand(cmd: string) {
  if (cmd === 'password') {
    forcedPwd.value = false
    pwdOpen.value = true
  } else if (cmd === 'logout') void logout()
}

function toggleTheme() {
  themeMode.value = isDark.value ? 'light' : 'dark'
}
</script>

<template>
  <div class="shell" :class="{ collapsed }">
    <aside class="aside">
      <div class="brand" @click="router.push('/dashboard')">
        <img :src="logoUrl" alt="" class="logo" />
        <div v-show="!collapsed" class="brand-text">
          <strong>FlyknitBuddy</strong>
          <small>管理后台</small>
        </div>
      </div>

      <el-scrollbar class="menu-scroll">
        <el-menu :default-active="String(route.name)" :collapse="collapsed" :collapse-transition="false" router class="menu">
          <template v-for="g in groups" :key="g.name">
            <div v-show="!collapsed" class="menu-group">{{ g.name }}</div>
            <el-menu-item v-for="item in g.items" :key="String(item.name)" :index="String(item.name)" :route="{ name: item.name }">
              <el-icon><component :is="item.meta?.icon" /></el-icon>
              <template #title>{{ item.meta?.title }}</template>
            </el-menu-item>
          </template>
        </el-menu>
      </el-scrollbar>

      <div v-show="!collapsed" class="aside-foot">员工端安全与模型配置，一处管理</div>
    </aside>

    <section class="body">
      <header class="header">
        <button class="icon-btn" :title="collapsed ? '展开菜单' : '收起菜单'" @click="collapsed = !collapsed">
          <el-icon :size="18"><Expand v-if="collapsed" /><Fold v-else /></el-icon>
        </button>
        <el-breadcrumb separator="/" class="crumbs">
          <el-breadcrumb-item :to="{ path: '/dashboard' }">首页</el-breadcrumb-item>
          <el-breadcrumb-item v-for="c in crumbs" :key="c">{{ c }}</el-breadcrumb-item>
        </el-breadcrumb>

        <div class="header-right">
          <el-tooltip content="刷新当前页" placement="bottom">
            <button class="icon-btn" @click="viewKey++"><el-icon :size="17"><Refresh /></el-icon></button>
          </el-tooltip>
          <el-tooltip :content="isDark ? '切换到浅色' : '切换到深色'" placement="bottom">
            <button class="icon-btn" @click="toggleTheme"><el-icon :size="17"><Sunny v-if="isDark" /><Moon v-else /></el-icon></button>
          </el-tooltip>
          <el-dropdown trigger="click" @command="onCommand">
            <button class="user">
              <span class="avatar">{{ initial }}</span>
              <span class="user-name">{{ auth.displayName || auth.username }}</span>
              <el-icon :size="12"><ArrowDown /></el-icon>
            </button>
            <template #dropdown>
              <el-dropdown-menu>
                <div class="user-card">
                  <strong>{{ auth.displayName || auth.username }}</strong>
                  <small>{{ auth.username }} · {{ auth.user?.can_read_chats ? '可查看聊天记录' : '仅管理配置' }}</small>
                </div>
                <el-dropdown-item command="password" :icon="Lock">修改密码</el-dropdown-item>
                <el-dropdown-item command="logout" :icon="SwitchButton" divided>退出登录</el-dropdown-item>
              </el-dropdown-menu>
            </template>
          </el-dropdown>
        </div>
      </header>

      <nav class="tabs">
        <router-link
          v-for="t in tabs"
          :key="t.name"
          :to="t.path"
          class="tab"
          :class="{ on: route.name === t.name }"
        >
          {{ t.title }}
          <el-icon v-if="t.name !== 'dashboard'" class="tab-x" @click.prevent.stop="closeTab(t.name)"><Close /></el-icon>
        </router-link>
      </nav>

      <main class="main">
        <router-view v-slot="{ Component }">
          <transition name="fade" mode="out-in">
            <component :is="Component" :key="`${String(route.name)}-${viewKey}`" />
          </transition>
        </router-view>
      </main>
    </section>

    <ChangePasswordDialog v-model="pwdOpen" :forced="forcedPwd" @done="forcedPwd = false" />
  </div>
</template>

<style scoped>
.shell {
  display: grid;
  grid-template-columns: var(--aside-w) 1fr;
  height: 100%;
  transition: grid-template-columns 0.2s ease;
}
.shell.collapsed {
  grid-template-columns: var(--aside-w-collapsed) 1fr;
}
.aside {
  display: flex;
  flex-direction: column;
  min-width: 0;
  border-right: 1px solid var(--line);
  background: var(--cloth);
}
.brand {
  display: flex;
  gap: 10px;
  align-items: center;
  height: var(--header-h);
  padding: 0 16px;
  cursor: pointer;
  overflow: hidden;
}
.collapsed .brand {
  justify-content: center;
  padding: 0;
}
.logo {
  flex: none;
  width: 34px;
  height: 34px;
  object-fit: contain;
}
.brand-text {
  display: flex;
  flex-direction: column;
  line-height: 1.2;
  white-space: nowrap;
}
.brand-text strong {
  font-size: 15.5px;
  font-weight: 600;
  letter-spacing: -0.01em;
}
.brand-text small {
  color: var(--ink-faint);
  font-size: 11.5px;
}
.menu-scroll {
  flex: 1;
}
.menu {
  --el-menu-bg-color: transparent;
  --el-menu-hover-bg-color: var(--cloth-sunk);
  --el-menu-text-color: var(--ink-soft);
  --el-menu-active-color: var(--indigo);
  --el-menu-item-height: 40px;
  border-right: 0;
  padding: 4px 10px 12px;
}
.menu.el-menu--collapse {
  padding: 4px 8px;
  width: auto;
}
.menu-group {
  padding: 14px 10px 6px;
  color: var(--ink-faint);
  font-size: 11.5px;
  font-weight: 500;
  letter-spacing: 0.04em;
}
.menu :deep(.el-menu-item) {
  margin: 2px 0;
  border-radius: var(--r-md);
  font-weight: 500;
}
.menu :deep(.el-menu-item.is-active) {
  background: var(--indigo-wash);
}
.aside-foot {
  padding: 12px 18px 16px;
  color: var(--ink-faint);
  font-size: 11.5px;
}

.body {
  display: flex;
  flex-direction: column;
  min-width: 0;
  min-height: 0;
}
.header {
  display: flex;
  gap: 12px;
  align-items: center;
  height: var(--header-h);
  padding: 0 16px 0 12px;
  border-bottom: 1px solid var(--line);
  background: var(--cloth);
}
.icon-btn {
  display: inline-grid;
  place-items: center;
  width: 34px;
  height: 34px;
  border: 0;
  border-radius: var(--r-sm);
  background: none;
  color: var(--ink-soft);
  cursor: pointer;
}
.icon-btn:hover {
  background: var(--cloth-sunk);
  color: var(--ink);
}
.crumbs {
  flex: 1;
}
.header-right {
  display: flex;
  gap: 4px;
  align-items: center;
}
.user {
  display: inline-flex;
  gap: 8px;
  align-items: center;
  height: 38px;
  margin-left: 6px;
  padding: 0 10px 0 4px;
  border: 0;
  border-radius: 999px;
  background: none;
  color: var(--ink);
  font: inherit;
  cursor: pointer;
}
.user:hover {
  background: var(--cloth-sunk);
}
.avatar {
  display: grid;
  place-items: center;
  width: 30px;
  height: 30px;
  border-radius: 50%;
  background: var(--indigo);
  color: #fff;
  font-size: 13px;
  font-weight: 600;
}
.user-name {
  font-size: 13.5px;
  font-weight: 500;
}
.user-card {
  display: flex;
  flex-direction: column;
  min-width: 200px;
  padding: 8px 16px 10px;
  border-bottom: 1px solid var(--line);
  margin-bottom: 4px;
}
.user-card small {
  color: var(--ink-faint);
  font-size: 12px;
}
.tabs {
  display: flex;
  gap: 6px;
  align-items: center;
  height: 40px;
  padding: 0 16px;
  border-bottom: 1px solid var(--line);
  background: var(--cloth);
  overflow-x: auto;
}
.tab {
  display: inline-flex;
  gap: 4px;
  align-items: center;
  height: 28px;
  padding: 0 10px;
  border: 1px solid var(--line);
  border-radius: var(--r-sm);
  color: var(--ink-soft);
  font-size: 12.5px;
  text-decoration: none;
  white-space: nowrap;
}
.tab:hover {
  color: var(--ink);
  border-color: var(--line-strong);
}
.tab.on {
  border-color: transparent;
  background: var(--indigo-wash);
  color: var(--indigo);
  font-weight: 500;
}
.tab-x {
  margin-right: -4px;
  border-radius: 50%;
  font-size: 11px;
}
.tab-x:hover {
  background: color-mix(in srgb, currentColor 16%, transparent);
}
.main {
  flex: 1;
  min-height: 0;
  padding: 20px 24px 28px;
  overflow-y: auto;
}
.fade-enter-active,
.fade-leave-active {
  transition: opacity 0.15s ease, transform 0.15s ease;
}
.fade-enter-from {
  opacity: 0;
  transform: translateY(4px);
}
.fade-leave-to {
  opacity: 0;
}
</style>
