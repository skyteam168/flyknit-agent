"""
数据脱敏（docs/data-protection.md）：发给外部模型之前，把身份证号、银行卡号、手机号、邮箱换成占位符，
模型的回答（正文、思考、工具调用参数）回来时再换回原值；密码、密钥直接隐藏，不还原。

为什么放在服务端网关：员工电脑上所有模型调用（对话、办事、专家、子代理、团队、翻译、压缩、标题）都必须经过
/api/v1/chat/completions，员工端绕不过去；模型 Key 也只在这里。这里是唯一的出网口。

效果：
- 百炼等上游只见过 [身份证_1] 这样的占位符，见不到原值；
- 员工看到的回答、AI 写出来的文件里是真实值（回来时还原，工具参数也还原）；
- 同一次请求里同一个号码对应同一个占位符，模型照样能判断「这两行是同一个人」；
  请求之间不需要保存对照表：员工端每次发的都是带原值的完整历史，按出现顺序编号，前缀不变时编号也不变（提示词缓存照样命中）。

识别只认「有固定格式、能校验」的数据，压低误判：
- 身份证：18 位，出生日期合法，且最后一位校验码算得对（18 位的订单号、批次号基本对不上校验码）；
- 银行卡：16～19 位（允许空格、横线分组），Luhn 校验通过；
- 手机号：中国大陆 11 位手机号（可带 +86）；邮箱：常见格式；
- 密码 / 口令 / 密钥 / 令牌 / 私钥：只认「值」（password=xxx、sk-xxx、Bearer xxx……），讲做法的句子不动。
姓名、住址、工资金额这类自由文本规则抓不全，不在这里处理（见文档：敏感部门走内网模型）。
"""

from __future__ import annotations

import json
import re
from collections import Counter
from dataclasses import dataclass, field
from typing import Any, Callable

#: 占位符里的类型名（模型看得懂、员工看得懂）
LABELS = {"id_card": "身份证", "bank_card": "银行卡", "phone": "手机号", "email": "邮箱"}
KIND_OF_LABEL = {v: k for k, v in LABELS.items()}
SECRET_LABEL = "secret"
#: 还原时认的写法：[身份证_1]，模型偶尔改成全角括号也认
PLACEHOLDER = re.compile(r"[\[【](身份证|银行卡|手机号|邮箱)_(\d{1,4})[\]】]")
#: 流式输出里，可能是占位符开头、还没收完的那一截最长多少字
_MAX_PLACEHOLDER = 12

_EMAIL = re.compile(r"(?<![A-Za-z0-9._%+\-])[A-Za-z0-9._%+\-]{1,64}@[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,24}(?![A-Za-z0-9\-])")
_ID_CARD = re.compile(r"(?<![0-9A-Za-z])\d{17}[\dXx](?![0-9A-Za-z])")
_BANK = re.compile(r"(?<![0-9A-Za-z.\-])\d(?:[ \-]?\d){15,18}(?![0-9A-Za-z])")
_PHONE = re.compile(r"(?<![0-9A-Za-z])(?:\+?86[ \-]?)?1[3-9]\d(?:[ \-]?\d{4}){2}(?![0-9A-Za-z])")

#: 密码、密钥：(前缀)(值)，只换值；私钥整段换
_SECRETS: list[re.Pattern[str]] = [
    # 值只认 ASCII 可见字符：中文标点、中文字不算在密码里（「密码是Abc12345。订单号……」只隐藏 Abc12345）
    re.compile(r"((?:密码|口令|password|passwd|pwd)\s*(?:是|为|[:：=])\s*[\"'“]?)([!#-&(-+\-./0-9:<-~]{4,})", re.I),
    re.compile(r"((?:api[_\- ]?key|secret|access[_\- ]?key|client[_\- ]?secret|token|密钥)\s*(?:是|为|[:：=])\s*[\"'“]?)([A-Za-z0-9_\-./+=]{16,})", re.I),
    re.compile(r"(\bBearer\s+)([A-Za-z0-9\-._~+/]{20,}=*)"),
    re.compile(r"()(\b(?:sk|pk|rk)-[A-Za-z0-9_\-]{16,}|\bgh[pousr]_[A-Za-z0-9]{20,}|\bAKIA[0-9A-Z]{16}\b|\bxox[abpr]-[A-Za-z0-9\-]{10,}"
               r"|\bAIza[0-9A-Za-z_\-]{30,}|\beyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{5,})"),
    re.compile(r"()(-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----)"),
]
SECRET_MASK = "〔已隐藏〕"

