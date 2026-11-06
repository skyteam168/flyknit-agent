<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Delete, Plus } from '@element-plus/icons-vue'
import { api } from '@/api'
import type { SlashCommand } from '@/api/types'

/**
 * 快捷指令：员工在输入框里打 / 就能选，例如「/周报 2026-11-03」展开成一整句要求发给 AI。
 * 模板里写 {参数名}，员工在指令后面按顺序填；最后一个参数拿走剩下的全部文字。
 * 只是一段文字，不带任何权限：展开后照常作为员工的话发给 AI，该确认的操作照样确认。
 */
const items = ref<SlashCommand[]>([])
const loading = ref(false)
const saving = ref(false)
const dirty = ref(false)

async function load() {
  loading.value = true
  try {
    items.value = (await api.commands()).items
    dirty.value = false
  } finally {
    loading.value = false
  }
}
onMounted(load)

function add() {
  items.value.push({ name: '', title: '', description: '', template: '' })
  dirty.value = true
}

async function remove(i: number) {
  await ElMessageBox.confirm(`删除指令 /${items.value[i].name || '（未命名）'}？`, '删除', { type: 'warning' })
  items.value.splice(i, 1)
  dirty.value = true
}

const params = (tpl: string) => [...new Set([...tpl.matchAll(/\{([^{}\s]{1,20})\}/g)].map((m) => m[1]))]

const problems = computed(() => {
  const out: string[] = []
  const seen = new Set<string>()
  items.value.forEach((c, i) => {
    const name = c.name.trim().replace(/^\//, '')
    if (!/^[\w\-一-鿿]{1,20}$/.test(name)) out.push(`第 ${i + 1} 条：指令名只能用中文、字母、数字、- 和 _，不超过 20 个字，不能有空格`)
    if (seen.has(name.toLowerCase())) out.push(`第 ${i + 1} 条：指令名 /${name} 重复`)
    seen.add(name.toLowerCase())
    if (!c.template.trim()) out.push(`第 ${i + 1} 条：模板不能为空`)
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
    items.value = (await api.saveCommands(items.value.map((c) => ({ ...c, name: c.name.trim().replace(/^\//, '') })))).items
    dirty.value = false
    ElMessage.success('已发布，员工端马上生效')
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <div v-loading="loading" class="page">
    <div class="head">
      <div>
        <h2>快捷指令</h2>
        <p class="sub">
          员工在输入框里打 <code>/</code> 就能选。模板里写 <code>{参数}</code>，员工在指令后面按顺序填，例如 <code>/周报 2026-11-03</code>；最后一个参数拿走剩下的全部文字。
          指令只是一段文字，不带任何权限，展开后照常作为员工的话发给 AI。员工也可以在客户端里加自己的指令（只在本机）。
        </p>
      </div>
      <div class="actions">
        <el-button :icon="Plus" @click="add">新增指令</el-button>
        <el-button type="primary" :loading="saving" :disabled="!dirty" @click="save">发布</el-button>
      </div>
    </div>

    <el-empty v-if="!items.length && !loading" description="还没有快捷指令，点「新增指令」加一条" />

    <el-card v-for="(c, i) in items" :key="i" class="cmd" shadow="never">
      <div class="row">
        <el-input v-model="c.name" placeholder="指令名，例如 周报" class="name" @input="dirty = true">
          <template #prepend>/</template>
        </el-input>
        <el-input v-model="c.title" placeholder="一句话说明（选择列表里显示）" maxlength="60" @input="dirty = true" />
        <el-button :icon="Delete" text type="danger" @click="remove(i)" />
      </div>
      <el-input
        v-model="c.template"
        type="textarea"
        :autosize="{ minRows: 3, maxRows: 10 }"
        maxlength="4000"
        show-word-limit
        placeholder="发给 AI 的要求，可以用 {参数}，例如：帮我汇总 {日期} 这一周 {车间} 的产量和不良率，做成表格并分析原因"
        @input="dirty = true"
      />
      <div class="meta">
        <span v-if="params(c.template).length">参数：<el-tag v-for="p in params(c.template)" :key="p" size="small" class="tag">{{ p }}</el-tag></span>
        <span v-else class="muted">没有参数：选中后直接发送模板内容</span>
      </div>
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
.cmd {
  margin-bottom: 12px;
}
.row {
  display: flex;
  gap: 8px;
  margin-bottom: 8px;
}
.name {
  flex: none;
  width: 220px;
}
.meta {
  margin-top: 6px;
  font-size: 12px;
}
.tag {
  margin-right: 4px;
}
.muted {
  color: var(--el-text-color-secondary);
}
</style>
