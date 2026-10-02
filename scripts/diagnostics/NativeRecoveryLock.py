"""Exclude concurrent recovery bridges before either can attach to a game."""

import ctypes
from ctypes import wintypes

from NativeCheckpointProbe import ProbeError


class RecoveryLock:
    def __init__(self, pid):
        self.kernel = ctypes.WinDLL('kernel32', use_last_error=True)
        self.kernel.CreateMutexW.argtypes = [ctypes.c_void_p, wintypes.BOOL, wintypes.LPCWSTR]
        self.kernel.CreateMutexW.restype = wintypes.HANDLE
        self.kernel.CloseHandle.argtypes = [wintypes.HANDLE]
        self.kernel.CloseHandle.restype = wintypes.BOOL
        self.handle = self.kernel.CreateMutexW(None, False, f'Local\\NoReturnGuardian.NativeRecovery.{pid}')
        error = ctypes.get_last_error()
        if not self.handle:
            raise ProbeError('Cannot establish native recovery exclusion')
        if error == 183:
            self.close()
            raise ProbeError('A native recovery already owns this game process')

    def close(self):
        if self.handle:
            self.kernel.CloseHandle(self.handle)
            self.handle = None
