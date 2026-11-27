<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Delete, Plus } from '@element-plus/icons-vue'
import { api } from '@/api'
import type { Device, EvalCase, EvalRunDetail, EvalRunSummary, EvalSuite, EvalSuiteIn, Expert } from '@/api/types'
import { dateTime } from '@/utils/format'

/**
 * 回归评测（docs/evals.md）：一组固定的问题，派到执行机上用某位专家（或普通办事助手）回答，按检查项和评分标准判分。
 * 改了专家、技能、模型、提示词之后跑一遍，和上一次比，看哪些题变差了。一律只预演，不会真的改任何东西。
 */
const loading = ref(false)
const saving = ref(false)
const suites = ref<EvalSuite[]>([])
const devices = ref<Device[]>([])
const experts = ref<Expert[]>([])
const dialog = ref(false)
const editing = ref<number | null>(null)
const drawer = ref(false)
const drawerSuite = ref<EvalSuite | null>(null)
const runs = ref<EvalRunSummary[]>([])
const detail = ref<EvalRunDetail | null>(null)

const blankCase = (): EvalCase => ({ id: '', prompt: '', must_include: [], must_not_include: [], regex: '', rubric: '' })
const blank = (): EvalSuiteIn => ({ name: '', description: '', expert_id: '', device_id: null, permission: 'readonly', pass_score: 60, cases: [blankCase()] })
const form = ref<EvalSuiteIn>(blank())

async function load() {
  loading.value = true
  try {
    const [r, d, e] = await Promise.all([api.evals(), api.devices(), api.experts()])
    suites.value = r.items
    devices.value = d
    experts.value = e
  } finally {
    loading.value = false
  }
}
onMounted(load)

// 有在跑的：每 10 秒刷新一次
const timer = window.setInterval(async () => {
  if (suites.value.some((s) => s.last_run?.status === 'running')) {
    suites.value = (await api.evals()).items
    if (drawer.value && drawerSuite.value) await openRuns(drawerSuite.value, detail.value?.id)
  }
}, 10000)
onUnmounted(() => window.clearInterval(timer))

function openCreate() {
  editing.value = null
  form.value = blank()
  dialog.value = true
}

function openEdit(s: EvalSuite) {
  editing.value = s.id
  form.value = {
    name: s.name, description: s.description, expert_id: s.expert_id, device_id: s.device_id, permission: s.permission,
    pass_score: s.pass_score, cases: s.cases.map((c) => ({ ...c, must_include: [...c.must_include], must_not_include: [...c.must_not_include] })),
  }
  dialog.value = true
}

