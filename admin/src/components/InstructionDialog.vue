<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { ElMessage } from 'element-plus'
import { Delete } from '@element-plus/icons-vue'
import { api } from '@/api'
import type { InstructionTemplate } from '@/api/types'
import { auth } from '@/store/auth'

const props = defineProps<{ modelValue: boolean; deviceIds: number[]; deviceNames: string[] }>()
const emit = defineEmits<{ 'update:modelValue': [boolean]; dispatched: [] }>()

const visible = computed({
  get: () => props.modelValue,
  set: (v) => emit('update:modelValue', v),
})

const canManageTpl = computed(() => !!auth.user?.can_dispatch && !auth.user?.must_change_password)

const title = ref('')
const prompt = ref('')
const submitting = ref(false)

const templates = ref<InstructionTemplate[]>([])
const templatesLoaded = ref(false)

async function loadTemplates() {
  try {
    templates.value = await api.instructionTemplates()
    templatesLoaded.value = true
  } catch {
    /* 模板拉取失败不挡下发 */
  }
}
onMounted(loadTemplates)

watch(
  () => props.modelValue,
  (open) => {
    if (open) {
      title.value = ''
      prompt.value = ''
      if (!templatesLoaded.value) loadTemplates()
    }
  },
)

function useTemplate(t: InstructionTemplate) {
  prompt.value = t.prompt
  if (!title.value) title.value = t.name
}

// 保存当前内容为模板
const saveTplOpen = ref(false)
const tplName = ref('')
async function saveTemplate() {
  if (!tplName.value.trim() || !prompt.value.trim()) return
  const t = await api.createInstructionTemplate({ name: tplName.value.trim(), prompt: prompt.value.trim() })
  templates.value.unshift(t)
  saveTplOpen.value = false
  tplName.value = ''
  ElMessage.success('模板已保存')
}

async function removeTemplate(t: InstructionTemplate) {
  await api.deleteInstructionTemplate(t.id)
  templates.value = templates.value.filter((x) => x.id !== t.id)
  ElMessage.success('已删除模板')
}

const canSubmit = computed(() => props.deviceIds.length > 0 && prompt.value.trim().length > 0)

async function submit() {
  submitting.value = true
  try {
    const ins = await api.createInstruction({
      prompt: prompt.value.trim(),
      title: title.value.trim() || undefined,
      device_ids: props.deviceIds,
    })
    ElMessage.success(`已下发「${ins.title}」到 ${ins.total} 台电脑`)
    visible.value = false
    emit('dispatched')
  } finally {
    submitting.value = false
  }
}
</script>

<template>
  <el-dialog v-model="visible" title="下发指令给 AI 助手" width="600px" :close-on-click-modal="false">
    <el-alert
      :title="`将下发到 ${deviceIds.length} 台电脑：${deviceNames.slice(0, 6).join('、')}${deviceNames.length > 6 ? ` 等 ${deviceNames.length} 台` : ''}`"
      type="info"
      :closable="false"
      show-icon
      style="margin-bottom: 16px"
    />
    <p class="hint">
      用大白话描述要办的事，员工端的 AI 助手会像在对话里一样自己理解意图、调用工具去执行，完成后回报结果。
      IT 下发即授权，自动执行、员工无需确认；但危险命令仍会被安全策略拦下，每一步都会写入审计。
    </p>

    <el-form label-position="top">
      <el-form-item v-if="templates.length" label="从模板填充">
        <div class="tpls">
          <el-tag
            v-for="t in templates"
            :key="t.id"
            class="tpl"
            type="info"
            effect="plain"
            :closable="canManageTpl"
            @click="useTemplate(t)"
            @close="removeTemplate(t)"
          >
            {{ t.name }}
          </el-tag>
        </div>
      </el-form-item>

      <el-form-item label="标题（可选）">
        <el-input v-model="title" maxlength="60" show-word-limit placeholder="不填则自动取指令前几个字" />
      </el-form-item>

      <el-form-item label="指令内容">
        <el-input
          v-model="prompt"
          type="textarea"
          :rows="6"
          maxlength="4000"
          show-word-limit
          placeholder="例如：帮我清理一下 C 盘的临时文件和浏览器缓存，腾出点空间；再看看开机启动项里有没有可以关掉的。"
        />
      </el-form-item>
    </el-form>

    <template #footer>
      <el-button
        v-if="canManageTpl"
        :icon="Delete"
        link
        :disabled="!prompt.trim()"
        style="float: left"
        @click="saveTplOpen = true"
      >
        存为模板
      </el-button>
      <el-button @click="visible = false">取消</el-button>
      <el-button type="primary" :loading="submitting" :disabled="!canSubmit" @click="submit">下发</el-button>
    </template>

    <el-dialog v-model="saveTplOpen" title="存为模板" width="420px" append-to-body>
      <el-form label-position="top">
        <el-form-item label="模板名称">
          <el-input v-model="tplName" maxlength="40" placeholder="如：清理 C 盘空间" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="saveTplOpen = false">取消</el-button>
        <el-button type="primary" :disabled="!tplName.trim()" @click="saveTemplate">保存</el-button>
      </template>
    </el-dialog>
  </el-dialog>
</template>

<style scoped>
.hint {
  margin: 0 0 16px;
  color: var(--ink-faint);
  font-size: 12.5px;
  line-height: 1.6;
}
.tpls {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}
.tpl {
  cursor: pointer;
}
</style>
