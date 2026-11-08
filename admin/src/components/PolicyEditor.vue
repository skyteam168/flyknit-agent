<script setup lang="ts">
/**
 * 某一层（部门 / 单台电脑）的安全设置：每一项可以「跟随上一层」，或者单独设成开 / 关（数值）、单独锁定 / 放开。
 * 只提交和上一层不一样的项——没动的项一直跟着上一层走，以后改全厂默认值时这里会自动跟着变。
 */
import { computed, onMounted, reactive, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { api } from '@/api'
import type { LayerPolicy, SecurityCatalogItem, SecurityEffectiveItem } from '@/api/types'

const props = defineProps<{
  /** 这一层叫什么：「这个部门」「这台电脑」 */
  layer: string
  /** 上一层叫什么：「全厂」「部门 / 全厂」 */
  upper: string
  canEdit: boolean
  /** 拉这一层的设置，以及不算这一层时（上一层）生效的值 */
  load: () => Promise<{ policy: LayerPolicy; inherited: Record<string, SecurityEffectiveItem> }>
  save: (body: { overrides: Record<string, boolean | number>; locks: Record<string, boolean>; note: string }) => Promise<unknown>
  clear: () => Promise<unknown>
  /** 换了对象（另一个部门 / 另一台电脑）时重新加载 */
  reloadKey?: string | number
}>()
const emit = defineEmits<{ saved: [] }>()

type Choice = 'follow' | 'on' | 'off' | 'number'
type LockChoice = 'follow' | 'lock' | 'unlock'

const catalog = ref<SecurityCatalogItem[]>([])
const inherited = ref<Record<string, SecurityEffectiveItem>>({})
const loading = ref(false)
const saving = ref(false)
const note = ref('')
const valueChoice = reactive<Record<string, Choice>>({})
const numberValue = reactive<Record<string, number>>({})
const lockChoice = reactive<Record<string, LockChoice>>({})
const filter = ref<'all' | 'changed'>('all')

async function reload() {
  loading.value = true
  try {
    const [cat, data] = await Promise.all([api.securityCatalog(), props.load()])
    catalog.value = cat
    inherited.value = data.inherited
    note.value = data.policy.note || ''
    for (const item of cat) {
      const v = data.policy.overrides[item.key]
      if (v === undefined) {
        valueChoice[item.key] = 'follow'
        numberValue[item.key] = Number(data.inherited[item.key]?.value ?? item.default)
      } else if (item.kind === 'bool') {
        valueChoice[item.key] = v ? 'on' : 'off'
      } else {
        valueChoice[item.key] = 'number'
        numberValue[item.key] = Number(v)
      }
      const l = data.policy.locks[item.key]
      lockChoice[item.key] = l === undefined ? 'follow' : l ? 'lock' : 'unlock'
    }
    filter.value = changedCount.value > 0 ? 'changed' : 'all'
  } finally {
    loading.value = false
  }
}
onMounted(reload)
watch(() => props.reloadKey, reload)

const changedCount = computed(
  () => catalog.value.filter((i) => valueChoice[i.key] !== 'follow' || lockChoice[i.key] !== 'follow').length,
)
const shown = computed(() =>
  filter.value === 'all'
    ? catalog.value
    : catalog.value.filter((i) => valueChoice[i.key] !== 'follow' || lockChoice[i.key] !== 'follow'),
)

function show(item: SecurityCatalogItem, v: boolean | number | undefined) {
  if (v === undefined) return '—'
  return item.kind === 'bool' ? (v ? '开' : '关') : String(v)
}

/** 这一层设完以后最终生效的值和锁 */
function final(item: SecurityCatalogItem): { value: boolean | number; locked: boolean } {
  const up = inherited.value[item.key]
  const c = valueChoice[item.key]
  const value = c === 'on' ? true : c === 'off' ? false : c === 'number' ? numberValue[item.key] : (up?.value ?? item.default)
  const l = lockChoice[item.key]
  const locked = l === 'lock' ? true : l === 'unlock' ? false : (up?.locked ?? item.locked_by_default)
  return { value, locked }
}

async function submit() {
  const overrides: Record<string, boolean | number> = {}
  const locks: Record<string, boolean> = {}
  for (const item of catalog.value) {
    const c = valueChoice[item.key]
    if (c === 'on') overrides[item.key] = true
    else if (c === 'off') overrides[item.key] = false
    else if (c === 'number') overrides[item.key] = numberValue[item.key]
    const l = lockChoice[item.key]
    if (l !== 'follow') locks[item.key] = l === 'lock'
  }
  saving.value = true
  try {
    await props.save({ overrides, locks, note: note.value })
    ElMessage.success('已保存。员工端几秒内生效')
    emit('saved')
    await reload()
  } finally {
    saving.value = false
  }
}

async function reset() {
  await ElMessageBox.confirm(`取消${props.layer}的全部单独设置，回到跟随${props.upper}？`, '恢复跟随', { type: 'warning' })
  saving.value = true
  try {
    await props.clear()
    ElMessage.success(`已恢复跟随${props.upper}`)
    emit('saved')
    await reload()
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <div v-loading="loading" class="policy">
    <div class="bar">
      <el-radio-group v-model="filter" size="small">
        <el-radio-button value="changed">单独设置的 {{ changedCount }} 项</el-radio-button>
        <el-radio-button value="all">全部</el-radio-button>
      </el-radio-group>
      <div class="actions">
        <el-button size="small" :disabled="!canEdit || changedCount === 0" @click="reset">恢复跟随{{ upper }}</el-button>
        <el-button size="small" type="primary" :loading="saving" :disabled="!canEdit" @click="submit">保存并下发</el-button>
      </div>
    </div>
    <p v-if="!canEdit" class="hint">只有超级管理员能改安全设置（放开一项就是给这些电脑放宽安全线）。</p>
    <el-empty v-if="!shown.length" :image-size="60" :description="`${layer}没有单独设置，全部跟随${upper}`" />
    <div v-for="item in shown" :key="item.key" class="row">
      <div class="info">
        <strong>{{ item.title }}</strong>
        <small>
          {{ upper }}：{{ show(item, inherited[item.key]?.value) }}{{ inherited[item.key]?.locked ? ' · 锁定' : ' · 员工可改' }}
          → 生效：<b>{{ show(item, final(item).value) }}{{ final(item).locked ? ' · 锁定' : ' · 员工可改' }}</b>
        </small>
      </div>
      <div class="ctrl">
        <el-select v-model="valueChoice[item.key]" size="small" style="width: 112px" :disabled="!canEdit">
          <el-option label="跟随" value="follow" />
          <template v-if="item.kind === 'bool'">
            <el-option label="开" value="on" />
            <el-option label="关" value="off" />
          </template>
          <el-option v-else label="单独设置" value="number" />
        </el-select>
        <el-input-number
          v-if="item.kind === 'int' && valueChoice[item.key] === 'number'"
          v-model="numberValue[item.key]"
          size="small"
          :min="inherited[item.key]?.min"
          :max="inherited[item.key]?.max"
          :disabled="!canEdit"
          style="width: 120px"
        />
        <el-select v-model="lockChoice[item.key]" size="small" style="width: 112px" :disabled="!canEdit">
          <el-option label="锁：跟随" value="follow" />
          <el-option label="锁定" value="lock" />
          <el-option label="员工可改" value="unlock" />
        </el-select>
      </div>
    </div>
    <el-input v-if="canEdit" v-model="note" size="small" maxlength="300" placeholder="备注（为什么单独设置，方便以后核对）" class="note" />
  </div>
</template>

<style scoped>
.bar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  margin-bottom: 8px;
}
.actions {
  display: flex;
  gap: 6px;
}
.hint {
  margin: 0 0 8px;
  color: var(--el-text-color-secondary);
  font-size: 12px;
}
.row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 8px 0;
  border-bottom: 1px solid var(--el-border-color-lighter);
}
.info {
  display: flex;
  flex-direction: column;
  min-width: 0;
}
.info small {
  color: var(--el-text-color-secondary);
  font-size: 12px;
}
.ctrl {
  display: flex;
  flex: none;
  gap: 6px;
}
.note {
  margin-top: 10px;
}
</style>
