"""Publish the validated artifact from this workflow run; no personal credentials."""
import os, json, pathlib, hashlib, urllib.request, urllib.error, urllib.parse, zipfile, io, base64, time

repo = os.environ['GITHUB_REPOSITORY']
run_id = os.environ.get('VALIDATED_RUN_ID') or os.environ['GITHUB_RUN_ID']
token = os.environ['GH_TOKEN']
version = '0.2.5'
tag = 'linux-v' + version
base = 'https://api.github.com/repos/' + repo

class SafeRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        redirected = super().redirect_request(req, fp, code, msg, headers, newurl)
        if redirected and urllib.parse.urlsplit(req.full_url).netloc != urllib.parse.urlsplit(newurl).netloc:
            redirected.remove_header('Authorization')
        return redirected
opener = urllib.request.build_opener(SafeRedirect())

def api(path, method='GET', body=None, raw=False):
    url = path if path.startswith('https://') else base + path
    headers = {'Authorization': 'Bearer ' + token, 'User-Agent': 'MGRemote-release', 'Accept': 'application/vnd.github+json', 'X-GitHub-Api-Version': '2022-11-28'}
    if body is not None: headers['Content-Type'] = 'application/json'
    request = urllib.request.Request(url, data=None if body is None else json.dumps(body).encode(), headers=headers, method=method)
    try:
        with opener.open(request, timeout=120) as response: data = response.read()
    except urllib.error.HTTPError as error:
        if error.code != 404:
            try: message = json.loads(error.read()).get('message', 'GitHub rejected the request')
            except (ValueError, UnicodeError): message = 'GitHub rejected the request'
            print('GitHub API ' + method + ': ' + str(error.code) + ' - ' + message, flush=True)
        raise
    return data if raw else json.loads(data) if data else None

assert run_id.isdigit()
source_run = api('/actions/runs/' + run_id)
assert source_run['head_repository']['full_name'] == repo and source_run['head_branch'] == 'linux-desktop-parity' and source_run['event'] == 'workflow_dispatch', 'Artifact must come from the trusted development branch'
commit = source_run['head_sha']
for attempt in range(15):
    jobs = api('/actions/runs/' + run_id + '/jobs?filter=all&per_page=100')['jobs']
    if any(j['name'] == 'linux' and j['conclusion'] == 'success' for j in jobs): break
    time.sleep(2)
else: raise RuntimeError('Validation job must pass first')
artifact = next(a for a in api('/actions/runs/' + run_id + '/artifacts')['artifacts'] if a['name'] == 'mgremote-linux-validated')
assert not artifact['expired']
destination = pathlib.Path('validated-release')
destination.mkdir(exist_ok=True)
with zipfile.ZipFile(io.BytesIO(api(artifact['archive_download_url'], raw=True))) as archive:
    for info in archive.infolist():
        path = pathlib.PurePosixPath(info.filename)
        assert not path.is_absolute() and '..' not in path.parts and (info.external_attr >> 16) & 0o170000 != 0o120000, 'Unsafe artifact path'
    archive.extractall(destination)
manifest_path = next(destination.glob('linux/' + version + '/*/manifest.json'))
manifest = json.loads(manifest_path.read_text(encoding='utf-8-sig'))
assert manifest['Version'] == version and manifest['LinuxExecutionTested'] and manifest['SerialPtyTested']
image = manifest_path.parent / manifest['AppImage']
source = manifest_path.parent / manifest['Source']
assert hashlib.sha256(image.read_bytes()).hexdigest().upper() == manifest['SHA256']
assert hashlib.sha256(source.read_bytes()).hexdigest().upper() == manifest['SourceSHA256']
with zipfile.ZipFile(source) as archive:
    assert 'Linux/MainWindow.Desktop.cs' in {n.replace('\\', '/') for n in archive.namelist()}
