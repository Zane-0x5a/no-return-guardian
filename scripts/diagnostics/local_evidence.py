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


def verified_executable():
    """The verified build's executable: the copy kept under artifacts/, otherwise game_executable()."""
    kept = evidence('verified-build-20250413/tlou-ii.exe')
    return kept if kept.is_file() else game_executable()


def build_executables(name):
    """The executables of a later build (NativeCodeSignature.BUILDS) kept under artifacts/build-<name>/."""
    return sorted(path for path in evidence(f'build-{name}').glob('tlou-ii.*') if path.suffix in ('.exe', '.rne'))
