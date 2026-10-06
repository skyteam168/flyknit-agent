<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { ElMessage } from 'element-plus'
import { Lock, Refresh } from '@element-plus/icons-vue'
import { api } from '@/api'
import type { SecurityCatalogItem, SecurityEffectiveItem } from '@/api/types'

const catalog = ref<SecurityCatalogItem[]>([])
const loading = ref(false)
const saving = ref(false)

// 每一项当前编辑中的值与锁状态
const values = reactive<Record<string, boolean | number>>({})
const locks = reactive<Record<string, boolean>>({})
const meta = reactive<Record<string, SecurityEffectiveItem>>({})

async function load() {
  loading.value = true
  try {
    const [cat, cur] = await Promise.all([api.securityCatalog(), api.security()])
    catalog.value = cat
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
onMounted(load)

const boolItems = computed(() => catalog.value.filter((i) => i.kind === 'bool'))
const intItems = computed(() => catalog.value.filter((i) => i.kind === 'int'))

async function save() {
  saving.value = true
  try {
    await api.setSecurity({ ...values }, { ...locks })
    ElMessage.success('已保存，员工端下次刷新配置（约 10 分钟内）生效')
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
        <el-button type="primary" :loading="saving" @click="save">保存并下发</el-button>
      </div>
    </div>

    <div v-loading="loading" class="panel">
      <el-alert
        type="info"
        :closable="false"
        show-icon
        title="这里改的是全厂默认值。需要给个别电脑单独放开某项，可在「设备列表 - 详情」里按机器设置（即将支持）。"
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
              @click="locks[i.key] = !locks[i.key]"
            >{{ locks[i.key] ? '已锁定' : '员工可改' }}</el-button>
          </el-tooltip>
          <el-switch v-model="(values[i.key] as boolean)" />
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
              @click="locks[i.key] = !locks[i.key]"
            >{{ locks[i.key] ? '已锁定' : '员工可改' }}</el-button>
          </el-tooltip>
          <el-input-number
            v-model="(values[i.key] as number)"
            :min="meta[i.key]?.min ?? 0"
            :max="meta[i.key]?.max ?? 100000"
            controls-position="right"
            style="width: 140px"
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
