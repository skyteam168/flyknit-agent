<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { ElMessage } from 'element-plus'
import { api } from '@/api'

/**
 * 电脑操作：允许 AI 操作的 Windows 程序（命令策略 desktop_apps）。
 * 安全中心「电脑操作」打开后才生效；名单为空 = 一个程序都不能操作。
 * 命令行、系统工具、资源管理器、浏览器、远程桌面、FlyknitBuddy 自己写进来也不生效（员工端写死拦下）。
 */
const props = defineProps<{ canEdit: boolean }>()

const apps = ref<string[]>([])
const input = ref('')
const loading = ref(false)
const saving = ref(false)
const dirty = ref(false)

const NEVER = new Set([
  'cmd', 'powershell', 'pwsh', 'conhost', 'windowsterminal', 'wt', 'regedit', 'mmc', 'taskmgr', 'msconfig', 'control', 'explorer',
  'msedge', 'chrome', 'firefox', 'iexplore', 'mstsc', 'msiexec', 'flyknitbuddy', 'flyknit.client', 'flyknit',
])

function normalize(raw: string): string {
  let name = raw.trim().replace(/\//g, '\\').split('\\').pop()!.trim().toLowerCase()
  if (name && !name.endsWith('.exe')) name += '.exe'
  return name === '.exe' ? '' : name
}

const neverHint = computed(() => apps.value.filter((a) => NEVER.has(a.replace(/\.exe$/, ''))))

async function load() {
  loading.value = true
  try {
    const policy = await api.policy()
    apps.value = [...((policy.desktop_apps as string[] | undefined) ?? [])]
    dirty.value = false
  } finally {
    loading.value = false
  }
}
onMounted(load)

function add() {
  for (const part of input.value.split(/[,，;；\s]+/)) {
    const name = normalize(part)
    if (name && !apps.value.includes(name)) apps.value.push(name)
  }
  input.value = ''
  dirty.value = true
}

function remove(name: string) {
  apps.value = apps.value.filter((a) => a !== name)
  dirty.value = true
}

async function save() {
  saving.value = true
  try {
    // 只改 desktop_apps 这一项，其余策略原样带回去
    const policy = await api.policy()
    policy.desktop_apps = apps.value
    await api.savePolicy(policy)
    ElMessage.success('已保存，员工端下一轮任务起生效')
    await load()
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <div v-loading="loading" class="desktop-apps">
    <div class="head">
      <div>
        <h3>电脑操作：允许 AI 操作的程序</h3>
        <p>
          安全中心打开「电脑操作」后，AI 只能操作这里列出的 Windows 程序（写进程名，如 <code>erp.exe</code>，在任务管理器「详细信息」里能看到）。
          名单为空时一个程序都不能操作。命令行、系统管理工具、资源管理器、浏览器、远程桌面和 FlyknitBuddy 自己写进来也不会生效。
        </p>
      </div>
      <el-button type="primary" :loading="saving" :disabled="!props.canEdit || !dirty" @click="save">保存并下发</el-button>
    </div>
    <div class="tags">
      <el-tag v-for="a in apps" :key="a" :closable="props.canEdit" @close="remove(a)">{{ a }}</el-tag>
      <span v-if="!apps.length" class="empty">还没有添加程序</span>
    </div>
    <el-alert
      v-if="neverHint.length"
      type="warning"
      :closable="false"
      show-icon
      :title="`${neverHint.join('、')} 属于永远不能操作的程序，写进来也不会生效`"
      style="margin-top: 10px"
    />
    <div v-if="props.canEdit" class="add">
      <el-input v-model="input" placeholder="输入进程名，多个用逗号隔开，如 erp.exe, wms.exe" @keyup.enter="add" />
      <el-button :disabled="!input.trim()" @click="add">添加</el-button>
    </div>
  </div>
</template>

<style scoped>
.desktop-apps {
  margin-top: 24px;
  padding: 16px 18px;
  border: 1px solid var(--el-border-color-lighter);
  border-radius: 10px;
}
.head {
  display: flex;
  justify-content: space-between;
  gap: 16px;
  align-items: flex-start;
}
h3 {
  margin: 0 0 6px;
  font-size: 15px;
}
p {
  margin: 0;
  color: var(--el-text-color-secondary);
  font-size: 13px;
  line-height: 1.6;
}
.tags {
  margin-top: 12px;
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}
.empty {
  color: var(--el-text-color-placeholder);
  font-size: 13px;
}
.add {
  margin-top: 12px;
  display: flex;
  gap: 8px;
  max-width: 560px;
}
</style>
