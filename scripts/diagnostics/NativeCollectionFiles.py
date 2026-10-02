"""Read-only disk version witnesses, not coherent preparation snapshots."""

import hashlib
import os
import time

from NativeCheckpointProbe import ProbeError


FILES = tuple(['gamedata/' + name for name in
               ('R0A.save', 'R0A.save-backup', '0P.save', '0P.save-backup', 'nr.bin')] +
              ['savedata/' + slot + '/' + name for slot in ('SAVEFILER0A', 'SAVEFILE0P')
               for name in ('USR-DATA', 'params.json', 'ICN-ID')])


class FileWitness:
    def __init__(self, profile, directory, record):
        self.profile = profile.resolve(strict=True)
        self.directory = directory
        directory.mkdir(exist_ok=False)
        self.record = record
        self.previous = None
        self.next_probe = 0
        self.versions = 0
        self.retained_bytes = 0

    def _metadata(self):
        result = []
        for relative in FILES:
            path = self.profile / relative
            if not path.resolve().is_relative_to(self.profile):
                raise ProbeError('Save witness source escapes selected profile')
            try:
                stat = path.stat()
                if not 0 <= stat.st_size <= 16 * 1024 * 1024:
                    raise ProbeError('Save witness source size exceeds bound')
                result.append((relative, stat.st_size, stat.st_mtime_ns))
            except FileNotFoundError:
                result.append((relative, None, None))
        return result

    def poll(self, force=False):
        now = time.monotonic()
        if not force and now < self.next_probe:
            return
        self.next_probe = now + 1
        before = self._metadata()
        if before == self.previous:
            return
        payloads = {}
        try:
            for relative, size, modified in before:
                if size is not None:
                    with (self.profile / relative).open('rb') as source:
                        data = source.read(16 * 1024 * 1024 + 1)
                    if len(data) != size:
                        raise ValueError('Source changed size')
                    payloads[relative] = data
            if before != self._metadata():
                raise ValueError('Source metadata changed')
            for relative, data in payloads.items():
                with (self.profile / relative).open('rb') as source:
                    if source.read(len(data) + 1) != data:
                        raise ValueError('Source bytes changed')
            if before != self._metadata():
                raise ValueError('Source metadata changed after second pass')
        except (FileNotFoundError, PermissionError, ValueError) as error:
            self.record({'kind': 'collection-files-changing', 'reason': str(error), 'restorable': False})
            return
        if self.versions >= 256:
            raise ProbeError('Save witness version budget exceeded')
        records = []
        for relative, size, modified in before:
            if size is None:
                records.append({'path': relative, 'absent': True})
                continue
            data = payloads[relative]
            digest = hashlib.sha256(data).hexdigest()
            filename = digest + '.bin'
            destination = self.directory / filename
            if destination.exists():
                if destination.is_symlink() or destination.read_bytes() != data:
                    raise ProbeError('Save witness content collision or corruption')
            else:
                if self.retained_bytes + len(data) > 512 * 1024 * 1024:
                    raise ProbeError('Save witness storage budget exceeded')
                with destination.open('xb') as output:
                    output.write(data)
                    output.flush()
                    os.fsync(output.fileno())
                self.retained_bytes += len(data)
            if hashlib.sha256(destination.read_bytes()).hexdigest() != digest:
                raise ProbeError('Save witness write verification failed')
            records.append({'path': relative, 'size': size, 'modifiedNs': modified,
                            'file': filename, 'sha256': digest})
        self.previous = before
        self.versions += 1
        self.record({'kind': 'collection-files', 'time': time.time(), 'version': self.versions,
                     'files': records, 'twoPassEqual': True, 'restorable': False,
                     'coherentSnapshot': False, 'source': str(self.profile)})
