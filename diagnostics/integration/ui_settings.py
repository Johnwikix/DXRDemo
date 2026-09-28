"""Exercise the real controls with lvt (no private renderer API)."""
import json
import subprocess
import sys
import time

lvt = r'G:\Tool\lvt\lvt.exe'
main, settings, model, denoiser, hdr = sys.argv[1:6]

def run(hwnd, *args):
    proc = subprocess.run([lvt, *args, '--hwnd', hwnd], capture_output=True, encoding='utf-8')
    if proc.returncode:
        raise RuntimeError(proc.stderr + proc.stdout)
    result = json.loads(proc.stdout) if proc.stdout.strip().startswith('{') else {}
    if result.get('ok') is False:
        raise RuntimeError(proc.stdout)
    return result

def nodes(node):
    yield node
    for child in node.get('children', []): yield from nodes(child)

def find(hwnd, automation_id):
    for attempt in range(10):
        tree = run(hwnd, 'dump', '--uia', '--format', 'json')
        for node in nodes(tree['root']):
            if node.get('properties', {}).get('AutomationId') == automation_id:
                return node
        time.sleep(0.1)
    raise RuntimeError('Control not found: ' + automation_id)

def ref(node): return 'uia:' + node['properties']['RuntimeId']

run(main, 'click', ref(find(main, 'OpenSettingsButton')))
for aid, index in [('ModelSelector', int(model)), ('DenoiserSelector', int(denoiser))]:
    run(settings, 'focus', ref(find(settings, aid)))
    run(settings, 'press-key', ';'.join(['Home'] + ['Down'] * index + ['Enter']))
node = find(settings, 'HdrToggle')
if node['properties']['Toggle.ToggleState'] != ('On' if hdr == '1' else 'Off'):
    run(settings, 'toggle', ref(node))
run(settings, 'close')
print(json.dumps(dict(main=main, model=int(model), denoiser=int(denoiser), hdr=hdr == '1')))
