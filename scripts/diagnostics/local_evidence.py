"""Local evidence for tests that replay real game sessions.

Transcripts, snapshot stores and runtime logs under artifacts/ come from a player's own machine and
contain their saves, so they are not part of the repository. Tests that replay them skip when they
are missing instead of failing.
"""
import json
import os
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]


def evidence(relative):
    return ROOT / 'artifacts' / relative


def requires(*relative):
    missing = [item for item in relative if not evidence(item).exists()]
    return unittest.skipIf(bool(missing), 'local evidence missing: ' + ', '.join(missing))


def game_executable():
    """NRG_GAME_EXE, otherwise the executable Guardian learned from the running game."""
    configured = os.environ.get('NRG_GAME_EXE')
    if configured:
        return Path(configured)
    settings = Path(os.environ.get('LOCALAPPDATA', '')) / 'NoReturnGuardian' / 'settings.json'
    try:
        learned = json.loads(settings.read_text(encoding='utf-8-sig')).get('GameExecutablePath')
    except (OSError, ValueError):
        return None
    return Path(learned) if learned else None