const problems = computed(() => {
  const out: string[] = []
  if (!form.value.name.trim()) out.push('请填写题集名称')
  if (!form.value.cases.length) out.push('至少要有一道题')
  form.value.cases.forEach((c, i) => {
    if (!c.prompt.trim()) out.push(`第 ${i + 1} 题没有填问题`)
    else if (!c.must_include.length && !c.must_not_include.length && !c.regex.trim() && !c.rubric.trim()) out.push(`第 ${i + 1} 题至少填一种判分方式`)
    if (c.regex.trim()) {
      try {
        new RegExp(c.regex)
      } catch {
        out.push(`第 ${i + 1} 题的正则不对`)
      }
    }
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
    if (editing.value === null) await api.createEval(form.value)
    else await api.updateEval(editing.value, form.value)
    ElMessage.success('已保存')
    dialog.value = false
    await load()
  } finally {
    saving.value = false
  }
}

async function run(s: EvalSuite) {
  const { value } = await ElMessageBox.prompt(
    `把 ${s.cases.length} 道题派给「${s.device_name || '执行机'}」，用${s.expert_name ? `专家「${s.expert_name}」` : '普通办事助手'}回答（只预演，不会改任何东西）。可以写一句这次改了什么，方便以后对比：`,
    '跑一次评测', { inputPlaceholder: '例如：专家说明改到 v3、换了模型', confirmButtonText: '开始', cancelButtonText: '取消' },
  ).catch(() => ({ value: null as string | null }))
  if (value === null) return
  await api.runEval(s.id, value ?? '')
  ElMessage.success('已派出，执行机做完会自动判分')
  await load()
}

async function remove(s: EvalSuite) {
  await ElMessageBox.confirm(`删除题集「${s.name}」？历次评测记录会一起删掉。`, '删除题集', { type: 'warning' })
  await api.deleteEval(s.id)
  await load()
}

async function openRuns(s: EvalSuite, keep?: number) {
  drawerSuite.value = s
  runs.value = (await api.evalRuns(s.id)).items
  const id = keep ?? runs.value[0]?.id
  detail.value = id ? await api.evalRun(id) : null
  drawer.value = true
}

async function pick(r: EvalRunSummary) {
  detail.value = await api.evalRun(r.id)
}

const deviceLabel = (d: Device) => `${d.owner || d.user_name || ''} · ${d.machine_name}${d.department ? '（' + d.department + '）' : ''}`
const runText = (r: EvalRunSummary | null) =>
  !r ? '还没跑过' : r.status === 'running' ? '正在跑…' : `${r.passed} / ${r.total} 通过 · ${r.score ?? 0} 分`
const scoreType = (r: EvalRunSummary | null) =>
  !r || r.status === 'running' ? 'info' : r.passed === r.total ? 'success' : r.regressions.length ? 'danger' : 'warning'
const isRegression = (caseId: string) => !!detail.value?.regressions.includes(caseId)
</script>

<template>
  <div v-loading="loading" class="page">
    <div class="head">
      <div>
        <h2>回归评测</h2>
        <p class="sub">
          一组固定的问题，派到一台执行机上用某位专家（或普通办事助手）回答，按每题的检查项（必须包含 / 不能包含 / 正则）和评分标准（模型打分）判分。
          改了专家、技能、模型或提示词之后跑一遍，和上一次比，<b>上次通过、这次没过</b>的题会标成退步。
          评测<b>一律只预演</b>：只读调研、列出会做的修改，不会真的改任何东西；需要确认的操作一律拒绝。
        </p>
      </div>
      <el-button type="primary" :icon="Plus" @click="openCreate">新建题集</el-button>
    </div>

    <el-table :data="suites" empty-text="还没有题集">
      <el-table-column label="题集" min-width="200">
        <template #default="{ row }">
          <strong>{{ row.name }}</strong>
          <div class="muted">{{ row.cases.length }} 道题 · {{ row.expert_name ? '专家：' + row.expert_name : '普通办事助手' }}</div>
        </template>
      </el-table-column>
      <el-table-column label="执行机" min-width="150">
        <template #default="{ row }">
          {{ row.device_name || '（没选）' }}
          <el-tag v-if="row.device_id" :type="row.device_online ? 'success' : 'info'" size="small" class="tag">{{ row.device_online ? '在线' : '不在线' }}</el-tag>
        </template>
      </el-table-column>
      <el-table-column label="上一次" min-width="200">
        <template #default="{ row }">
          <el-tag :type="scoreType(row.last_run)" size="small">{{ runText(row.last_run) }}</el-tag>
          <el-tag v-if="row.last_run?.regressions.length" type="danger" size="small" class="tag">退步 {{ row.last_run.regressions.length }} 题</el-tag>
          <div v-if="row.last_run" class="muted">{{ dateTime(row.last_run.created_at) }}<span v-if="row.last_run.note"> · {{ row.last_run.note }}</span></div>
        </template>
      </el-table-column>
      <el-table-column label="" width="230">
        <template #default="{ row }">
          <el-button link type="primary" :disabled="!row.device_id || row.last_run?.status === 'running'" @click="run(row as EvalSuite)">跑一次</el-button>
          <el-button link :disabled="!row.last_run" @click="openRuns(row as EvalSuite)">结果</el-button>
          <el-button link @click="openEdit(row as EvalSuite)">编辑</el-button>
          <el-button link type="danger" @click="remove(row as EvalSuite)">删除</el-button>
        </template>
      </el-table-column>
    </el-table>

    <el-dialog v-model="dialog" :title="editing === null ? '新建题集' : '编辑题集'" width="820px" top="5vh">
      <el-form label-width="100px">
        <el-form-item label="名称"><el-input v-model="form.name" maxlength="100" placeholder="MES 专家 · 周报类问题" /></el-form-item>
        <el-form-item label="说明"><el-input v-model="form.description" maxlength="500" placeholder="可选：这组题测什么" /></el-form-item>
        <el-form-item label="谁来回答">
          <el-select v-model="form.expert_id" filterable placeholder="普通办事助手" clearable class="wide">
            <el-option value="" label="普通办事助手（不指定专家）" />
            <el-option v-for="e in experts" :key="e.id" :value="e.id" :label="e.name" />
          </el-select>
          <small class="muted">专家要下发到执行机、执行机在它的使用范围里，否则这一题会失败并写明原因。</small>
        </el-form-item>
        <el-form-item label="执行机">
          <el-select v-model="form.device_id" filterable placeholder="选一台常开的专用电脑" class="wide">
            <el-option v-for="d in devices" :key="d.id" :value="d.id" :label="deviceLabel(d)" />
          </el-select>
          <small class="muted">建议和无人值守任务用同一台执行机，连接器、技能都装好登录好。</small>
        </el-form-item>
        <el-form-item label="权限上限">
          <el-radio-group v-model="form.permission">
            <el-radio-button value="readonly">仅可查看</el-radio-button>
            <el-radio-button value="workspace">工作区内修改</el-radio-button>
          </el-radio-group>
          <small class="muted gap-left">反正只预演；选「工作区内修改」能看到它打算怎么改</small>
        </el-form-item>
        <el-form-item label="及格分">
          <el-input-number v-model="form.pass_score" :min="0" :max="100" /> <small class="muted gap-left">有评分标准的题，模型打分不低于这个才算通过</small>
        </el-form-item>

        <h4>题目</h4>
        <el-card v-for="(c, i) in form.cases" :key="i" class="case" shadow="never">
          <div class="case-head">
            <strong>第 {{ i + 1 }} 题</strong>
            <el-input v-model="c.id" size="small" placeholder="编号（可选，用来和上次对比）" maxlength="40" class="case-id" />
            <el-button :icon="Delete" text type="danger" :disabled="form.cases.length === 1" @click="form.cases.splice(i, 1)" />
          </div>
          <el-input v-model="c.prompt" type="textarea" :rows="2" maxlength="4000" placeholder="问题：像员工平时那样问，例如「上周一车间的产量是多少？」" />
          <div class="grid">
            <el-select v-model="c.must_include" multiple filterable allow-create default-first-option placeholder="回答必须包含（回车添加）" />
            <el-select v-model="c.must_not_include" multiple filterable allow-create default-first-option placeholder="回答不能包含（回车添加）" />
          </div>
          <el-input v-model="c.regex" size="small" placeholder="可选：回答要匹配的正则，例如 \|\s*车间\s*\|（要有车间表格）" class="gap" />
          <el-input v-model="c.rubric" type="textarea" :rows="2" maxlength="2000" class="gap"
            placeholder="可选：评分标准，由模型按它打 0-100 分。例如「要提到产量、达成率、不良率三项，不能编造数字」" />
        </el-card>
        <el-button :icon="Plus" @click="form.cases.push(blankCase())">加一道题</el-button>
      </el-form>
      <template #footer>
        <el-button @click="dialog = false">取消</el-button>
        <el-button type="primary" :loading="saving" @click="save">保存</el-button>
      </template>
    </el-dialog>

    <el-drawer v-model="drawer" :title="drawerSuite ? `评测结果 · ${drawerSuite.name}` : '评测结果'" size="70%">
      <div class="runs">
        <el-table :data="runs" size="small" highlight-current-row class="run-list" @row-click="(r: EvalRunSummary) => pick(r)">
          <el-table-column label="时间" width="150"><template #default="{ row }">{{ dateTime(row.created_at) }}</template></el-table-column>
          <el-table-column label="结果" min-width="150">
            <template #default="{ row }">
              <el-tag :type="scoreType(row as EvalRunSummary)" size="small">{{ runText(row as EvalRunSummary) }}</el-tag>
              <el-tag v-if="row.regressions.length" type="danger" size="small" class="tag">退步 {{ row.regressions.length }}</el-tag>
              <div v-if="row.note" class="muted">{{ row.note }}</div>
            </template>
          </el-table-column>
        </el-table>
        <div v-if="detail" class="detail">
          <el-card v-for="r in detail.results" :key="r.id" class="result" :class="{ regression: isRegression(r.case_id) }" shadow="never">
            <div class="result-head">
              <el-tag v-if="r.status !== 'done'" size="small" type="info">{{ r.status === 'judging' ? '打分中' : '等执行机' }}</el-tag>
              <el-tag v-else :type="r.passed ? 'success' : 'danger'" size="small">{{ r.passed ? '通过' : '没通过' }} · {{ r.score }} 分</el-tag>
              <el-tag v-if="isRegression(r.case_id)" type="danger" effect="dark" size="small">比上次退步</el-tag>
              <strong class="prompt">{{ r.prompt }}</strong>
            </div>
            <div class="checks">
              <el-tag v-for="c in r.checks" :key="c.name" :type="c.ok ? 'success' : 'danger'" size="small" effect="plain">
                {{ c.ok ? '✓' : '✗' }} {{ c.name }}<span v-if="c.detail">：{{ c.detail }}</span>
              </el-tag>
              <el-tag v-if="r.judge_score !== null" size="small" effect="plain">模型打分 {{ r.judge_score }}</el-tag>
            </div>
            <p v-if="r.judge_reason" class="muted">{{ r.judge_reason }}</p>
            <details v-if="r.answer || r.error">
              <summary class="muted">看回答</summary>
              <pre class="answer">{{ r.answer || r.error }}</pre>
            </details>
          </el-card>
        </div>
      </div>
    </el-drawer>
  </div>
</template>

<style scoped>
.page {
  display: flex;
  flex-direction: column;
  gap: 12px;
}
.head {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 16px;
}
.head h2 {
  margin: 0 0 4px;
}
.sub,
.muted {
  margin: 0;
  color: var(--el-text-color-secondary);
  font-size: 12px;
  line-height: 1.6;
}
.tag {
  margin-left: 6px;
}
.wide {
  width: 100%;
}
.gap {
  margin-top: 8px;
}
.gap-left {
  margin-left: 8px;
}
.case {
  margin-bottom: 10px;
}
.case-head {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 8px;
}
.case-id {
  width: 220px;
}
.grid {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 8px;
  margin-top: 8px;
}
.runs {
  display: grid;
  grid-template-columns: 320px 1fr;
  gap: 16px;
  align-items: start;
}
.run-list :deep(tr) {
  cursor: pointer;
}
.result {
  margin-bottom: 10px;
}
.result.regression {
  border-color: var(--el-color-danger);
}
.result-head {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px;
}
.prompt {
  margin-left: 4px;
}
.checks {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  margin-top: 8px;
}
.answer {
  max-height: 320px;
  overflow: auto;
  padding: 8px;
  background: var(--el-fill-color-light);
  white-space: pre-wrap;
  font-size: 12px;
}
</style>