NOTE = (
    "【数据保护】为保护员工和客户的个人信息，本次对话里的身份证号、银行卡号、手机号、邮箱已经换成占位符，"
    "形如 [身份证_1]、[银行卡_2]、[手机号_3]、[邮箱_4]。需要用到时原样写出占位符——写文件、填表、回复、调用工具都一样，"
    "系统会在员工电脑上自动换回真实值；不要猜测、拼接、截取或改写占位符，也不要因为看不到原值就拒绝任务。"
    "写成〔已隐藏〕的是密码、密钥，已删除，无法还原，需要时请员工自己填写。"
)


# ---------- 校验 ----------

_ID_WEIGHTS = (7, 9, 10, 5, 8, 4, 2, 1, 6, 3, 7, 9, 10, 5, 8, 4, 2)
_ID_CODES = "10X98765432"


def valid_id_card(s: str) -> bool:
    if len(s) != 18 or not s[:17].isdigit():
        return False
    year, month, day = int(s[6:10]), int(s[10:12]), int(s[12:14])
    if not (1900 <= year <= 2099 and 1 <= month <= 12 and 1 <= day <= 31):
        return False
    total = sum(int(c) * w for c, w in zip(s[:17], _ID_WEIGHTS))
    return _ID_CODES[total % 11] == s[17].upper()


def valid_luhn(digits: str) -> bool:
    total = 0
    for i, c in enumerate(reversed(digits)):
        d = int(c)
        if i % 2 == 1:
            d *= 2
            if d > 9:
                d -= 9
        total += d
    return total % 10 == 0


# ---------- 设置 ----------


@dataclass(frozen=True)
class Policy:
    enabled: bool = False
    #: 手机号、邮箱也换（业务邮件、供应商电话多的部门可以关）
    contacts: bool = True
    #: 有身份证、银行卡、手机号、邮箱时直接拦下不发，而不是换成占位符
    block: bool = False

    @classmethod
    def from_security(cls, values: dict[str, Any]) -> "Policy":
        def v(key: str, default: bool) -> bool:
            item = values.get(key)
            value = item.get("value") if isinstance(item, dict) else item
            return bool(value) if value is not None else default

        return cls(enabled=v("dlp_enabled", False), contacts=v("dlp_contacts", True), block=v("dlp_block", False))


# ---------- 替换 ----------


@dataclass
class Vault:
    """一次请求里的对照表：原值 ↔ 占位符。只活在这次请求里，不落库、不写日志。"""

    by_value: dict[tuple[str, str], str] = field(default_factory=dict)
    by_token: dict[str, str] = field(default_factory=dict)
    counts: Counter = field(default_factory=Counter)

    def token(self, kind: str, value: str) -> str:
        key = (kind, value)
        if key not in self.by_value:
            n = sum(1 for k in self.by_value if k[0] == kind) + 1
            label = LABELS[kind]
            self.by_value[key] = f"[{label}_{n}]"
            self.by_token[f"{label}_{n}"] = value
        self.counts[kind] += 1
        return self.by_value[key]

    @property
    def empty(self) -> bool:
        return not self.by_token

    @property
    def total(self) -> int:
        return sum(self.counts.values())


def mask_text(text: str, vault: Vault, policy: Policy) -> str:
    """把一段文字里的敏感数据换掉。顺序要紧：先整段的（私钥、邮箱），再长号码，最后手机号。"""
    if not text:
        return text
    for pattern in _SECRETS:
        def hide(m: re.Match[str]) -> str:
            vault.counts[SECRET_LABEL] += 1
            return m.group(1) + SECRET_MASK

        text = pattern.sub(hide, text)
    if policy.contacts:
        text = _EMAIL.sub(lambda m: vault.token("email", m.group(0)), text)

    def id_card(m: re.Match[str]) -> str:
        return vault.token("id_card", m.group(0)) if valid_id_card(m.group(0)) else m.group(0)

    text = _ID_CARD.sub(id_card, text)

    def bank(m: re.Match[str]) -> str:
        digits = re.sub(r"[ \-]", "", m.group(0))
        if 16 <= len(digits) <= 19 and valid_luhn(digits) and not valid_id_card(digits):
            return vault.token("bank_card", m.group(0))
        return m.group(0)

    text = _BANK.sub(bank, text)
    if policy.contacts:
        text = _PHONE.sub(lambda m: vault.token("phone", m.group(0)), text)
    return text


def _mask_content(content: Any, vault: Vault, policy: Policy) -> Any:
    if isinstance(content, str):
        return mask_text(content, vault, policy)
    if isinstance(content, list):
        out = []
        for part in content:
            if isinstance(part, dict) and part.get("type") == "text" and isinstance(part.get("text"), str):
                part = {**part, "text": mask_text(part["text"], vault, policy)}
            out.append(part)  # 图片等其他部分原样（图片里的号码正则管不到，见文档）
        return out
    return content


