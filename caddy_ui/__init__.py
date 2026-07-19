"""Caddy UI application package."""

from pathlib import Path


__version__ = Path(__file__).with_name("VERSION").read_text(encoding="utf-8").strip()

# Keep the public analytics repository import stable while feature-specific query
# semantics remain isolated from the persistence core.
from .analytics_status import install as _install_analytics_status

_install_analytics_status()
del _install_analytics_status
