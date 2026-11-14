#!/usr/bin/env python3
"""Bundle pinned Ubuntu 24.04 terminal packages, notices and corresponding source.

Hashes in TerminalPackages.json come from Ubuntu's signed noble/updates/security
APT indexes. No package scripts, privileged helpers or system installs are used.
"""
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import urllib.request


def bundle(destination):
    manifest = Path(__file__).with_name('TerminalPackages.json')
    metadata = json.loads(manifest.read_text())
    terminal = destination / 'terminal'
    terminal.mkdir(parents=True, exist_ok=True)
    notices = terminal / 'licenses'
    notices.mkdir(exist_ok=True)
    sources = terminal / 'sources'
    sources.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='mgremote-terminal-') as temporary:
        temporary = Path(temporary)

        def download(item, directory):
            path = directory / Path(item['Filename']).name
            urllib.request.urlretrieve('https://archive.ubuntu.com/ubuntu/' + item['Filename'], path)
            with path.open('rb') as stream:
                actual = hashlib.file_digest(stream, 'sha256').hexdigest()
            if actual != item['SHA256']:
                raise RuntimeError('Terminal package checksum mismatch: ' + path.name)
            return path

        for package in metadata['Packages']:
            archive = download(package, temporary)
            extracted = temporary / package['Package']
            subprocess.run(['dpkg-deb', '--extract', str(archive), str(extracted)], check=True)
            if package['Package'] == 'xterm':
                shutil.copy2(extracted / 'usr/bin/xterm', terminal / 'xterm')
                (terminal / 'xterm').chmod(0o755)  # Never preserve privileged mode bits.
            else:
                for library in (extracted / 'usr/lib/x86_64-linux-gnu').glob('*.so.*'):
                    shutil.copyfile(library.resolve(strict=True), terminal / library.name)
                    (terminal / library.name).chmod(0o644)
            shutil.copyfile(extracted / 'usr/share/doc' / package['Package'] / 'copyright', notices / (package['Package'] + '.copyright'))
        for source in metadata['Sources']:
            for item in source['Files']:
                download(item, sources)
    shutil.copyfile(manifest, terminal / manifest.name)
    print('Bundled xterm with pinned libraries, licenses and corresponding source.')


if __name__ == '__main__':
    bundle(Path(sys.argv[1]))
