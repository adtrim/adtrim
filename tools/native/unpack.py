import hashlib
import json
import pathlib
import tarfile

root = pathlib.Path(__file__).resolve().parent
sources = root / 'src'
sources.mkdir(exist_ok=True)
for entry in json.loads((root / 'sources.lock.json').read_text()):
    archive = root / 'archives' / entry['file']
    assert hashlib.sha256(archive.read_bytes()).hexdigest() == entry['sha256'], entry['name']
    dest = sources / entry['name']
    if dest.exists():
        continue
    dest.mkdir()
    with tarfile.open(archive) as tar:
        members = tar.getmembers()
        roots = {pathlib.PurePosixPath(m.name).parts[0] for m in members}
        strip = len(roots) == 1 and any('/' in m.name for m in members)
        for member in members:
            if strip:
                member.name = '/'.join(pathlib.PurePosixPath(member.name).parts[1:])
            if member.name:
                tar.extract(member, dest, filter='data')
print('Source archives verified and unpacked')
