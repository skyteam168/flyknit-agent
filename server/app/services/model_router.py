"""根据场景选择模型，并把请求转发到上游 OpenAI 兼容接口。"""

from dataclasses import dataclass

import httpx
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession
from sqlalchemy.orm import joinedload

from ..crypto import decrypt
from ..models import ModelConfig, RouteRule
from ..schemas import SCENES

# 这些状态码视为“上游不可用”，会切换到备用模型
RETRYABLE_STATUS = {408, 429, 500, 502, 503, 504}


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


def _to_target(m: ModelConfig) -> Target:
    base = m.provider.base_url.rstrip("/")
    return Target(
        model_id=m.id,
        display_name=m.name,
        upstream_model=m.model,
        url=f"{base}/chat/completions",
        api_key=decrypt(m.provider.api_key_enc),
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


async def resolve(session: AsyncSession, scene: str, model_id: int | None = None) -> list[Target]:
    """返回按优先级排列的候选模型（主模型、备用模型）。chat 场景未配置时回退到 agent，反之亦然。

    model_id：用户在客户端选择的模型，可用时排在最前，场景配置的模型作为备用。
    """
    if scene not in SCENES:
        raise NoRouteError(f"未知场景：{scene}")

    if model_id is not None:
        chosen = await load_model(session, model_id)
        if _usable(chosen):
            targets = [_to_target(chosen)]
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
            if _usable(m):
                targets.append(_to_target(m))
        if targets:
            return targets
    raise NoRouteError(f"场景 {scene} 尚未配置可用模型，请在管理后台配置路由")


def build_body(body: dict, target: Target) -> dict:
    out = {k: v for k, v in body.items() if k not in ("scene", "flyknit_model_id")}
    for k, v in target.extra_body.items():
        out.setdefault(k, v)
    out["model"] = target.upstream_model
    return out


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
