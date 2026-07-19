from __future__ import annotations

from . import __version__, views


_original_layout = views.layout


def layout(*args, **kwargs) -> bytes:
    content = _original_layout(*args, **kwargs)
    version = __version__.encode("utf-8")
    return (
        content.replace(b'/static/app.css"', b'/static/app.css?v=' + version + b'"')
        .replace(b'/static/app.js"', b'/static/app.js?v=' + version + b'"')
    )


def install() -> None:
    views.layout = layout
