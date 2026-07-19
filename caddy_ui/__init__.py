"""Caddy UI application package."""

from pathlib import Path


__version__ = Path(__file__).with_name("VERSION").read_text(encoding="utf-8").strip()

# Keep public imports stable while small query/preset extensions stay isolated
# from the persistence and Caddy rendering cores.
from .analytics_status import install as _install_analytics_status
from .protection_presets import install as _install_protection_presets

_install_analytics_status()
_install_protection_presets()
del _install_analytics_status
del _install_protection_presets
