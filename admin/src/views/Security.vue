<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { ElMessage } from 'element-plus'
import { Lock, Refresh } from '@element-plus/icons-vue'
import { api } from '@/api'
import type { SecurityCatalogItem, SecurityEffectiveItem } from '@/api/types'
import { auth } from '@/store/auth'

const catalog = ref<SecurityCatalogItem[]>([])
const loading = ref(false)
const saving = ref(false)

// 放宽一项就是全厂一起放宽，所以这页限超级管理员
const canEdit = computed(() => !!auth.user?.is_owner && !auth.user?.must_change_password)

// 每一项当前编辑中的值与锁状态
const values = reactive<Record<string, boolean | number>>({})
const locks = reactive<Record<string, boolean>>({})
const meta = reactive<Record<string, SecurityEffectiveItem>>({})

// 库里真正存着的那几项。没存的项跟随代码默认值——这个区别要保住：
// 一旦把「跟随默认」写成显式值，以后改默认值这台部署就不会跟着变了
const stored = reactive<{ values: Record<string, boolean | number>; locks: Record<string, boolean> }>({
  values: {},
  locks: {},
})
//: 管理员这次真正动过的项
const touched = reactive<{ values: Set<string>; locks: Set<string> }>({ values: new Set(), locks: new Set() })

async function load() {
  loading.value = true
  try {
    const [cat, cur] = await Promise.all([api.securityCatalog(), api.security()])
    catalog.value = cat
    stored.values = { ...cur.values }
    stored.locks = { ...cur.locks }
    touched.values = new Set()
    touched.locks = new Set()
    for (const item of cat) {
      const eff = cur.effective[item.key]
      values[item.key] = eff ? eff.value : item.default
      locks[item.key] = eff ? eff.locked : item.locked_by_default
      if (eff) meta[item.key] = eff
    }
  } finally {
    loading.value = false
  }
}

const dirty = computed(() => touched.values.size > 0 || touched.locks.size > 0)

function setValue(key: string, next: boolean | number) {
  values[key] = next
  touched.values.add(key)
}
function setLock(key: string, next: boolean) {
  locks[key] = next
  touched.locks.add(key)
}
onMounted(load)

const boolItems = computed(() => catalog.value.filter((i) => i.kind === 'bool'))
const intItems = computed(() => catalog.value.filter((i) => i.kind === 'int'))

async function save() {
  saving.value = true
  try {
    // 只提交动过的项，没动过的保持原样——「跟随默认」要一直是「跟随默认」
    const nextValues = { ...stored.values }
    const nextLocks = { ...stored.locks }
    for (const key of touched.values) nextValues[key] = values[key]
    for (const key of touched.locks) nextLocks[key] = locks[key]
    await api.setSecurity(nextValues, nextLocks)
    await load()
    ElMessage.success('已保存。员工端几秒内生效，不用等下次拉配置')
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <div class="page">
    <div class="page-head">
      <div>
        <h1>安全中心</h1>
        <p>统一管控每台电脑上 AI 能做什么。「锁定」的项员工端只读、不能自己改；开关决定默认开还是关。</p>
      </div>
      <div class="toolbar">
        <el-button :icon="Refresh" :loading="loading" @click="load">刷新</el-button>
        <el-button type="primary" :loading="saving" :disabled="!canEdit || !dirty" @click="save">保存并下发</el-button>
      </div>
    </div>

    <div v-loading="loading" class="panel">
      <el-alert
        v-if="!canEdit"
        type="info"
        :closable="false"
        show-icon
        title="只有超级管理员能改安全策略"
        description="放宽一项就是全厂所有电脑一起放宽，所以这页只能看。需要调整请找超级管理员。"
        style="margin-bottom: 16px"
      />
      <el-alert
        v-else
        type="info"
        :closable="false"
        show-icon
        title="这里改的是全厂默认值。需要给个别电脑单独放开某项，在「设备列表 - 详情」里按机器设置。"
        style="margin-bottom: 16px"
      />

      <h3 class="section">开关项</h3>
      <div v-for="i in boolItems" :key="i.key" class="row">
        <div class="info">
          <strong>{{ i.title }}</strong>
          <small v-if="i.risk">{{ i.risk }}</small>
        </div>
        <div class="ctrl">
          <el-tooltip content="锁定后员工端只读，不能自己改" placement="top">
            <el-button
              :type="locks[i.key] ? 'warning' : 'default'"
              :icon="Lock"
              size="small"
              plain
              :disabled="!canEdit"
              @click="setLock(i.key, !locks[i.key])"
            >{{ locks[i.key] ? '已锁定' : '员工可改' }}</el-button>
          </el-tooltip>
          <el-switch
            :model-value="(values[i.key] as boolean)"
            :disabled="!canEdit"
            @update:model-value="setValue(i.key, $event as boolean)"
          />
        </div>
      </div>

      <h3 class="section">数值项</h3>
      <div v-for="i in intItems" :key="i.key" class="row">
        <div class="info">
          <strong>{{ i.title }}</strong>
          <small v-if="i.risk">{{ i.risk }}</small>
        </div>
        <div class="ctrl">
          <el-tooltip content="锁定后员工端只读，不能自己改" placement="top">
            <el-button
              :type="locks[i.key] ? 'warning' : 'default'"
              :icon="Lock"
              size="small"
              plain
              :disabled="!canEdit"
              @click="setLock(i.key, !locks[i.key])"
            >{{ locks[i.key] ? '已锁定' : '员工可改' }}</el-button>
          </el-tooltip>
          <el-input-number
            :model-value="(values[i.key] as number)"
            :min="meta[i.key]?.min ?? 0"
            :max="meta[i.key]?.max ?? 100000"
            :disabled="!canEdit"
            controls-position="right"
            style="width: 140px"
            @update:model-value="setValue(i.key, ($event as number) ?? 0)"
          />
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.section {
  font-size: 13px;
  font-weight: 600;
  color: var(--ink-faint);
  margin: 18px 0 8px;
}
.section:first-of-type {
  margin-top: 0;
}
.row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  padding: 12px 0;
  border-bottom: 1px solid var(--line);
}
.row:last-child {
  border-bottom: none;
}
.info {
  display: flex;
  flex-direction: column;
  gap: 3px;
  min-width: 0;
}
.info strong {
  font-weight: 500;
}
.info small {
  color: var(--ink-faint);
  font-size: 12.5px;
  line-height: 1.5;
}
.ctrl {
  display: flex;
  align-items: center;
  gap: 12px;
  flex: none;
}
</style>
