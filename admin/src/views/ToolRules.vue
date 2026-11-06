<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Delete, Plus } from '@element-plus/icons-vue'
import { api } from '@/api'
import type { ToolRule } from '@/api/types'

/**
 * 工具规则：AI 调用工具前、拿到结果后，按公司规定拦下、必须确认、加提醒、给结果打码。
 * 只能收紧，不能放行任何本来要确认或被拦的操作；保存后员工端马上生效（下一轮任务起）。
 */
const items = ref<ToolRule[]>([])
const loading = ref(false)
const saving = ref(false)
const dirty = ref(false)

const actions = [
  { value: 'block', label: '拦下', hint: '直接拦下，AI 看到原因' },
  { value: 'confirm', label: '必须确认', hint: '每次都要员工确认，「以后自动允许」也跳不过' },
  { value: 'note', label: '提醒', hint: '照常执行，在结果前面加一句公司规定' },
  { value: 'none', label: '只打码', hint: '只给结果打码' },
]

async function load() {
  loading.value = true
  try {
    items.value = (await api.toolRules()).items
    dirty.value = false
  } finally {
    loading.value = false
  }
}
onMounted(load)

function add() {
  items.value.push({ name: '', enabled: true, tools: '*', match: '', action: 'confirm', message: '', redact: '' })
  dirty.value = true
}

async function remove(i: number) {
  await ElMessageBox.confirm(`删除规则「${items.value[i].name || '未命名'}」？`, '删除', { type: 'warning' })
  items.value.splice(i, 1)
  dirty.value = true
}

const problems = computed(() => {
  const out: string[] = []
  items.value.forEach((r, i) => {
    if (!r.name.trim()) out.push(`第 ${i + 1} 条：请填写规则名`)
    for (const [field, label] of [['match', '参数匹配'], ['redact', '结果打码']] as const) {
      if (r[field].trim()) {
        try {
          new RegExp(r[field])
        } catch {
          out.push(`第 ${i + 1} 条：${label}不是合法的正则表达式`)
        }
      }
    }
    if (r.action === 'none' && !r.redact.trim()) out.push(`第 ${i + 1} 条：「只打码」要填写打码的内容`)
  })
  return out
})

async function save() {
  if (problems.value.length) {
    ElMessage.error(problems.value[0])
    return
  }
  saving.value = true
  try {
    items.value = (await api.saveToolRules(items.value)).items
    dirty.value = false
    ElMessage.success('已保存，员工端下一轮任务起生效')
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <div v-loading="loading" class="page">
    <div class="head">
      <div>
        <h2>工具规则</h2>
        <p class="sub">
          AI 调用工具前、拿到结果后，按公司规定处理。工具名支持 <code>*</code> 通配，多个用逗号分开，例如 <code>mcp__erp__*</code>、<code>run_shell, write_file</code>；
          参数匹配和结果打码用正则表达式。规则只能收紧：不会让任何本来要确认或被拦的操作自动执行。
        </p>
      </div>
      <div class="actions">
        <el-button :icon="Plus" @click="add">新增规则</el-button>
        <el-button type="primary" :loading="saving" :disabled="!dirty" @click="save">保存</el-button>
      </div>
    </div>

    <el-empty v-if="!items.length && !loading" description="还没有规则" />

    <el-card v-for="(r, i) in items" :key="i" class="rule" shadow="never">
      <div class="row">
        <el-switch v-model="r.enabled" @change="dirty = true" />
        <el-input v-model="r.name" placeholder="规则名，例如 ERP 只读" maxlength="60" class="name" @input="dirty = true" />
        <el-select v-model="r.action" class="action" @change="dirty = true">
          <el-option v-for="a in actions" :key="a.value" :label="a.label" :value="a.value" />
        </el-select>
        <el-button :icon="Delete" text type="danger" @click="remove(i)" />
      </div>
      <el-form label-width="90px" size="small">
        <el-form-item label="工具">
          <el-input v-model="r.tools" placeholder="* 表示所有工具；例如 mcp__erp__*, run_shell" @input="dirty = true" />
        </el-form-item>
        <el-form-item label="参数匹配">
          <el-input v-model="r.match" placeholder="可选：参数里包含这些内容时才生效（正则），例如 delete|update" @input="dirty = true" />
        </el-form-item>
        <el-form-item v-if="r.action !== 'none'" :label="r.action === 'note' ? '提醒内容' : '原因'">
          <el-input v-model="r.message" maxlength="500" :placeholder="actions.find((a) => a.value === r.action)?.hint" @input="dirty = true" />
        </el-form-item>
        <el-form-item label="结果打码">
          <el-input v-model="r.redact" placeholder="可选：结果里匹配的内容换成 ***（正则），例如身份证号 \d{17}[\dXx]" @input="dirty = true" />
        </el-form-item>
      </el-form>
    </el-card>
  </div>
</template>

<style scoped>
.page {
  max-width: 980px;
}
.head {
  display: flex;
  gap: 16px;
  align-items: flex-start;
  justify-content: space-between;
  margin-bottom: 16px;
}
.head h2 {
  margin: 0 0 6px;
}
.sub {
  margin: 0;
  color: var(--el-text-color-secondary);
  line-height: 1.7;
}
.actions {
  display: flex;
  flex: none;
  gap: 8px;
}
.rule {
  margin-bottom: 12px;
}
.row {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 10px;
}
.name {
  flex: 1;
}
.action {
  width: 140px;
}
</style>
