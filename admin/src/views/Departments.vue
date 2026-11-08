<script setup lang="ts">
/**
 * 部门清单：设备的部门、技能 / 专家 / 连接器 / 插件的使用范围都从这里选。
 * 每个部门可以单独设置安全中心的项（比如研发部打开开发者工具），优先级：单台电脑 > 部门 > 全厂。
 */
import { computed, onMounted, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Plus, Refresh } from '@element-plus/icons-vue'
import { api } from '@/api'
import type { Department } from '@/api/types'
import { auth } from '@/store/auth'
import PolicyEditor from '@/components/PolicyEditor.vue'

const list = ref<Department[]>([])
const loading = ref(false)
const canEditSecurity = computed(() => !!auth.user?.is_owner && !auth.user?.must_change_password)

async function load() {
  loading.value = true
  try {
    list.value = await api.departments()
  } finally {
    loading.value = false
  }
}
onMounted(load)

async function add() {
  const { value } = await ElMessageBox.prompt('部门名称（和设备列表、使用范围里选的一致）', '新建部门', {
    inputPattern: /\S/,
    inputErrorMessage: '名称不能为空',
  })
  await api.createDepartment({ name: value.trim() })
  ElMessage.success('已添加')
  await load()
}

async function rename(d: Department) {
  const { value } = await ElMessageBox.prompt(
    '改名会同时改掉设备、插件和使用范围里的旧名字，这些电脑能用的东西不变。',
    `给「${d.name}」改名`,
    { inputValue: d.name, inputPattern: /\S/, inputErrorMessage: '名称不能为空' },
  )
  if (value.trim() === d.name) return
  await api.updateDepartment(d.id, { name: value.trim() })
  ElMessage.success('已改名')
  await load()
}

async function editNote(d: Department) {
  const { value } = await ElMessageBox.prompt('备注', d.name, { inputValue: d.note })
  await api.updateDepartment(d.id, { note: value ?? '' })
  await load()
}

async function remove(d: Department) {
  await ElMessageBox.confirm(`删除部门「${d.name}」？`, '删除部门', { type: 'warning' })
  // 还有电脑、插件或使用范围在用时服务端会拒绝，并说明在哪里用
  await api.deleteDepartment(d.id)
  ElMessage.success('已删除')
  await load()
}

// ---------- 部门安全设置 ----------
const policyOpen = ref(false)
const policyDept = ref<Department | null>(null)

function openPolicy(d: Department) {
  policyDept.value = d
  policyOpen.value = true
}

async function loadPolicy() {
  const id = policyDept.value!.id
  const [policy, shared] = await Promise.all([api.departmentPolicy(id), api.security()])
  return { policy, inherited: shared.effective }
}
</script>

<template>
  <div class="page">
    <div class="page-head">
      <div>
        <h1>部门</h1>
        <p>
          部门决定一台电脑能用哪些技能、专家、连接器（在各自页面的「使用范围」里选），也可以单独设置安全中心的项。
          电脑属于哪个部门在「设备列表」里改。
        </p>
      </div>
      <div class="toolbar">
        <el-button :icon="Refresh" :loading="loading" @click="load">刷新</el-button>
        <el-button type="primary" :icon="Plus" @click="add">新建部门</el-button>
      </div>
    </div>

    <div class="panel">
      <el-table v-loading="loading" :data="list" empty-text="还没有部门。设备列表里填过的部门会自动出现在这里">
        <el-table-column label="部门" prop="name" min-width="160" />
        <el-table-column label="电脑" width="90">
          <template #default="{ row }">{{ row.devices }} 台</template>
        </el-table-column>
        <el-table-column label="安全设置" width="150">
          <template #default="{ row }">
            <el-tag v-if="row.policy_items" type="warning" size="small">单独设置 {{ row.policy_items }} 项</el-tag>
            <span v-else class="muted">跟随全厂</span>
          </template>
        </el-table-column>
        <el-table-column label="备注" prop="note" min-width="160" show-overflow-tooltip />
        <el-table-column label="操作" width="300" fixed="right">
          <template #default="{ row }">
            <el-button link type="primary" @click="openPolicy(row as Department)">安全设置</el-button>
            <el-button link @click="rename(row as Department)">改名</el-button>
            <el-button link @click="editNote(row as Department)">备注</el-button>
            <el-button link type="danger" @click="remove(row as Department)">删除</el-button>
          </template>
        </el-table-column>
      </el-table>
    </div>

    <el-drawer v-model="policyOpen" :title="`安全设置 · ${policyDept?.name ?? ''}`" size="680px" destroy-on-close>
      <el-alert
        type="info"
        :closable="false"
        show-icon
        title="这里设置的项对这个部门所有电脑生效，覆盖全厂默认值；某台电脑在「设备列表 - 详情」里单独设置的项再覆盖部门。"
        style="margin-bottom: 12px"
      />
      <PolicyEditor
        v-if="policyDept"
        layer="这个部门"
        upper="全厂"
        :can-edit="canEditSecurity"
        :reload-key="policyDept.id"
        :load="loadPolicy"
        :save="(body) => api.setDepartmentPolicy(policyDept!.id, body)"
        :clear="() => api.clearDepartmentPolicy(policyDept!.id)"
        @saved="load"
      />
    </el-drawer>
  </div>
</template>

<style scoped>
.muted {
  color: var(--el-text-color-secondary);
  font-size: 12px;
}
</style>
