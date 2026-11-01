"""根据场景选择模型，并把请求转发到上游 OpenAI 兼容接口。"""

import logging
import re
from dataclasses import dataclass

import httpx
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession
from sqlalchemy.orm import joinedload

from ..crypto import decrypt
from ..models import ModelConfig, RouteRule
from ..schemas import SCENES

log = logging.getLogger("flyknit.router")

# 这些状态码视为“上游不可用”，会切换到备用模型
RETRYABLE_STATUS = {408, 429, 500, 502, 503, 504}

# 向量、语音、图像生成等非对话模型（按上游模型名判断）：可以配到场景路由，但不出现在员工端的对话模型列表里
NON_CHAT_MODEL = re.compile(
    r"embed|rerank|tts|asr|whisper|audio|speech|paraformer|sensevoice|cosyvoice|sambert|wanx|wan2|image|flux"
    r"|stable-diffusion|video|realtime|ocr|moderation|livetranslate|captioner|character",
    re.I,
)


@dataclass(frozen=True)
class Target:
    model_id: int
    display_name: str
    upstream_model: str
    url: str
    api_key: str
    extra_body: dict
    supports_vision: bool
    context_length: int = 131072


class NoRouteError(Exception):
    pass


def _to_target(m: ModelConfig) -> Target | None:
    """密钥解不开（FLYKNIT_SECRET_KEY 改过）就返回 None，调用方跳过这个模型。"""
    base = m.provider.base_url.rstrip("/")
    try:
        api_key = decrypt(m.provider.api_key_enc)
    except ValueError:
        log.error("模型提供方「%s」(#%s) 的密钥无法解密，已跳过；请在管理后台重新填写", m.provider.name, m.provider.id)
        return None
    return Target(
        model_id=m.id,
        display_name=m.name,
        upstream_model=m.model,
        url=f"{base}/chat/completions",
        api_key=api_key,
        extra_body=dict(m.extra_body or {}),
        supports_vision=m.supports_vision,
        context_length=m.context_length or 131072,
    )


def _usable(m: ModelConfig | None) -> bool:
    return m is not None and m.enabled and m.provider.enabled


async def load_model(session: AsyncSession, model_id: int | None) -> ModelConfig | None:
    if model_id is None:
        return None
    return await session.scalar(
        select(ModelConfig).options(joinedload(ModelConfig.provider)).where(ModelConfig.id == model_id)
    )


def has_image(messages: list) -> bool:
    for msg in messages or []:
        content = msg.get("content") if isinstance(msg, dict) else None
        if isinstance(content, list):
            for part in content:
                if isinstance(part, dict) and part.get("type") == "image_url":
                    return True
    return False


# 手机端默认优先：快模型。名字或上游型号对得上就用它，避免路由到会长时间思考的大模型
PREFERRED_MOBILE = re.compile(r"deepseek-v4\.?1-flash|deepseek.*flash", re.I)


async def list_chat_models(session: AsyncSession) -> list[ModelConfig]:
    """员工 / 手机可选的对话模型（启用、非向量语音类）。"""
    rows = (
        await session.scalars(
            select(ModelConfig).options(joinedload(ModelConfig.provider)).order_by(ModelConfig.provider_id, ModelConfig.name)
        )
    ).unique().all()
    return [m for m in rows if _usable(m) and not NON_CHAT_MODEL.search(m.model)]


async def prefer_mobile(session: AsyncSession, scene: str, model_id: int | None = None) -> list[Target]:
    """手机选模型：指定了就用指定的；否则优先 deepseek flash，再退回场景路由。"""
    if model_id is not None:
        return await resolve(session, scene, model_id)
    for m in await list_chat_models(session):
        if PREFERRED_MOBILE.search(m.model) or PREFERRED_MOBILE.search(m.name):
            return await resolve(session, scene, m.id)
    return await resolve(session, scene)


async def resolve(session: AsyncSession, scene: str, model_id: int | None = None) -> list[Target]:
    """返回按优先级排列的候选模型（主模型、备用模型）。chat 场景未配置时回退到 agent，反之亦然。

    model_id：用户在客户端选择的模型，可用时排在最前，场景配置的模型作为备用。
    """
    if scene not in SCENES:
        raise NoRouteError(f"未知场景：{scene}")

    if model_id is not None:
        chosen = await load_model(session, model_id)
        first = _to_target(chosen) if _usable(chosen) else None
        if first is not None:
            targets = [first]
            try:
                for t in await resolve(session, scene):
                    if t.model_id != chosen.id and len(targets) < 2:
                        targets.append(t)
            except NoRouteError:
                pass
            return targets

    order = [scene]
    if scene in ("translate", "title", "vision"):
        order.append("chat")
    if scene == "chat":
        order.append("agent")
    if scene == "agent":
        order.append("chat")

    for s in order:
        rule = await session.get(RouteRule, s)
        if rule is None:
            continue
        targets: list[Target] = []
        for mid in (rule.model_id, rule.fallback_model_id):
            m = await load_model(session, mid)
            if _usable(m) and (t := _to_target(m)) is not None:
                targets.append(t)
        if targets:
            return targets
    raise NoRouteError(f"场景 {scene} 尚未配置可用模型，请在管理后台配置路由")


def build_body(body: dict, target: Target) -> dict:
    # flyknit_* 都是给网关自己看的（选模型、子任务 ID……），上游不认，有的还会因为多出来的字段报 400
    out = {k: v for k, v in body.items() if k != "scene" and not k.startswith("flyknit_")}
    for k, v in target.extra_body.items():
        out.setdefault(k, v)
    out["model"] = target.upstream_model
    return out


def embeddings_url(target: Target) -> str:
    """同一个服务的 /embeddings 地址（Target.url 是 /chat/completions）。"""
    return target.url.removesuffix("/chat/completions") + "/embeddings"


def headers_for(target: Target) -> dict:
    h = {"Content-Type": "application/json"}
    if target.api_key:
        h["Authorization"] = f"Bearer {target.api_key}"
    return h


async def open_upstream(
    client: httpx.AsyncClient, targets: list[Target], body: dict
) -> tuple[httpx.Response, Target]:
    """依次尝试候选模型，返回第一个可用的（已开始的）流式响应。

    只有在尚未收到任何输出时才会切换备用模型，因此不会出现半截回答拼接的问题。
    """
    last_error: Exception | None = None
    for i, target in enumerate(targets):
        is_last = i == len(targets) - 1
        req = client.build_request("POST", target.url, json=build_body(body, target), headers=headers_for(target))
        try:
            resp = await client.send(req, stream=True)
        except httpx.TransportError as exc:
            last_error = exc
            continue
        if resp.status_code in RETRYABLE_STATUS and not is_last:
            await resp.aclose()
            continue
        return resp, target
    raise UpstreamUnavailable(str(last_error) if last_error else "上游模型不可用")


class UpstreamUnavailable(Exception):
    pass