def mask_messages(messages: list[Any], vault: Vault, policy: Policy) -> list[Any]:
    """按顺序处理每条消息的正文、思考、工具调用参数。不改原来的列表（员工端的原值还要用来归档）。"""
    out: list[Any] = []
    for m in messages:
        if not isinstance(m, dict):
            out.append(m)
            continue
        m2 = dict(m)
        if "content" in m2:
            m2["content"] = _mask_content(m2["content"], vault, policy)
        if isinstance(m2.get("reasoning_content"), str):
            m2["reasoning_content"] = mask_text(m2["reasoning_content"], vault, policy)
        calls = m2.get("tool_calls")
        if isinstance(calls, list):
            new_calls = []
            for c in calls:
                fn = c.get("function") if isinstance(c, dict) else None
                if isinstance(fn, dict) and isinstance(fn.get("arguments"), str):
                    c = {**c, "function": {**fn, "arguments": mask_text(fn["arguments"], vault, policy)}}
                new_calls.append(c)
            m2["tool_calls"] = new_calls
        out.append(m2)
    return out


def with_note(messages: list[Any]) -> list[Any]:
    """告诉模型占位符是怎么回事：加在第一条系统消息后面（只在真有替换时加，前缀其余部分不变）。"""
    if messages and isinstance(messages[0], dict) and messages[0].get("role") == "system" and isinstance(messages[0].get("content"), str):
        first = {**messages[0], "content": messages[0]["content"] + "\n\n" + NOTE}
        return [first, *messages[1:]]
    return [{"role": "system", "content": NOTE}, *messages]


@dataclass
class Outcome:
    body: dict[str, Any]
    vault: Vault
    #: 拦截时给员工看的话；None 表示放行
    blocked: str | None = None


def describe(counts: Counter) -> str:
    names = {**LABELS, SECRET_LABEL: "密码或密钥"}
    return "、".join(f"{names[k]} {n} 处" for k, n in counts.items() if n)


def protect(body: dict[str, Any], policy: Policy) -> Outcome:
    """请求体 → 发给上游的请求体。没开、没找到时原样返回同一个对象。"""
    vault = Vault()
    if not policy.enabled or not isinstance(body.get("messages"), list):
        return Outcome(body, vault)
    messages = mask_messages(body["messages"], vault, policy)
    if vault.total == 0:
        return Outcome(body, vault)
    personal = Counter({k: n for k, n in vault.counts.items() if k != SECRET_LABEL})
    if policy.block and sum(personal.values()):
        return Outcome(body, vault, blocked=(
            f"要发给模型的内容里有{describe(personal)}，按公司规定不能发给外部模型，这次没有发送。"
            "请去掉这些内容（比如先删掉表格里的身份证、银行卡列）再试，或联系 IT。"
        ))
    if not vault.empty:
        messages = with_note(messages)
    return Outcome({**body, "messages": messages}, vault)


def protect_texts(texts: list[str], policy: Policy) -> tuple[list[str], Vault]:
    """向量（记忆检索）：只替换不还原（向量本身不含原文）。"""
    vault = Vault()
    if not policy.enabled:
        return texts, vault
    return [mask_text(t, vault, policy) for t in texts], vault


# ---------- 还原 ----------


def restore_text(text: str, vault: Vault, escape: Callable[[str], str] | None = None) -> str:
    if not text or vault.empty:
        return text

    def back(m: re.Match[str]) -> str:
        value = vault.by_token.get(f"{m.group(1)}_{m.group(2)}")
        if value is None:
            return m.group(0)
        return escape(value) if escape else value

    return PLACEHOLDER.sub(back, text)


def _json_escape(value: str) -> str:
    """工具调用参数是 JSON 文本，原值放进 JSON 字符串里要转义（邮箱、带引号的值）。"""
    return json.dumps(value, ensure_ascii=False)[1:-1]


def restore_payload(payload: Any, vault: Vault) -> Any:
    """非流式回答：正文、思考、工具调用参数里的占位符换回原值。"""
    if vault.empty or not isinstance(payload, dict):
        return payload
    for choice in payload.get("choices") or []:
        msg = choice.get("message") if isinstance(choice, dict) else None
        if not isinstance(msg, dict):
            continue
        for key in ("content", "reasoning_content"):
            if isinstance(msg.get(key), str):
                msg[key] = restore_text(msg[key], vault)
        for call in msg.get("tool_calls") or []:
            fn = call.get("function") if isinstance(call, dict) else None
            if isinstance(fn, dict) and isinstance(fn.get("arguments"), str):
                fn["arguments"] = restore_text(fn["arguments"], vault, _json_escape)
    return payload