proof = destination / ('linux-' + version + '-validation.json')
proof.write_text(json.dumps({'commit': commit, 'workflow': 'https://github.com/' + repo + '/actions/runs/' + run_id, 'offlineChecks': 102, 'embeddedX11ClipboardBidirectional': True, 'ownedExternalProcessCleanup': True, 'remoteRdpClipboardTested': False, 'physicalMultimonitorTested': False}, indent=2))
notes = '''## Linux Desktop 0.2.5 - Development preview

- Folder, Windows/RDP, Linux/SSH and serial vector icons, with configured login/domain labels.
- Working Help menu with an offline guide and About dialog.
- RDP multimonitor connects to a managed native fullscreen FreeRDP window; its MGRemote tab owns shutdown. Direct RDP can use an unlocked imported secret. Gateways/MFA remain manual.
- Embedded and fallback xterm SSH terminals share Ctrl+Shift+C/V. Files/folders use SFTP or explicit RDP folder sharing.

Validation: 102 offline Linux checks, actual X11 clipboard copying from the embedded terminal and pasting into its PTY with Ctrl+Shift+C/V, Help/About, tree actions, native/external process cleanup and serial PTY tests. This clipboard test uses a local fixture, not a remote SSH server.

RDP clipboard options are checked, but server redirection/policies, real remote authentication/transfers, physical multimonitor layouts, mixed DPI and hot-plug remain unverified. Unsigned development preview with incomplete Windows parity. Profile format and encrypted-secret restrictions are unchanged from 0.2.4. See the versioned Linux/README.md for requirements.
'''
notes += '\n[Ubuntu validation](https://github.com/' + repo + '/actions/runs/' + run_id + ')\n'
release = next((r for r in api('/releases?per_page=100') if r['tag_name'] == tag), None)
if release is None:
    release = api('/releases', 'POST', {'tag_name': tag, 'target_commitish': commit, 'name': 'MGRemote Linux Desktop ' + version + ' - Development preview', 'body': notes, 'draft': True, 'prerelease': True, 'make_latest': 'false'})
assert release['target_commitish'] == commit, 'Existing tag belongs to another build'
for path in [image, source, manifest_path, proof, destination / 'linux-gui.png']:
    digest = 'sha256:' + hashlib.sha256(path.read_bytes()).hexdigest()
    existing = next((a for a in release['assets'] if a['name'] == path.name), None)
    if existing:
        assert existing['digest'] == digest
        continue
    assert release['draft'], 'Published releases cannot be modified by this job'
    upload = release['upload_url'].split('{')[0] + '?name=' + urllib.parse.quote(path.name)
    request = urllib.request.Request(upload, data=path.read_bytes(), method='POST', headers={'Authorization': 'Bearer ' + token, 'User-Agent': 'MGRemote-release', 'Content-Type': 'application/octet-stream', 'Accept': 'application/vnd.github+json'})
    with opener.open(request, timeout=300) as response: asset = json.load(response)
    assert asset['size'] == path.stat().st_size and asset['digest'] == digest
release = api('/releases/' + str(release['id']), 'PATCH', {'draft': False, 'prerelease': True})
assert len(release['assets']) == 5 and not release['draft']
document = api('/contents/README.md?ref=main')
content = base64.b64decode(document['content']).decode('utf-8')
updated = content.replace('linux-v0.2.4', tag).replace('AppImage 0.2.4', 'AppImage ' + version).replace('MGRemote-Linux-0.2.4', 'MGRemote-Linux-' + version)
paragraph = 'Folder/Windows-RDP/Linux-SSH icons and configured usernames identify connections. Help includes an offline guide and About dialog. Multimonitor RDP opens a managed native fullscreen window. Local X11 clipboard copy/paste is tested; remote RDP clipboard and physical monitor behavior still require a server/hardware pilot.\n\n'
if paragraph not in updated: updated = updated.replace('## Linux desktop development preview\n\n', '## Linux desktop development preview\n\n' + paragraph, 1)
if updated != content: api('/contents/README.md', 'PUT', {'message': 'Update Linux downloads and desktop features for 0.2.5', 'sha': document['sha'], 'branch': 'main', 'content': base64.b64encode(updated.encode()).decode()})
try: old = api('/releases/tags/linux-v0.2.4')
except urllib.error.HTTPError as error:
    if error.code != 404: raise
else: api('/releases/' + str(old['id']), 'DELETE')
try: api('/git/refs/tags/linux-v0.2.4', 'DELETE')
except urllib.error.HTTPError as error:
    if error.code != 404: raise
assert {r['tag_name'] for r in api('/releases?per_page=100')} == {'windows-v0.1.15', tag}
assert {t['name'] for t in api('/tags?per_page=100')} == {'windows-v0.1.15', tag}
print('Published and verified: ' + release['html_url'])