def _split_safe(text: str) -> tuple[str, str]:
    """流式：最后那一截如果可能是还没收完的占位符（[身份 …），先留着，等下一段。"""
    cut = max(text.rfind("["), text.rfind("【"))
    if cut >= 0 and len(text) - cut <= _MAX_PLACEHOLDER and not any(c in text[cut:] for c in "]】"):
        return text[:cut], text[cut:]
    return text, ""


class StreamRestorer:
    """
    流式回答的还原：按 SSE 事件解析，每个字段（第几个回答的正文 / 思考 / 第几个工具调用的参数）各自缓冲，
    占位符被拆在两段里也能拼回来再换。没有替换过（对照表为空）时不用这个类，原样转发。
    """

    def __init__(self, vault: Vault) -> None:
        self.vault = vault
        self._buf = b""
        self._pending: dict[tuple, str] = {}

    def feed(self, chunk: bytes) -> bytes:
        self._buf += chunk
        out: list[bytes] = []
        while True:
            idx, sep = self._event_end(self._buf)
            if idx < 0:
                break
            event, self._buf = self._buf[:idx], self._buf[idx + sep:]
            out.append(self._event(event) + b"\n\n")
        return b"".join(out)

    def finish(self) -> bytes:
        """流结束：剩下的半截事件原样给出，还压着的文字补一个事件发出去。"""
        out = b""
        if self._buf.strip():
            out += self._event(self._buf) + b"\n\n"
            self._buf = b""
        tail = self._flush_all()
        if tail:
            out += b"data: " + json.dumps(tail, ensure_ascii=False, separators=(",", ":")).encode() + b"\n\n"
        return out

    @staticmethod
    def _event_end(buf: bytes) -> tuple[int, int]:
        a, b = buf.find(b"\n\n"), buf.find(b"\r\n\r\n")
        if a < 0 and b < 0:
            return -1, 0
        if b >= 0 and (a < 0 or b < a):
            return b, 4
        return a, 2

    def _event(self, event: bytes) -> bytes:
        lines = event.split(b"\n")
        result = []
        for line in lines:
            stripped = line.rstrip(b"\r")
            if not stripped.startswith(b"data:"):
                result.append(line)
                continue
            data = stripped[5:].strip()
            if data == b"[DONE]":
                tail = self._flush_all()
                if tail:
                    result.append(b"data: " + json.dumps(tail, ensure_ascii=False, separators=(",", ":")).encode())
                    result.append(b"")
                result.append(line)
                continue
            try:
                obj = json.loads(data)
            except ValueError:
                result.append(line)
                continue
            self._rewrite(obj)
            result.append(b"data: " + json.dumps(obj, ensure_ascii=False, separators=(",", ":")).encode())
        return b"\n".join(result)

    def _take(self, key: tuple, fragment: str, escape: Callable[[str], str] | None) -> str:
        text = self._pending.pop(key, "") + fragment
        ready, hold = _split_safe(text)
        if hold:
            self._pending[key] = hold
        return restore_text(ready, self.vault, escape)

    def _rewrite(self, obj: Any) -> None:
        if not isinstance(obj, dict):
            return
        for choice in obj.get("choices") or []:
            if not isinstance(choice, dict):
                continue
            i = choice.get("index", 0)
            delta = choice.get("delta")
            if isinstance(delta, dict):
                for key in ("content", "reasoning_content"):
                    if isinstance(delta.get(key), str):
                        delta[key] = self._take((i, key), delta[key], None)
                for call in delta.get("tool_calls") or []:
                    fn = call.get("function") if isinstance(call, dict) else None
                    if isinstance(fn, dict) and isinstance(fn.get("arguments"), str):
                        fn["arguments"] = self._take((i, "tool", call.get("index", 0)), fn["arguments"], _json_escape)
            if choice.get("finish_reason") is not None:
                # 这个回答结束了：压着的半截一起放出去（多半不是占位符，只是正好以 [ 结尾）
                if not isinstance(delta, dict):
                    delta = choice["delta"] = {}
                self._flush_choice(i, delta)

    def _flush_choice(self, i: Any, delta: dict) -> None:
        for key in [k for k in self._pending if k[0] == i]:
            text = restore_text(self._pending.pop(key), self.vault, _json_escape if key[1] == "tool" else None)
            if key[1] == "tool":
                calls = delta.setdefault("tool_calls", [])
                calls.append({"index": key[2], "function": {"arguments": text}})
            else:
                delta[key[1]] = (delta.get(key[1]) or "") + text

    def _flush_all(self) -> dict | None:
        if not self._pending:
            return None
        choices: dict[Any, dict] = {}
        for key in list(self._pending):
            choices.setdefault(key[0], {})
        out = []
        for i, delta in choices.items():
            self._flush_choice(i, delta)
            out.append({"index": i, "delta": delta, "finish_reason": None})
        return {"choices": out}

